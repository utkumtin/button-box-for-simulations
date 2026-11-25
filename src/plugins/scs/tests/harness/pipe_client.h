// The host side of the plugin pipe for the harness: connects like the real host (Identification impersonation level),
// reads frames without ever blocking, and checks that every frame the plugin sends is valid protocol.
#pragma once

#include <windows.h>

#include <cstdint>
#include <string>
#include <vector>

#include "plugin_info.h"
#include "protocol.h"

namespace harness
{

class PipeClient
{
  public:
    PipeClient() = default;
    PipeClient(const PipeClient &) = delete;
    PipeClient &operator=(const PipeClient &) = delete;

    ~PipeClient()
    {
        close();
    }

    // Connects, retrying while the plugin is not waiting yet (the instance is busy for a moment after a host left).
    bool connect(DWORD timeout_ms = 20000)
    {
        close();
        const ULONGLONG deadline = GetTickCount64() + timeout_ms;
        for (;;)
        {
            handle_ = CreateFileW(bbs::kPipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                                  SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
            if (handle_ != INVALID_HANDLE_VALUE)
            {
                inbox.clear();
                closed = false;
                invalid_frames = 0;
                reader_.reset();
                return true;
            }
            if (GetTickCount64() >= deadline)
            {
                return false;
            }
            Sleep(10);
        }
    }

    void close()
    {
        if (handle_ != INVALID_HANDLE_VALUE)
        {
            CloseHandle(handle_);
            handle_ = INVALID_HANDLE_VALUE;
        }
        closed = true;
    }

    HANDLE handle() const
    {
        return handle_;
    }

    // Moves every complete frame that is available into `inbox`; never blocks. Sets `closed` when the plugin hung up.
    void poll()
    {
        if (handle_ == INVALID_HANDLE_VALUE || closed)
        {
            return;
        }
        for (;;)
        {
            DWORD available = 0;
            if (!PeekNamedPipe(handle_, nullptr, 0, nullptr, &available, nullptr))
            {
                closed = true;
                return;
            }
            if (available == 0)
            {
                return;
            }
            std::uint8_t buffer[512];
            DWORD got = 0;
            if (!ReadFile(handle_, buffer, available < sizeof buffer ? available : sizeof buffer, &got, nullptr))
            {
                closed = true;
                return;
            }
            size_t pos = 0;
            while (pos < got)
            {
                pos += reader_.feed(buffer + pos, got - pos);
                if (reader_.status() == bbs::FrameReader::Status::Frame)
                {
                    inbox.emplace_back(reader_.payload());
                    bbs::ToHost parsed;
                    if (!bbs::parse_to_host(inbox.back(), parsed))
                    {
                        ++invalid_frames; // the plugin must only ever send valid protocol
                    }
                    reader_.next();
                }
                else if (reader_.status() == bbs::FrameReader::Status::Error)
                {
                    ++invalid_frames;
                    reader_.reset();
                }
            }
        }
    }

    bool send_payload(const std::string &payload)
    {
        std::uint8_t frame[2 + bbs::kMaxPayload];
        const size_t n = bbs::encode_frame(payload, frame, sizeof frame);
        return n > 0 && send_raw(frame, n);
    }

    bool send_raw(const std::uint8_t *bytes, size_t size)
    {
        if (handle_ == INVALID_HANDLE_VALUE)
        {
            return false;
        }
        DWORD written = 0;
        return WriteFile(handle_, bytes, static_cast<DWORD>(size), &written, nullptr) && written == size;
    }

    // Removes and returns true if the inbox holds `payload`.
    bool take(const std::string &payload)
    {
        for (size_t i = 0; i < inbox.size(); ++i)
        {
            if (inbox[i] == payload)
            {
                inbox.erase(inbox.begin() + static_cast<std::ptrdiff_t>(i));
                return true;
            }
        }
        return false;
    }

    bool has(const std::string &payload) const
    {
        for (const std::string &p : inbox)
        {
            if (p == payload)
            {
                return true;
            }
        }
        return false;
    }

    std::vector<std::string> inbox;
    bool closed = true;
    int invalid_frames = 0;

  private:
    HANDLE handle_ = INVALID_HANDLE_VALUE;
    bbs::FrameReader reader_;
};

} // namespace harness
