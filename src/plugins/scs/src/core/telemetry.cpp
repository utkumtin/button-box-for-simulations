#include "telemetry.h"

namespace bbs
{

void TelemetryStore::set_none(size_t channel) noexcept
{
    if (channel < kChannelCount)
    {
        has_[channel] = false;
        value_[channel] = 0;
    }
}

void TelemetryStore::set_bool(size_t channel, bool value) noexcept
{
    set(channel, map_bool(value));
}

void TelemetryStore::set_u32(size_t channel, std::uint32_t value) noexcept
{
    set(channel, map_u32(value));
}

void TelemetryStore::set_float(size_t channel, float value) noexcept
{
    set(channel, map_positive(value));
}

void TelemetryStore::clear_values() noexcept
{
    for (size_t i = 0; i < kMaxChannels; ++i)
    {
        has_[i] = false;
        value_[i] = 0;
    }
}

void TelemetryStore::forget_reported() noexcept
{
    for (size_t i = 0; i < kMaxChannels; ++i)
    {
        reported_[i] = false;
        reported_value_[i] = 0;
    }
}

} // namespace bbs
