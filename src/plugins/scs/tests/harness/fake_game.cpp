#include "fake_game.h"

#include <cstring>
#include <thread>

namespace harness
{

namespace
{

// The SDK callbacks are plain functions without a user pointer, so the fake registry is one global.
FakeGame *g_current = nullptr;
std::vector<RegisteredChannel> *g_channels = nullptr;
std::vector<RegisteredEvent> *g_events = nullptr;
std::vector<RegisteredDevice> *g_devices = nullptr;
std::vector<std::string> *g_log = nullptr;

SCSAPI_RESULT fake_register_event(const scs_event_t event, const scs_telemetry_event_callback_t callback,
                                  const scs_context_t context)
{
    RegisteredEvent e;
    e.event = event;
    e.callback = callback;
    e.context = context;
    g_events->push_back(e);
    return SCS_RESULT_ok;
}

SCSAPI_RESULT fake_unregister_event(const scs_event_t)
{
    return SCS_RESULT_ok;
}

SCSAPI_RESULT fake_register_channel(const scs_string_t name, const scs_u32_t index, const scs_value_type_t type,
                                    const scs_u32_t flags, const scs_telemetry_channel_callback_t callback,
                                    const scs_context_t context)
{
    RegisteredChannel c;
    c.name = name;
    c.index = index;
    c.type = type;
    c.flags = flags;
    c.callback = callback;
    c.context = context;
    g_channels->push_back(c);
    return SCS_RESULT_ok;
}

SCSAPI_RESULT fake_unregister_channel(const scs_string_t, const scs_u32_t, const scs_value_type_t)
{
    return SCS_RESULT_ok;
}

SCSAPI_RESULT fake_register_device(const scs_input_device_t *const device)
{
    RegisteredDevice d;
    d.name = device->name;
    d.type = device->type;
    for (scs_u32_t i = 0; i < device->input_count; ++i)
    {
        d.input_names.push_back(device->inputs[i].name);
        d.input_types.push_back(device->inputs[i].value_type);
    }
    d.context = device->callback_context;
    d.event_callback = device->input_event_callback;
    g_devices->push_back(d);
    return SCS_RESULT_ok;
}

SCSAPI_VOID fake_log(const scs_log_type_t, const scs_string_t message)
{
    g_log->push_back(message);
}

} // namespace

scs_result_t FakeGame::init_telemetry(const char *game_id, scs_u32_t version)
{
    stop_polling();
    std::lock_guard<std::recursive_mutex> lock(mutex_);
    g_current = this;
    g_channels = &channels_;
    g_events = &events_;
    g_devices = &devices_;
    g_log = &game_log_;
    channels_.clear();
    events_.clear();
    scs_telemetry_init_params_v101_t params = {};
    params.common.game_name = "Fake Game";
    params.common.game_id = game_id;
    params.common.game_version = SCS_MAKE_VERSION(1, 0);
    params.common.log = fake_log;
    params.register_for_event = fake_register_event;
    params.unregister_from_event = fake_unregister_event;
    params.register_for_channel = fake_register_channel;
    params.unregister_from_channel = fake_unregister_channel;
    return exports_.telemetry_init(version, &params);
}

scs_result_t FakeGame::init_input(const char *game_id, scs_u32_t version)
{
    stop_polling();
    std::lock_guard<std::recursive_mutex> lock(mutex_);
    g_current = this;
    g_channels = &channels_;
    g_events = &events_;
    g_devices = &devices_;
    g_log = &game_log_;
    devices_.clear();
    activated_ = false;
    scs_input_init_params_v100_t params = {};
    params.common.game_name = "Fake Game";
    params.common.game_id = game_id;
    params.common.game_version = SCS_MAKE_VERSION(1, 0);
    params.common.log = fake_log;
    params.register_device = fake_register_device;
    return exports_.input_init(version, &params);
}

void FakeGame::shutdown_telemetry()
{
    stop_polling();
    std::lock_guard<std::recursive_mutex> lock(mutex_);
    exports_.telemetry_shutdown();
    channels_.clear();
    events_.clear();
}

void FakeGame::shutdown_input()
{
    stop_polling();
    std::lock_guard<std::recursive_mutex> lock(mutex_);
    exports_.input_shutdown();
    devices_.clear();
}

void FakeGame::frame(DWORD pace_ms)
{
    {
        std::lock_guard<std::recursive_mutex> lock(mutex_);
        ++frame_number_;
        if (!devices_.empty() && devices_[0].event_callback != nullptr)
        {
            scs_u32_t flags = SCS_INPUT_EVENT_CALLBACK_FLAG_first_in_frame;
            if (!activated_)
            {
                flags |= SCS_INPUT_EVENT_CALLBACK_FLAG_first_after_activation;
                activated_ = true;
            }
            for (int calls = 0; calls < 1000; ++calls)
            {
                scs_input_event_t event;
                std::memset(&event, 0xCD, sizeof event);
                const scs_result_t result = devices_[0].event_callback(&event, flags, devices_[0].context);
                flags = 0;
                if (result != SCS_RESULT_ok)
                {
                    break;
                }
                InputEvent logged;
                logged.frame = frame_number_;
                logged.index = event.input_index;
                logged.value = event.value_bool.value != 0;
                input_log_.push_back(logged);
                if (halt_on_input_)
                {
                    halt_on_input_ = false;
                    halted_ = true; // the poller thread stops polling after this frame
                }
            }
        }
        fire_event(SCS_TELEMETRY_EVENT_frame_end);
    }
    if (pace_ms > 0)
    {
        Sleep(pace_ms);
    }
}

void FakeGame::fire_event(scs_event_t event)
{
    std::lock_guard<std::recursive_mutex> lock(mutex_);
    for (const RegisteredEvent &e : events_)
    {
        if (e.event == event)
        {
            e.callback(event, nullptr, e.context);
        }
    }
}

const RegisteredChannel *FakeGame::find_channel(const char *name) const
{
    for (const RegisteredChannel &c : channels_)
    {
        if (c.name == name)
        {
            return &c;
        }
    }
    return nullptr;
}

void FakeGame::set_channel_bool(const char *name, bool value)
{
    std::lock_guard<std::recursive_mutex> lock(mutex_);
    const RegisteredChannel *c = find_channel(name);
    if (c != nullptr)
    {
        scs_value_t v = {};
        v.type = SCS_VALUE_TYPE_bool;
        v.value_bool.value = value ? 1 : 0;
        c->callback(c->name.c_str(), c->index, &v, c->context);
    }
}

void FakeGame::set_channel_u32(const char *name, scs_u32_t value)
{
    std::lock_guard<std::recursive_mutex> lock(mutex_);
    const RegisteredChannel *c = find_channel(name);
    if (c != nullptr)
    {
        scs_value_t v = {};
        v.type = SCS_VALUE_TYPE_u32;
        v.value_u32.value = value;
        c->callback(c->name.c_str(), c->index, &v, c->context);
    }
}

void FakeGame::set_channel_float(const char *name, float value)
{
    std::lock_guard<std::recursive_mutex> lock(mutex_);
    const RegisteredChannel *c = find_channel(name);
    if (c != nullptr)
    {
        scs_value_t v = {};
        v.type = SCS_VALUE_TYPE_float;
        v.value_float.value = value;
        c->callback(c->name.c_str(), c->index, &v, c->context);
    }
}

void FakeGame::set_channel_none(const char *name)
{
    std::lock_guard<std::recursive_mutex> lock(mutex_);
    const RegisteredChannel *c = find_channel(name);
    if (c != nullptr)
    {
        c->callback(c->name.c_str(), c->index, nullptr, c->context);
    }
}

scs_result_t FakeGame::call_input_event_callback(scs_input_event_t *event, scs_u32_t flags)
{
    std::lock_guard<std::recursive_mutex> lock(mutex_);
    if (devices_.empty())
    {
        return SCS_RESULT_not_found;
    }
    return devices_[0].event_callback(event, flags, devices_[0].context);
}

void FakeGame::call_channel_callback(const char *name, const scs_value_t *value, scs_context_t context_override,
                                     bool override)
{
    std::lock_guard<std::recursive_mutex> lock(mutex_);
    const RegisteredChannel *c = find_channel(name);
    if (c != nullptr)
    {
        c->callback(c->name.c_str(), c->index, value, override ? context_override : c->context);
    }
}

void FakeGame::start_polling(DWORD interval_ms)
{
    stop_polling();
    stop_poller_ = false;
    poller_ = std::thread([this, interval_ms] {
        // A steady poll, like the game at its frame rate; slightly above normal so a loaded machine does not starve it.
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
        while (!stop_poller_)
        {
            if (!halted_)
            {
                frame(0);
            }
            Sleep(interval_ms);
        }
    });
}

void FakeGame::stop_polling()
{
    stop_poller_ = true;
    if (poller_.joinable())
    {
        poller_.join();
    }
}

int FakeGame::frame_number() const
{
    std::lock_guard<std::recursive_mutex> lock(mutex_);
    return frame_number_;
}

std::vector<InputEvent> FakeGame::input_log() const
{
    std::lock_guard<std::recursive_mutex> lock(mutex_);
    return input_log_;
}

std::vector<std::string> FakeGame::game_log() const
{
    std::lock_guard<std::recursive_mutex> lock(mutex_);
    return game_log_;
}

} // namespace harness
