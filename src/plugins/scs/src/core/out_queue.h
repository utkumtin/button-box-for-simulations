// Bounded queue of outgoing frame payloads (plugin to host). A full queue is reported to the caller, never resolved by
// dropping a frame: the plugin closes the connection instead (PROTOCOL.md "Rules"). Pure logic.
#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string_view>

#include "protocol.h"

namespace bbs
{

class OutQueue
{
  public:
    static constexpr size_t kCapacity = 256;
    // Longest outgoing payload: "tel <64-byte name> -2147483648" is 80 bytes, "hello ..." 54.
    static constexpr size_t kMaxOutPayload = 96;

    // false if the queue is full or the payload is empty or too long; nothing is stored then.
    bool push(std::string_view payload) noexcept
    {
        if (count_ == kCapacity || payload.empty() || payload.size() > kMaxOutPayload)
        {
            return false;
        }
        Slot &slot = slots_[(head_ + count_) % kCapacity];
        slot.length = static_cast<std::uint8_t>(payload.size());
        std::memcpy(slot.bytes, payload.data(), payload.size());
        ++count_;
        return true;
    }

    // Writes as many whole frames (length prefix included) as fit in `capacity` and removes them. Returns the bytes
    // written.
    size_t pop_frames(std::uint8_t *out, size_t capacity) noexcept
    {
        size_t written = 0;
        while (count_ > 0)
        {
            const Slot &slot = slots_[head_];
            const size_t n = encode_frame(std::string_view(slot.bytes, slot.length), out + written, capacity - written);
            if (n == 0)
            {
                break;
            }
            written += n;
            head_ = (head_ + 1) % kCapacity;
            --count_;
        }
        return written;
    }

    size_t size() const noexcept
    {
        return count_;
    }

    void clear() noexcept
    {
        head_ = 0;
        count_ = 0;
    }

  private:
    struct Slot
    {
        std::uint8_t length = 0;
        char bytes[kMaxOutPayload] = {};
    };

    Slot slots_[kCapacity] = {};
    size_t head_ = 0;
    size_t count_ = 0;
};

} // namespace bbs
