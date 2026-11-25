#include "scheduler.h"

namespace bbs
{

bool PressScheduler::submit(size_t input, std::uint64_t now_ms, bool listening) noexcept
{
    if (!listening || input >= kInputCount || count_ == kQueueCapacity)
    {
        return false;
    }
    Pending &slot = queue_[(head_ + count_) % kQueueCapacity];
    slot.arrival_ms = now_ms;
    slot.input = static_cast<std::uint16_t>(input);
    ++count_;
    return true;
}

void PressScheduler::on_frame(std::uint64_t now_ms) noexcept
{
    if (phase_ == Phase::Down)
    {
        if (frames_ < kDownFrames)
        {
            ++frames_;
            return;
        }
        phase_ = Phase::Up; // this frame is the first up frame
        frames_ = 1;
        return;
    }
    if (phase_ == Phase::Up)
    {
        if (frames_ < kUpFrames)
        {
            ++frames_;
            return;
        }
        phase_ = Phase::Idle;
        free_since_ms_ = now_ms;
    }
    start_next(now_ms);
}

void PressScheduler::start_next(std::uint64_t now_ms) noexcept
{
    while (count_ > 0)
    {
        const Pending head = queue_[head_];
        head_ = (head_ + 1) % kQueueCapacity;
        --count_;
        const std::uint64_t eligible_ms = head.arrival_ms > free_since_ms_ ? head.arrival_ms : free_since_ms_;
        if (now_ms >= eligible_ms && now_ms - eligible_ms >= kHeadTimeoutMs)
        {
            continue; // waited too long without starting: the game was not polling, drop it
        }
        phase_ = Phase::Down;
        active_ = head.input;
        frames_ = 1;
        return;
    }
}

void PressScheduler::release_all() noexcept
{
    head_ = 0;
    count_ = 0;
    if (phase_ == Phase::Down)
    {
        phase_ = Phase::Up;
        frames_ = 0; // the next polled frame is the up frame
    }
}

} // namespace bbs
