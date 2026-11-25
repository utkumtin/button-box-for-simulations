// Wire protocol of protocol/PROTOCOL.md: frame reader and message parse/encode in both directions. Pure logic, no I/O.
#pragma once

#include <cstddef>
#include <cstdint>
#include <string_view>

#include "plugin_info.h"

namespace bbs
{

enum class Game : std::uint8_t
{
    Ets2,
    Ats,
};

const char *game_text(Game game) noexcept;

inline constexpr size_t kMaxNameLength = 64;
inline constexpr size_t kMaxVersionLength = 32;

// Plugin to host.
struct ToHost
{
    enum class Kind : std::uint8_t
    {
        Hello,     // number = protocol, game, version
        Tel,       // name, value
        Avail,     // number = bit
        Listening, // number = bit
        Pong,      // number
    };

    Kind kind = Kind::Avail;
    std::uint32_t number = 0;
    std::int32_t value = 0;
    Game game = Game::Ets2;
    char name[kMaxNameLength + 1] = {};
    char version[kMaxVersionLength + 1] = {};
};

// Host to plugin.
struct ToPlugin
{
    enum class Kind : std::uint8_t
    {
        Press, // name
        Ping,  // number
    };

    Kind kind = Kind::Ping;
    std::uint32_t number = 0;
    char name[kMaxNameLength + 1] = {};
};

// Grammar-only parsing: a valid payload says nothing about whether a name is registered. false = protocol error.
bool parse_to_host(std::string_view payload, ToHost &out) noexcept;
bool parse_to_plugin(std::string_view payload, ToPlugin &out) noexcept;

// Writes the canonical payload (no length prefix). Returns the length, or 0 if it does not fit in capacity.
size_t encode_to_host(const ToHost &message, char *out, size_t capacity) noexcept;
size_t encode_to_plugin(const ToPlugin &message, char *out, size_t capacity) noexcept;

// Writes the 2-byte little endian length followed by the payload. Returns 0 if the payload length is not 1..256 or
// does not fit.
size_t encode_frame(std::string_view payload, std::uint8_t *out, size_t capacity) noexcept;

// Incremental frame reader. Feed bytes in any chunking: feed() stops after the end of one frame or at the first
// error, so the caller handles the frame (payload()), calls next(), and feeds the rest.
class FrameReader
{
  public:
    enum class Status : std::uint8_t
    {
        NeedMore,
        Frame,
        Error,
    };

    // Returns the number of bytes consumed. Consumes nothing unless status() is NeedMore.
    size_t feed(const std::uint8_t *data, size_t size) noexcept;

    Status status() const noexcept
    {
        return status_;
    }

    // The payload of the current frame; valid while status() is Frame.
    std::string_view payload() const noexcept;

    // After a Frame: ready for the next frame.
    void next() noexcept
    {
        reset();
    }

    void reset() noexcept
    {
        have_ = 0;
        length_ = 0;
        status_ = Status::NeedMore;
    }

  private:
    std::uint8_t buffer_[2 + kMaxPayload] = {};
    size_t have_ = 0;
    size_t length_ = 0;
    Status status_ = Status::NeedMore;
};

} // namespace bbs
