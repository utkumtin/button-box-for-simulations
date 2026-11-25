#include "protocol.h"

#include <charconv>
#include <cstring>

namespace bbs
{

namespace
{

constexpr size_t kMaxTokens = 4;

// Splits a payload into single-space separated tokens of printable ASCII. false on any byte outside 0x21-0x7E and
// 0x20, an empty token (leading, trailing or double space), an empty or oversized payload, or more than kMaxTokens.
bool split(std::string_view payload, std::string_view (&tokens)[kMaxTokens], size_t &count) noexcept
{
    if (payload.empty() || payload.size() > kMaxPayload)
    {
        return false;
    }
    count = 0;
    size_t start = 0;
    for (size_t i = 0; i <= payload.size(); ++i)
    {
        const bool at_end = i == payload.size();
        const unsigned char c = at_end ? ' ' : static_cast<unsigned char>(payload[i]);
        if (!at_end && c != ' ' && (c < 0x21 || c > 0x7E))
        {
            return false;
        }
        if (c == ' ')
        {
            if (i == start || count == kMaxTokens)
            {
                return false;
            }
            tokens[count++] = payload.substr(start, i - start);
            start = i + 1;
        }
    }
    return true;
}

bool is_digit(char c) noexcept
{
    return c >= '0' && c <= '9';
}

// Digits with no leading zero ("0" alone is fine); `text` has no sign.
bool plain_digits(std::string_view text) noexcept
{
    if (text.empty() || (text.size() > 1 && text[0] == '0'))
    {
        return false;
    }
    for (char c : text)
    {
        if (!is_digit(c))
        {
            return false;
        }
    }
    return true;
}

bool parse_uint(std::string_view text, std::uint32_t &out) noexcept
{
    if (!plain_digits(text))
    {
        return false;
    }
    std::uint32_t value = 0;
    const auto result = std::from_chars(text.data(), text.data() + text.size(), value);
    if (result.ec != std::errc() || result.ptr != text.data() + text.size())
    {
        return false;
    }
    out = value;
    return true;
}

bool parse_int(std::string_view text, std::int32_t &out) noexcept
{
    const bool negative = !text.empty() && text[0] == '-';
    if (!plain_digits(negative ? text.substr(1) : text) || (negative && text[1] == '0'))
    {
        return false; // also rejects "-0", which is not canonical
    }
    std::int32_t value = 0;
    const auto result = std::from_chars(text.data(), text.data() + text.size(), value);
    if (result.ec != std::errc() || result.ptr != text.data() + text.size())
    {
        return false;
    }
    out = value;
    return true;
}

bool parse_bit(std::string_view text, std::uint32_t &out) noexcept
{
    if (text == "0" || text == "1")
    {
        out = text[0] == '1' ? 1u : 0u;
        return true;
    }
    return false;
}

bool parse_name(std::string_view text, char (&out)[kMaxNameLength + 1]) noexcept
{
    if (text.empty() || text.size() > kMaxNameLength || text[0] < 'a' || text[0] > 'z')
    {
        return false;
    }
    for (char c : text)
    {
        const bool ok = (c >= 'a' && c <= 'z') || is_digit(c) || c == '_' || c == '.';
        if (!ok)
        {
            return false;
        }
    }
    std::memcpy(out, text.data(), text.size());
    out[text.size()] = '\0';
    return true;
}

bool parse_version(std::string_view text, char (&out)[kMaxVersionLength + 1]) noexcept
{
    if (text.empty() || text.size() > kMaxVersionLength)
    {
        return false;
    }
    for (char c : text)
    {
        const bool ok =
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || is_digit(c) || c == '.' || c == '+' || c == '-';
        if (!ok)
        {
            return false;
        }
    }
    std::memcpy(out, text.data(), text.size());
    out[text.size()] = '\0';
    return true;
}

bool parse_game(std::string_view text, Game &out) noexcept
{
    if (text == "ets2")
    {
        out = Game::Ets2;
        return true;
    }
    if (text == "ats")
    {
        out = Game::Ats;
        return true;
    }
    return false;
}

// Bounded text writer; any overflow makes finish() return 0.
class Writer
{
  public:
    Writer(char *out, size_t capacity) noexcept : out_(out), capacity_(capacity)
    {
    }

    void text(std::string_view part) noexcept
    {
        if (!ok_ || part.size() > capacity_ - used_)
        {
            ok_ = false;
            return;
        }
        std::memcpy(out_ + used_, part.data(), part.size());
        used_ += part.size();
    }

    void space() noexcept
    {
        text(" ");
    }

    template <typename T> void number(T value) noexcept
    {
        char digits[16];
        const auto result = std::to_chars(digits, digits + sizeof digits, value);
        text(std::string_view(digits, static_cast<size_t>(result.ptr - digits)));
    }

    size_t finish() const noexcept
    {
        return ok_ ? used_ : 0;
    }

