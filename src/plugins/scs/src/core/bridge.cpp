#include "bridge.h"

#include <cstring>
#include <string_view>

namespace bbs
{

namespace
{

constexpr bool names_fit() noexcept
{
    for (size_t i = 0; i < kChannelCount; ++i)
    {
        if (std::string_view(kChannels[i].name).size() > kMaxNameLength)
        {
            return false;
        }
    }
    return true;
}
static_assert(names_fit(), "channel names must fit the protocol's name grammar");

// Copies a NUL-terminated ASCII constant into a protocol field (the constants are checked to fit).
template <size_t N> void copy_text(char (&dst)[N], const char *src) noexcept
{
    const size_t n = std::string_view(src).size();
    std::memcpy(dst, src, n < N - 1 ? n : N - 1);
}

} // namespace

void Bridge::reset_telemetry(std::uint64_t now_ms) noexcept
{
    store_.clear_values();
    detector_.set_paused(true);
    update_listening(now_ms);
    refresh_avail();
}

void Bridge::reset_input(std::uint64_t now_ms) noexcept
{
    scheduler_.release_all();
    for (bool &emitted : last_emitted_)
    {
        emitted = false;
    }
    detector_.reset_polling();
    update_listening(now_ms);
}

void Bridge::on_channel_none(size_t channel) noexcept
{
    store_.set_none(channel);
}

void Bridge::on_channel_bool(size_t channel, bool value) noexcept
{
    if (channel < kChannelCount && kChannels[channel].mapping == Mapping::Bool)
    {
        store_.set_bool(channel, value);
    }
}

void Bridge::on_channel_u32(size_t channel, std::uint32_t value) noexcept
{
    if (channel < kChannelCount && kChannels[channel].mapping == Mapping::U32)
    {
        store_.set_u32(channel, value);
    }
}

void Bridge::on_channel_float(size_t channel, float value) noexcept
{
    if (channel < kChannelCount && kChannels[channel].mapping == Mapping::Positive)
    {
        store_.set_float(channel, value);
    }
}

void Bridge::on_paused(std::uint64_t now_ms) noexcept
{
    detector_.set_paused(true);
    update_listening(now_ms);
}

void Bridge::on_started(std::uint64_t now_ms) noexcept
{
    detector_.set_paused(false);
    update_listening(now_ms);
}

void Bridge::on_device_activated(std::uint64_t now_ms) noexcept
{
    detector_.reset_polling(); // not listening until two close polls
    // The game treats a freshly activated device as having every input at 0; a press that was running must not
    // continue into the new activation.
    scheduler_.release_all();
    for (bool &emitted : last_emitted_)
    {
        emitted = false;
    }
    update_listening(now_ms);
}

void Bridge::on_poll_frame(std::uint64_t now_ms) noexcept
{
    detector_.on_poll(now_ms);
    update_listening(now_ms);
    scheduler_.on_frame(now_ms);
}

bool Bridge::next_input_event(size_t &index, bool &value) noexcept
{
    for (size_t i = 0; i < kInputCount; ++i)
    {
        const bool level = scheduler_.is_down(i);
        if (level != last_emitted_[i])
        {
            last_emitted_[i] = level;
            index = i;
            value = level;
            return true;
        }
    }
    return false;
}

void Bridge::on_frame_end(std::uint64_t now_ms) noexcept
{
    update_listening(now_ms);
    if (connected_)
    {
        store_.changes([this](size_t channel, std::int32_t value) { push_tel(channel, value); });
    }
    refresh_avail();
}

void Bridge::on_connect(std::uint64_t now_ms) noexcept
{
    out_.clear();
    failed_ = false;
    reader_.reset();
    detector_.reset_polling();
    scheduler_.release_all();
    update_listening(now_ms); // bring listening_ up to date while nothing is queued
    store_.forget_reported();
    avail_ = store_.has_value(kAvailChannel);
    connected_ = true;

    ToHost hello;
    hello.kind = ToHost::Kind::Hello;
    hello.number = kProtocolVersion;
    hello.game = game_;
    copy_text(hello.version, kPluginVersion);
    push(hello);
    store_.snapshot([this](size_t channel, std::int32_t value) { push_tel(channel, value); });
    push_bit(ToHost::Kind::Avail, avail_);
    push_bit(ToHost::Kind::Listening, listening_);
}

void Bridge::on_disconnect() noexcept
{
    connected_ = false;
    failed_ = false;
    out_.clear();
    reader_.reset();
    scheduler_.release_all();
}

Bridge::HostResult Bridge::on_host_bytes(const std::uint8_t *data, size_t size, std::uint64_t now_ms) noexcept
{
    size_t pos = 0;
    while (pos < size)
    {
        const size_t used = reader_.feed(data + pos, size - pos);
        pos += used;
        if (reader_.status() == FrameReader::Status::Error)
        {
            return HostResult::ProtocolError;
        }
        if (reader_.status() == FrameReader::Status::Frame)
        {
            const HostResult result = handle_payload(reader_.payload(), now_ms);
            reader_.next();
            if (result != HostResult::Ok)
            {
                return result;
            }
        }
        else if (used == 0)
        {
            break;
        }
    }
    return HostResult::Ok;
}

Bridge::HostResult Bridge::handle_payload(std::string_view payload, std::uint64_t now_ms) noexcept
{
    ToPlugin message;
    if (!parse_to_plugin(payload, message))
    {
        return HostResult::ProtocolError;
    }
    if (message.kind == ToPlugin::Kind::Ping)
    {
        ToHost pong;
        pong.kind = ToHost::Kind::Pong;
        pong.number = message.number;
        push(pong);
        return HostResult::Ok;
    }
    const size_t input = find_input(message.name);
    if (input == kNotFound)
    {
        return HostResult::ProtocolError; // never press a name the plugin has not registered
    }
    update_listening(now_ms); // the drop decision and the "listening" report use the same value
    scheduler_.submit(input, now_ms, listening_);
    return HostResult::Ok;
}

void Bridge::update_listening(std::uint64_t now_ms) noexcept
{
    const bool now_listening = detector_.listening(now_ms);
    if (now_listening == listening_)
    {
        return;
    }
    listening_ = now_listening;
    if (!listening_)
    {
        scheduler_.on_listening_lost();
    }
    if (connected_)
    {
        push_bit(ToHost::Kind::Listening, listening_);
    }
}

void Bridge::refresh_avail() noexcept
{
    const bool now_avail = store_.has_value(kAvailChannel);
    if (now_avail == avail_)
    {
        return;
    }
    avail_ = now_avail;
    if (connected_)
    {
        push_bit(ToHost::Kind::Avail, avail_);
    }
}

void Bridge::push(const ToHost &message) noexcept
{
    if (!connected_ || failed_)
    {
        return;
    }
    char text[OutQueue::kMaxOutPayload];
    const size_t n = encode_to_host(message, text, sizeof text);
    if (n == 0 || !out_.push(std::string_view(text, n)))
    {
        failed_ = true; // full: never skip a change, close the connection and resynchronise
    }
}

void Bridge::push_tel(size_t channel, std::int32_t value) noexcept
{
    ToHost message;
    message.kind = ToHost::Kind::Tel;
    message.value = value;
    copy_text(message.name, kChannels[channel].name);
    push(message);
}

void Bridge::push_bit(ToHost::Kind kind, bool bit) noexcept
{
    ToHost message;
    message.kind = kind;
    message.number = bit ? 1u : 0u;
    push(message);
}

} // namespace bbs
