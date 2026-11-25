// The SCS plugin: Telemetry SDK and semantical Input SDK glue around the pure logic in src/core.
//
// Rules for everything in this file (it runs inside the game process):
//  - every export and every SDK callback catches everything and returns a safe result; no exception reaches the game;
//  - SDK calls are main-thread only; the pipe worker never calls the SDK (its log lines go through log_ring);
//  - the game thread only takes the short shared lock, it never waits for I/O or for the worker;
//  - the input event callback never returns an error code other than "not found" (that would disconnect the device).

#include <windows.h>

#include <cstdio>
#include <cstring>

#include <amtrucks/scssdk_ats.h>
#include <eurotrucks2/scssdk_eut2.h>
#include <scssdk_input.h>
#include <scssdk_telemetry.h>

#include "bridge.h"
#include "log_ring.h"
#include "pipe_server.h"
#include "plugin_info.h"
#include "shared.h"
#include "tables.h"

namespace
{

using bbs::plugin::g_shared;
using bbs::plugin::LockGuard;
using bbs::plugin::now_ms;

// ---- lifecycle -------------------------------------------------------------------------------------------------
// The two APIs initialise and shut down in any order and can repeat per DLL load; the pipe worker runs while at least
// one of them is initialised.

SRWLOCK g_life_lock = SRWLOCK_INIT;
int g_refs = 0;
bool g_telemetry_inited = false;
bool g_input_inited = false;
scs_log_t g_telemetry_log = nullptr;
scs_log_t g_input_log = nullptr;

class LifeGuard
{
  public:
    LifeGuard() noexcept
    {
        AcquireSRWLockExclusive(&g_life_lock);
    }

    ~LifeGuard()
    {
        ReleaseSRWLockExclusive(&g_life_lock);
    }