  private:
    char *out_;
    size_t capacity_;
    size_t used_ = 0;
    bool ok_ = true;
};

} // namespace

const char *game_text(Game game) noexcept
{
    return game == Game::Ats ? "ats" : "ets2";
}

bool parse_to_host(std::string_view payload, ToHost &out) noexcept
{
    std::string_view t[kMaxTokens];
    size_t n = 0;
    if (!split(payload, t, n))
    {
        return false;
    }
    ToHost m;
    if (t[0] == "hello" && n == 4)
    {
        m.kind = ToHost::Kind::Hello;
        if (!parse_uint(t[1], m.number) || !parse_game(t[2], m.game) || !parse_version(t[3], m.version))
        {
            return false;
        }
    }
    else if (t[0] == "tel" && n == 3)
    {
        m.kind = ToHost::Kind::Tel;
        if (!parse_name(t[1], m.name) || !parse_int(t[2], m.value))
        {
            return false;
        }
    }
    else if (t[0] == "avail" && n == 2)
    {
        m.kind = ToHost::Kind::Avail;
        if (!parse_bit(t[1], m.number))
        {
            return false;
        }
    }
    else if (t[0] == "listening" && n == 2)
    {
        m.kind = ToHost::Kind::Listening;
        if (!parse_bit(t[1], m.number))
        {
            return false;
        }
    }
    else if (t[0] == "pong" && n == 2)
    {
        m.kind = ToHost::Kind::Pong;
        if (!parse_uint(t[1], m.number))
        {
            return false;
        }
    }
    else
    {
        return false;
    }
    out = m;
    return true;
}

bool parse_to_plugin(std::string_view payload, ToPlugin &out) noexcept
{
    std::string_view t[kMaxTokens];
    size_t n = 0;
    if (!split(payload, t, n))
    {
        return false;
    }
    ToPlugin m;
    if (t[0] == "press" && n == 2)
    {
        m.kind = ToPlugin::Kind::Press;
        if (!parse_name(t[1], m.name))
        {
            return false;
        }
    }
    else if (t[0] == "ping" && n == 2)
    {
        m.kind = ToPlugin::Kind::Ping;
        if (!parse_uint(t[1], m.number))
        {
            return false;
        }
    }
    else
    {
        return false;
    }
    out = m;
    return true;
}

size_t encode_to_host(const ToHost &m, char *out, size_t capacity) noexcept
{
    Writer w(out, capacity);
    switch (m.kind)
    {
    case ToHost::Kind::Hello:
        w.text("hello ");
        w.number(m.number);
        w.space();
        w.text(game_text(m.game));
        w.space();
        w.text(m.version);
        break;
    case ToHost::Kind::Tel:
        w.text("tel ");
        w.text(m.name);
        w.space();
        w.number(m.value);
        break;
    case ToHost::Kind::Avail:
        w.text("avail ");
        w.number(m.number);
        break;
    case ToHost::Kind::Listening:
        w.text("listening ");
        w.number(m.number);
        break;
    case ToHost::Kind::Pong:
        w.text("pong ");
        w.number(m.number);
        break;
    }
    return w.finish();
}

size_t encode_to_plugin(const ToPlugin &m, char *out, size_t capacity) noexcept
{
    Writer w(out, capacity);
    if (m.kind == ToPlugin::Kind::Press)
    {
        w.text("press ");
        w.text(m.name);
    }
    else
    {
        w.text("ping ");
        w.number(m.number);
    }
    return w.finish();
}

size_t encode_frame(std::string_view payload, std::uint8_t *out, size_t capacity) noexcept
{
    if (payload.empty() || payload.size() > kMaxPayload || capacity < 2 + payload.size())
    {
        return 0;
    }
    out[0] = static_cast<std::uint8_t>(payload.size() & 0xFFu);
    out[1] = static_cast<std::uint8_t>(payload.size() >> 8);
    std::memcpy(out + 2, payload.data(), payload.size());
    return 2 + payload.size();
}

size_t FrameReader::feed(const std::uint8_t *data, size_t size) noexcept
{
    size_t used = 0;
    while (used < size && status_ == Status::NeedMore)
    {
        if (have_ < 2)
        {
            buffer_[have_++] = data[used++];
            if (have_ == 2)
            {
                length_ = static_cast<size_t>(buffer_[0]) | (static_cast<size_t>(buffer_[1]) << 8);
                if (length_ == 0 || length_ > kMaxPayload)
                {
                    status_ = Status::Error; // as soon as the length is known
                }
            }
        }
        else
        {
            const size_t want = 2 + length_ - have_;
            const size_t take = want < size - used ? want : size - used;
            std::memcpy(buffer_ + have_, data + used, take);
            have_ += take;
            used += take;
            if (have_ == 2 + length_)
            {
                status_ = Status::Frame;
            }
        }
    }
    return used;
}

std::string_view FrameReader::payload() const noexcept
{
    return status_ == Status::Frame ? std::string_view(reinterpret_cast<const char *>(buffer_ + 2), length_)
                                    : std::string_view();
}

} // namespace bbs
