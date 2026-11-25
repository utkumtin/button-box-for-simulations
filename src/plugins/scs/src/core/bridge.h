// The plugin's logic without Windows or SDK types: telemetry store, press scheduler, listening detector, host
// protocol and the outgoing queue. NOT thread safe: the DLL wraps every call in one short lock. Time is injected
// (milliseconds from any steady clock).
//
// Threads in the DLL: the game's main thread calls the "game thread" group, the pipe worker calls the "worker" group.
#pragma once

#include <cstddef>
#include <cstdint>

#include "listening.h"
#include "out_queue.h"
#include "protocol.h"
#include "scheduler.h"
#include "telemetry.h"

namespace bbs
{

// "avail" (the game reports truck values): 1 while truck.electric.enabled has a value. It is registered with the
// "no value" flag, so the game calls back with NULL when the truck goes away (main menu, loading, no truck) and has a
// value whenever a truck exists, also with the ignition off. Checked in the game later (decisions §10).
inline constexpr const char *kAvailChannelName = "truck.electric.enabled";
inline constexpr size_t kAvailChannel = find_channel(kAvailChannelName);
static_assert(kAvailChannel != kNotFound, "channels.inc must contain the avail channel");

class Bridge
{
  public:
    enum class HostResult : std::uint8_t
    {
        Ok,
        ProtocolError,
    };

    // ---- game thread -------------------------------------------------------------------------------------------

    void set_game(Game game) noexcept
    {
        game_ = game;
    }

    // Telemetry (re)initialised or shut down: forget every value, game paused again (SDK: telemetry starts paused).
    void reset_telemetry(std::uint64_t now_ms) noexcept;

    // Input (re)initialised or shut down: release everything, forget the polling history.
    void reset_input(std::uint64_t now_ms) noexcept;

    // Channel callbacks. A call that does not fit the channel's mapping (e.g. a float for a bool channel) is ignored.
    void on_channel_none(size_t channel) noexcept;
    void on_channel_bool(size_t channel, bool value) noexcept;
    void on_channel_u32(size_t channel, std::uint32_t value) noexcept;
    void on_channel_float(size_t channel, float value) noexcept;

    void on_paused(std::uint64_t now_ms) noexcept;
    void on_started(std::uint64_t now_ms) noexcept;

    // Input device callback: the device became active again.
    void on_device_activated(std::uint64_t now_ms) noexcept;
    // Input device callback: the first poll of a game frame.
    void on_poll_frame(std::uint64_t now_ms) noexcept;
    // Input device callback: next input whose value differs from what the game last got. false = none (not_found).
    bool next_input_event(size_t &index, bool &value) noexcept;

    // Telemetry frame_end: sends the changes of this frame.
    void on_frame_end(std::uint64_t now_ms) noexcept;

    // ---- worker thread -----------------------------------------------------------------------------------------

    // A host connected: queues hello, the snapshot, avail and listening.
    void on_connect(std::uint64_t now_ms) noexcept;
    // The host is gone or the connection is being closed: releases every input, discards queued presses and frames.
    void on_disconnect() noexcept;
    // Bytes read from the host. ProtocolError means close the connection.
    HostResult on_host_bytes(const std::uint8_t *data, size_t size, std::uint64_t now_ms) noexcept;
    // Re-evaluates "listening"; call at least every 50 ms while connected.
    void update_listening(std::uint64_t now_ms) noexcept;
    // Moves queued frames (length prefix included) into `out`. Returns the bytes written.
    size_t pop_out(std::uint8_t *out, size_t capacity) noexcept
    {
        return out_.pop_frames(out, capacity);
    }

    // ---- either thread -----------------------------------------------------------------------------------------

    bool connected() const noexcept
    {
        return connected_;
    }

    // The outgoing queue overflowed: the connection must be closed.
    bool failed() const noexcept
    {
        return failed_;
    }

    bool out_pending() const noexcept
    {
        return out_.size() > 0;
    }

    bool listening() const noexcept
    {
        return listening_;
    }

    bool avail() const noexcept
    {
        return avail_;
    }

    const PressScheduler &scheduler() const noexcept
    {
        return scheduler_;
    }

  private:
    void refresh_avail() noexcept;
    void push(const ToHost &message) noexcept;
    void push_tel(size_t channel, std::int32_t value) noexcept;
    void push_bit(ToHost::Kind kind, bool bit) noexcept;
    HostResult handle_payload(std::string_view payload, std::uint64_t now_ms) noexcept;

    Game game_ = Game::Ets2;
    TelemetryStore store_;
    PressScheduler scheduler_;
    ListeningDetector detector_;
    FrameReader reader_;
    OutQueue out_;
    bool last_emitted_[kMaxInputs] = {};
    bool connected_ = false;
    bool failed_ = false;
    bool listening_ = false;
    bool avail_ = false;
};

} // namespace bbs
