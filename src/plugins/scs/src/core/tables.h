// Compiled input and channel lists (src/inputs.inc, src/channels.inc) as constexpr tables, shared by the DLL and the
// tests.
#pragma once

#include <cstddef>
#include <iterator>
#include <string_view>

namespace bbs
{

// How a telemetry value becomes the integer sent to the host (decisions §4.1, src/channels.inc).
enum class Mapping
{
    Bool,     // 0 or 1
    U32,      // the value, clamped to the int32 range
    Positive, // 1 while the float is above 0, else 0
};

struct ChannelDef
{
    const char *name;
    Mapping mapping;
};

#define BBS_MAPPING_bool Mapping::Bool
#define BBS_MAPPING_u32 Mapping::U32
#define BBS_MAPPING_positive Mapping::Positive

inline constexpr const char *kInputNames[] = {
#define BBS_INPUT(name) name,
#include "inputs.inc"
#undef BBS_INPUT
};

inline constexpr ChannelDef kChannels[] = {
#define BBS_CHANNEL(name, mapping) {name, BBS_MAPPING_##mapping},
#include "channels.inc"
#undef BBS_CHANNEL
};

inline constexpr size_t kInputCount = std::size(kInputNames);
inline constexpr size_t kChannelCount = std::size(kChannels);

// Upper bounds of the fixed-size state arrays; the SDK allows 400 inputs per device.
inline constexpr size_t kMaxInputs = 64;
inline constexpr size_t kMaxChannels = 32;
static_assert(kInputCount > 0 && kInputCount <= kMaxInputs, "inputs.inc must stay within kMaxInputs");
static_assert(kChannelCount > 0 && kChannelCount <= kMaxChannels, "channels.inc must stay within kMaxChannels");

inline constexpr size_t kNotFound = static_cast<size_t>(-1);

constexpr size_t find_input(std::string_view name) noexcept
{
    for (size_t i = 0; i < kInputCount; ++i)
    {
        if (name == kInputNames[i])
        {
            return i;
        }
    }
    return kNotFound;
}

constexpr size_t find_channel(std::string_view name) noexcept
{
    for (size_t i = 0; i < kChannelCount; ++i)
    {
        if (name == kChannels[i].name)
        {
            return i;
        }
    }
    return kNotFound;
}

} // namespace bbs
