// A fake SCS game around the real button_box_scs.dll: records what the plugin registers and drives its callbacks the
// way the game does (polled frames, telemetry frame_end, pause events).
#pragma once

#include <windows.h>

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include <scssdk_input.h>
#include <scssdk_telemetry.h>

namespace harness
{

struct RegisteredChannel
{
    std::string name;
    scs_u32_t index = 0;
    scs_value_type_t type = 0;
    scs_u32_t flags = 0;
    scs_telemetry_channel_callback_t callback = nullptr;
    scs_context_t context = nullptr;
};

struct RegisteredEvent
{
    scs_event_t event = 0;
    scs_telemetry_event_callback_t callback = nullptr;
    scs_context_t context = nullptr;
};

struct RegisteredDevice
{
    std::string name;
    scs_input_device_type_t type = 0;
    std::vector<std::string> input_names;
    std::vector<scs_value_type_t> input_types;
    scs_context_t context = nullptr;
    scs_input_event_callback_t event_callback = nullptr;
};

// One input event as the game saw it.
struct InputEvent
{
    int frame = 0;
    scs_u32_t index = 0;
    bool value = false;
};

// The exports of the DLL.
struct Exports
{
    scs_result_t(SCSAPIFUNC *telemetry_init)(scs_u32_t, const scs_telemetry_init_params_t *) = nullptr;
    void(SCSAPIFUNC *telemetry_shutdown)(void) = nullptr;
    scs_result_t(SCSAPIFUNC *input_init)(scs_u32_t, const scs_input_init_params_t *) = nullptr;
    void(SCSAPIFUNC *input_shutdown)(void) = nullptr;
};

class FakeGame
{
  public:
    explicit FakeGame(const Exports &exports) : exports_(exports)
    {
    }

    ~FakeGame()
    {
        stop_polling();
    }

    // Polls from a dedicated thread at a steady short interval (the game at its frame rate) until stop_polling().
    // init_*, shutdown_* and the destructor stop it. frame() may still be called by hand while it is stopped.
    void start_polling(DWORD interval_ms = 10);
    void stop_polling();
    // The poller stops polling right after the frame in which the next input event is delivered, until
    // resume_polling().
    void halt_after_next_input()
    {
        halted_ = false;
        halt_on_input_ = true;
    }
    void resume_polling()
    {
        halt_on_input_ = false;
        halted_ = false;
    }
    bool polling() const
    {
        return poller_.joinable();
    }

    // Calls scs_telemetry_init / scs_input_init with fake parameters. Registrations of a previous init are forgotten
    // (the game unregisters everything at shutdown).
    scs_result_t init_telemetry(const char *game_id, scs_u32_t version = SCS_TELEMETRY_VERSION_1_01);
    scs_result_t init_input(const char *game_id, scs_u32_t version = SCS_INPUT_VERSION_1_00);
    void shutdown_telemetry();
    void shutdown_input();

    // One polled game frame: the input callback until it says "not found", then frame_end. Sleeps `pace_ms` after.
    void frame(DWORD pace_ms = 4);

    void fire_event(scs_event_t event);
    // Delivers a channel value; nullptr value = no value (menu).
    void set_channel_bool(const char *name, bool value);
    void set_channel_u32(const char *name, scs_u32_t value);
    void set_channel_float(const char *name, float value);
    void set_channel_none(const char *name);

    const std::vector<RegisteredChannel> &channels() const
    {
        return channels_;
    }

    const std::vector<RegisteredEvent> &events() const
    {
        return events_;
    }

    const std::vector<RegisteredDevice> &devices() const
    {
        return devices_;
    }

    // Copies: a polling thread may append at any time.
    std::vector<InputEvent> input_log() const;

    int frame_number() const;

    std::vector<std::string> game_log() const;

    // Robustness: callbacks called with odd arguments must not crash and must not disconnect the device.
    scs_result_t call_input_event_callback(scs_input_event_t *event, scs_u32_t flags);
    void call_channel_callback(const char *name, const scs_value_t *value, scs_context_t context_override,
                               bool override);

  private:
    const RegisteredChannel *find_channel(const char *name) const;

    mutable std::recursive_mutex mutex_;
    std::thread poller_;
    std::atomic<bool> stop_poller_{false};
    std::atomic<bool> halt_on_input_{false};
    std::atomic<bool> halted_{false};
    Exports exports_;
    std::vector<RegisteredChannel> channels_;
    std::vector<RegisteredEvent> events_;
    std::vector<RegisteredDevice> devices_;
    std::vector<InputEvent> input_log_;
    std::vector<std::string> game_log_;
    int frame_number_ = 0;
    bool activated_ = false;
};

} // namespace harness
