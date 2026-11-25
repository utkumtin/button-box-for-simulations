// Telemetry values as the integers of the protocol (src/channels.inc mappings), with change detection per host
// connection. Pure logic.
#pragma once

#include <cstddef>
#include <cstdint>

#include "tables.h"

namespace bbs
{

// The mappings of decisions §4.1.
constexpr std::int32_t map_bool(bool value) noexcept
{
    return value ? 1 : 0;
}

constexpr std::int32_t map_u32(std::uint32_t value) noexcept
{
    return value > 2147483647u ? 2147483647 : static_cast<std::int32_t>(value);
}

constexpr std::int32_t map_positive(float value) noexcept
{
    return value > 0.0f ? 1 : 0; // NaN is not above 0
}

class TelemetryStore
{
  public:
    // The game reports no value (menu, loading, no truck).
    void set_none(size_t channel) noexcept;
    void set_bool(size_t channel, bool value) noexcept;
    void set_u32(size_t channel, std::uint32_t value) noexcept;
    void set_float(size_t channel, float value) noexcept;
    void clear_values() noexcept;

    bool has_value(size_t channel) const noexcept
    {
        return channel < kChannelCount && has_[channel];
    }

    std::int32_t value(size_t channel) const noexcept
    {
        return channel < kChannelCount ? value_[channel] : 0;
    }

    // A new host connection knows nothing yet: the next snapshot() sends every channel that has a value.
    void forget_reported() noexcept;

    // Calls emit(channel, value) for every channel that has a value, and marks them as reported.
    template <typename Emit> void snapshot(Emit &&emit)
    {
        for (size_t i = 0; i < kChannelCount; ++i)
        {
            if (has_[i])
            {
                reported_[i] = true;
                reported_value_[i] = value_[i];
                emit(i, value_[i]);
            }
        }
    }

    // Calls emit(channel, value) for every channel that has a value which differs from what the host was last told (or
    // was never told), and marks them as reported. A channel that lost its value is not reported: the host learns it
    // from "avail".
    template <typename Emit> void changes(Emit &&emit)
    {
        for (size_t i = 0; i < kChannelCount; ++i)
        {
            if (has_[i] && (!reported_[i] || reported_value_[i] != value_[i]))
            {
                reported_[i] = true;
                reported_value_[i] = value_[i];
                emit(i, value_[i]);
            }
        }
    }

  private:
    void set(size_t channel, std::int32_t mapped) noexcept
    {
        if (channel < kChannelCount)
        {
            has_[channel] = true;
            value_[channel] = mapped;
        }
    }

    bool has_[kMaxChannels] = {};
    std::int32_t value_[kMaxChannels] = {};
    bool reported_[kMaxChannels] = {};
    std::int32_t reported_value_[kMaxChannels] = {};
};

} // namespace bbs
