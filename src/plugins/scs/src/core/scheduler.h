// Press scheduling (decisions §4.1): one input at a time, in arrival order, at least 3 polled frames down, then at
// least 1 polled frame up before the next press starts. Pure logic: the caller supplies the clock and calls on_frame()
// once per polled game frame.
#pragma once

#include <cstddef>
#include <cstdint>

#include "tables.h"

namespace bbs
{

class PressScheduler
{
  public:
    static constexpr unsigned kDownFrames = 3;
    static constexpr unsigned kUpFrames = 1;
    // A press that could start but has not within this time is dropped (it was sent before the game stopped polling).
    static constexpr std::uint64_t kHeadTimeoutMs = 250;
    // Bound of the pending presses; a press that finds the queue full is dropped.
    static constexpr size_t kQueueCapacity = 32;

    // Queues a press of input `input` that arrived at `now_ms`. Dropped (false) if the game is not listening, the input
    // index is out of range, or the queue is full.
    bool submit(size_t input, std::uint64_t now_ms, bool listening) noexcept;

    // One polled game frame at `now_ms`. Advances the active press, drops stale heads, starts the next press.
    void on_frame(std::uint64_t now_ms) noexcept;

    // The value the game must see for `input` in the current frame.
    bool is_down(size_t input) const noexcept
    {
        return phase_ == Phase::Down && active_ == input;
    }

    // The game stopped listening: discard every queued press and release the active one.
    void on_listening_lost() noexcept
    {
        release_all();
    }

    // Host gone or device re-activated: discard every queued press and release the active one. The release still
    // takes one polled up frame before the next press can start.
    void release_all() noexcept;

    size_t queued() const noexcept
    {
        return count_;
    }

    bool busy() const noexcept
    {
        return phase_ != Phase::Idle;
    }

  private:
    enum class Phase : std::uint8_t
    {
        Idle,
        Down,
        Up,
    };

    struct Pending
    {
        std::uint64_t arrival_ms = 0;
        std::uint16_t input = 0;
    };

    void start_next(std::uint64_t now_ms) noexcept;

    Pending queue_[kQueueCapacity] = {};
    size_t head_ = 0;
    size_t count_ = 0;
    Phase phase_ = Phase::Idle;
    size_t active_ = 0;
    unsigned frames_ = 0;
    // When the scheduler last became free to start a press. A queued press waits from max(arrival, free_since).
    std::uint64_t free_since_ms_ = 0;
};

} // namespace bbs
