// "Game listening" (decisions §10): not paused and the input device polls steadily. Pure logic with an injected clock;
// the plugin evaluates it on the pipe worker thread, because the game stops polling when unfocused.
//
// Steady = the last poll is at most 250 ms old AND the gap between the last two polls was at most 250 ms. A gap of
// exactly 250 ms counts as steady in both checks; a longer one does not. A game polling every ~500 ms in the background
// therefore never reports listening, and after a refocus the second fast poll makes it listening again.
#pragma once

#include <cstdint>

namespace bbs
{

class ListeningDetector
{
  public:
    static constexpr std::uint64_t kMaxPollGapMs = 250;

    void set_paused(bool paused) noexcept
    {
        paused_ = paused;
    }

    void on_poll(std::uint64_t now_ms) noexcept
    {
        has_previous_ = polled_;
        previous_poll_ms_ = last_poll_ms_;
        polled_ = true;
        last_poll_ms_ = now_ms;
    }

    bool listening(std::uint64_t now_ms) const noexcept
    {
        if (paused_ || !has_previous_)
        {
            return false;
        }
        const bool recent = now_ms <= last_poll_ms_ || now_ms - last_poll_ms_ <= kMaxPollGapMs;
        const bool steady = last_poll_ms_ <= previous_poll_ms_ || last_poll_ms_ - previous_poll_ms_ <= kMaxPollGapMs;
        return recent && steady;
    }

    // Forget the polling history (device re-activated, host connected); keeps the paused flag. Not listening until two
    // close polls have been seen.
    void reset_polling() noexcept
    {
        polled_ = false;
        has_previous_ = false;
        last_poll_ms_ = 0;
        previous_poll_ms_ = 0;
    }

  private:
    bool paused_ = true; // the SDK starts telemetry in the paused state
    bool polled_ = false;
    bool has_previous_ = false;
    std::uint64_t last_poll_ms_ = 0;
    std::uint64_t previous_poll_ms_ = 0;
};

} // namespace bbs