    LifeGuard(const LifeGuard &) = delete;
    LifeGuard &operator=(const LifeGuard &) = delete;
};

scs_log_t game_log() noexcept
{
    return g_input_log != nullptr ? g_input_log : g_telemetry_log;
}

// Main thread: lines posted by the worker, then (optionally) one of our own.
void flush_log() noexcept
{
    if (bbs::plugin::log_pending())
    {
        bbs::plugin::log_flush(game_log());
    }
}

void log_now(scs_log_t sink, scs_log_type_t type, const char *text) noexcept
{
    bbs::plugin::log_post(type, text);
    bbs::plugin::log_flush(sink);
}

void life_acquire() noexcept
{
    LifeGuard guard;
    if (g_refs == 0 && !bbs::plugin::server_start())
    {
        bbs::plugin::log_post(SCS_LOG_TYPE_error, "cannot start the pipe worker, running without a host connection");
    }
    ++g_refs;
}

void life_release() noexcept
{
    LifeGuard guard;
    if (g_refs > 0 && --g_refs == 0)
    {
        bbs::plugin::server_stop();
    }
}

bool game_from_id(const char *id, bbs::Game &game) noexcept
{
    if (id == nullptr)
    {
        return false;
    }
    if (std::strcmp(id, SCS_GAME_ID_EUT2) == 0)
    {
        game = bbs::Game::Ets2;
        return true;
    }
    if (std::strcmp(id, SCS_GAME_ID_ATS) == 0)
    {
        game = bbs::Game::Ats;
        return true;
    }
    return false;
}

// Runs `change` on the bridge under the lock and wakes the worker if frames were queued.
template <typename Change> void game_call(Change &&change) noexcept
{
    bool wake = false;
    {
        LockGuard guard;
        change(g_shared.bridge);
        wake = g_shared.bridge.out_pending();
    }
    if (wake)
    {
        bbs::plugin::server_wake();
    }
}

// ---- telemetry -------------------------------------------------------------------------------------------------

scs_value_type_t sdk_type(bbs::Mapping mapping) noexcept
{
    switch (mapping)
    {
    case bbs::Mapping::Bool:
        return SCS_VALUE_TYPE_bool;
    case bbs::Mapping::U32:
        return SCS_VALUE_TYPE_u32;
    case bbs::Mapping::Positive:
        return SCS_VALUE_TYPE_float;
    }
    return SCS_VALUE_TYPE_INVALID;
}

SCSAPI_VOID channel_callback(const scs_string_t, const scs_u32_t, const scs_value_t *const value,
                             const scs_context_t context)
{
    try
    {
        const size_t channel = reinterpret_cast<size_t>(context);
        if (channel >= bbs::kChannelCount)
        {
            return;
        }
        // The channels are registered with the "no value" flag: NULL means the game has no value (no truck).
        game_call([&](bbs::Bridge &bridge) {
            if (value == nullptr)
            {
                bridge.on_channel_none(channel);
                return;
            }
            switch (bbs::kChannels[channel].mapping)
            {
            case bbs::Mapping::Bool:
                if (value->type == SCS_VALUE_TYPE_bool)
                {
                    bridge.on_channel_bool(channel, value->value_bool.value != 0);
                }
                break;
            case bbs::Mapping::U32:
                if (value->type == SCS_VALUE_TYPE_u32)
                {
                    bridge.on_channel_u32(channel, value->value_u32.value);
                }
                break;
            case bbs::Mapping::Positive:
                if (value->type == SCS_VALUE_TYPE_float)
                {
                    bridge.on_channel_float(channel, value->value_float.value);
                }
                break;
            }
        });
    }
    catch (...)
    {
    }
}

SCSAPI_VOID event_callback(const scs_event_t event, const void *const, const scs_context_t)
{
    try
    {
        switch (event)
        {
        case SCS_TELEMETRY_EVENT_frame_end:
            game_call([](bbs::Bridge &bridge) { bridge.on_frame_end(now_ms()); });
            break;
        case SCS_TELEMETRY_EVENT_paused:
            game_call([](bbs::Bridge &bridge) { bridge.on_paused(now_ms()); });
            break;
        case SCS_TELEMETRY_EVENT_started:
            game_call([](bbs::Bridge &bridge) { bridge.on_started(now_ms()); });
            break;
        default:
            break;
        }
        flush_log();
    }
    catch (...)
    {
    }
}

void telemetry_shutdown_impl() noexcept
{
    try
    {
        game_call([](bbs::Bridge &bridge) { bridge.reset_telemetry(now_ms()); });
        const scs_log_t sink = game_log();
        bbs::plugin::log_post(SCS_LOG_TYPE_message, "telemetry shut down");
        bbs::plugin::log_flush(sink);
        g_telemetry_log = nullptr;
        if (g_telemetry_inited)
        {
            g_telemetry_inited = false;
            life_release();
        }
    }
    catch (...)
    {
    }
}

// ---- input -----------------------------------------------------------------------------------------------------

SCSAPI_RESULT input_event_callback(scs_input_event_t *const event_info, const scs_u32_t flags, const scs_context_t)
{
    try
    {
        size_t index = 0;
        bool value = false;
        bool found = false;
        game_call([&](bbs::Bridge &bridge) {
            if ((flags & SCS_INPUT_EVENT_CALLBACK_FLAG_first_after_activation) != 0)
            {
                bridge.on_device_activated(now_ms());
            }
            if ((flags & SCS_INPUT_EVENT_CALLBACK_FLAG_first_in_frame) != 0)
            {
                bridge.on_poll_frame(now_ms());
            }
            found = bridge.next_input_event(index, value);
        });
        if ((flags & SCS_INPUT_EVENT_CALLBACK_FLAG_first_in_frame) != 0)
        {
            flush_log();
        }
        if (!found || event_info == nullptr)
        {
            return SCS_RESULT_not_found;
        }
        std::memset(event_info, 0, sizeof *event_info);
        event_info->input_index = static_cast<scs_u32_t>(index);
        event_info->value_bool.value = value ? 1 : 0;
        return SCS_RESULT_ok;
    }
    catch (...)
    {
        return SCS_RESULT_not_found; // never an error code: that would disconnect the device for good
    }
}

void input_shutdown_impl() noexcept
{
    try
    {
        game_call([](bbs::Bridge &bridge) { bridge.reset_input(now_ms()); });
        const scs_log_t sink = game_log();
        bbs::plugin::log_post(SCS_LOG_TYPE_message, "input shut down");
        bbs::plugin::log_flush(sink);
        g_input_log = nullptr;
        if (g_input_inited)
        {
            g_input_inited = false;
            life_release();
        }
    }
    catch (...)
    {
    }
}

} // namespace

// ---- exports (listed in exports.def; exactly these four) -------------------------------------------------------

SCSAPI_RESULT scs_telemetry_init(const scs_u32_t version, const scs_telemetry_init_params_t *const params)
{
    try
    {
        if (version != SCS_TELEMETRY_VERSION_1_00 && version != SCS_TELEMETRY_VERSION_1_01)
        {
            return SCS_RESULT_unsupported;
        }
        if (params == nullptr)
        {
            return SCS_RESULT_invalid_parameter;
        }
        const auto *p = static_cast<const scs_telemetry_init_params_v101_t *>(params);
        bbs::Game game = bbs::Game::Ets2;
        if (!game_from_id(p->common.game_id, game))
        {
            if (p->common.log != nullptr)
            {
                p->common.log(SCS_LOG_TYPE_warning, "[button_box] unsupported game, telemetry not used");
            }
            return SCS_RESULT_unsupported;
        }
        if (g_telemetry_inited)
        {
            telemetry_shutdown_impl(); // init without shutdown: start clean
        }
        g_telemetry_log = p->common.log;
        game_call([&](bbs::Bridge &bridge) {
            bridge.set_game(game);
            bridge.reset_telemetry(now_ms());
        });

        const scs_event_t events[] = {SCS_TELEMETRY_EVENT_frame_end, SCS_TELEMETRY_EVENT_paused,
                                      SCS_TELEMETRY_EVENT_started};
        bool events_ok = true;
        for (const scs_event_t event : events)
        {
            events_ok = p->register_for_event(event, event_callback, nullptr) == SCS_RESULT_ok && events_ok;
        }
        if (!events_ok)
        {
            log_now(g_telemetry_log, SCS_LOG_TYPE_error, "cannot register the telemetry events");
            g_telemetry_log = nullptr;
            return SCS_RESULT_generic_error;
        }
        for (size_t i = 0; i < bbs::kChannelCount; ++i)
        {
            const scs_result_t result = p->register_for_channel(
                bbs::kChannels[i].name, SCS_U32_NIL, sdk_type(bbs::kChannels[i].mapping),
                SCS_TELEMETRY_CHANNEL_FLAG_no_value, channel_callback, reinterpret_cast<scs_context_t>(i));
            if (result != SCS_RESULT_ok)
            {
                char text[160];
                std::snprintf(text, sizeof text, "cannot register channel %s (%d)", bbs::kChannels[i].name,
                              static_cast<int>(result));
                bbs::plugin::log_post(SCS_LOG_TYPE_warning, text);
            }
        }
        g_telemetry_inited = true;
        life_acquire();
        char text[128];
        std::snprintf(text, sizeof text, "telemetry ready (%s, plugin %s, game %s)", p->common.game_id,
                      bbs::kPluginVersion, bbs::game_text(game));
        log_now(g_telemetry_log, SCS_LOG_TYPE_message, text);
        return SCS_RESULT_ok;
    }
    catch (...)
    {
        return SCS_RESULT_generic_error;
    }
}

SCSAPI_VOID scs_telemetry_shutdown(void)
{
    try
    {
        telemetry_shutdown_impl();
    }
    catch (...)
    {
    }
}

SCSAPI_RESULT scs_input_init(const scs_u32_t version, const scs_input_init_params_t *const params)
{
    try
    {
        if (version != SCS_INPUT_VERSION_1_00)
        {
            return SCS_RESULT_unsupported;
        }
        if (params == nullptr)
        {
            return SCS_RESULT_invalid_parameter;
        }
        const auto *p = static_cast<const scs_input_init_params_v100_t *>(params);
        bbs::Game game = bbs::Game::Ets2;
        if (!game_from_id(p->common.game_id, game))
        {
            if (p->common.log != nullptr)
            {
                p->common.log(SCS_LOG_TYPE_warning, "[button_box] unsupported game, input not used");
            }
            return SCS_RESULT_unsupported;
        }
        if (g_input_inited)
        {
            input_shutdown_impl();
        }
        g_input_log = p->common.log;
        game_call([&](bbs::Bridge &bridge) {
            bridge.set_game(game);
            bridge.reset_input(now_ms());
        });

        // One semantical device with every input of inputs.inc as a bool input, in that order. Static storage: the
        // game may read these after register_device returns.
        static scs_input_device_input_t inputs[bbs::kInputCount];
        for (size_t i = 0; i < bbs::kInputCount; ++i)
        {
            inputs[i].name = bbs::kInputNames[i];
            inputs[i].display_name = bbs::kInputNames[i];
            inputs[i].value_type = SCS_VALUE_TYPE_bool;
        }
        scs_input_device_t device;
        std::memset(&device, 0, sizeof device);
        device.name = "button_box";
        device.display_name = "Button Box for Simulations";
        device.type = SCS_INPUT_DEVICE_TYPE_semantical;
        device.input_count = static_cast<scs_u32_t>(bbs::kInputCount);
        device.inputs = inputs;
        device.callback_context = nullptr;
        device.input_active_callback = nullptr;
        device.input_event_callback = input_event_callback;
        const scs_result_t result = p->register_device(&device);
        if (result != SCS_RESULT_ok)
        {
            char text[96];
            std::snprintf(text, sizeof text, "cannot register the input device (%d)", static_cast<int>(result));
            log_now(g_input_log, SCS_LOG_TYPE_error, text);
            g_input_log = nullptr;
            return SCS_RESULT_generic_error;
        }
        g_input_inited = true;
        life_acquire();
        char text[128];
        std::snprintf(text, sizeof text, "input ready (%u inputs, plugin %s, game %s)",
                      static_cast<unsigned>(bbs::kInputCount), bbs::kPluginVersion, bbs::game_text(game));
        log_now(g_input_log, SCS_LOG_TYPE_message, text);
        return SCS_RESULT_ok;
    }
    catch (...)
    {
        return SCS_RESULT_generic_error;
    }
}

SCSAPI_VOID scs_input_shutdown(void)
{
    try
    {
        input_shutdown_impl();
    }
    catch (...)
    {
    }
}

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID reserved)
{
    // Unloaded with FreeLibrary (reserved == NULL): the pipe instance lived for the DLL's lifetime and ends here. At
    // process exit the system closes it.
    if (reason == DLL_PROCESS_DETACH && reserved == nullptr)
    {
        bbs::plugin::server_close_pipe();
    }
    return TRUE;
}
