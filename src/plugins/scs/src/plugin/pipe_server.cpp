#include "pipe_server.h"

#include <windows.h>

#include <process.h>
#include <sddl.h>

#include <cstdint>
#include <cstdio>

#include "log_ring.h"
#include "plugin_info.h"
#include "shared.h"

namespace bbs::plugin
{

namespace
{

constexpr DWORD kPipeBufferSize = 4096;
constexpr DWORD kWriteTimeoutMs = 250; // PROTOCOL.md: every host write has a 250 ms timeout
constexpr DWORD kTickMs = 25;          // "listening" is re-checked at least this often (spec: every <= 50 ms)
constexpr DWORD kCreateRetryMs = 5000; // the other game may own the name
constexpr DWORD kReconnectDelayMs = 1000;
constexpr DWORD kStopJoinMs = 2000;
constexpr std::uint64_t kProtocolLogGapMs = 1000;

struct Worker
{
    HANDLE stop = nullptr;
    HANDLE thread = nullptr;
};

Worker *g_worker = nullptr; // main thread only
HANDLE g_wake = nullptr;    // auto-reset; created once, never closed (the game thread may signal it at any time)
HANDLE g_pipe = INVALID_HANDLE_VALUE; // worker thread, and DLL unload when no worker runs

struct Io
{
    HANDLE stop = nullptr;
    HANDLE connect_event = nullptr;
    HANDLE read_event = nullptr;
    HANDLE write_event = nullptr;
};

bool stopped(const Io &io) noexcept
{
    return WaitForSingleObject(io.stop, 0) == WAIT_OBJECT_0;
}

// Sleeps up to `ms`; true if the stop event fired.
bool sleep_or_stop(const Io &io, DWORD ms) noexcept
{
    return WaitForSingleObject(io.stop, ms) == WAIT_OBJECT_0;
}

void post(scs_log_type_t type, const char *text) noexcept
{
    log_post(type, text);
}

void post_error_code(const char *what, DWORD error) noexcept
{
    char text[128];
    std::snprintf(text, sizeof text, "%s (error %lu)", what, static_cast<unsigned long>(error));
    log_post(SCS_LOG_TYPE_warning, text);
}

// Protocol errors: at most one log line per second, so a misbehaving client cannot flood the game log.
void post_protocol_error(const char *what) noexcept
{
    static std::uint64_t last_ms = 0;
    const std::uint64_t now = now_ms();
    if (last_ms == 0 || now - last_ms >= kProtocolLogGapMs)
    {
        last_ms = now;
        log_post(SCS_LOG_TYPE_warning, what);
    }
}

// "O:<sid>D:P(A;;FA;;;<sid>)": owner and sole DACL entry are the current user (the host checks both).
HANDLE create_pipe(DWORD &error) noexcept
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
    {
        error = GetLastError();
        return INVALID_HANDLE_VALUE;
    }
    BYTE user_buffer[256];
    DWORD length = 0;
    const BOOL got = GetTokenInformation(token, TokenUser, user_buffer, sizeof user_buffer, &length);
    error = got ? 0 : GetLastError();
    CloseHandle(token);
    if (!got)
    {
        return INVALID_HANDLE_VALUE;
    }
    LPWSTR sid = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER *>(user_buffer)->User.Sid, &sid))
    {
        error = GetLastError();
        return INVALID_HANDLE_VALUE;
    }
    wchar_t sddl[256];
    const int written = swprintf_s(sddl, L"O:%lsD:P(A;;FA;;;%ls)", sid, sid);
    LocalFree(sid);
    if (written < 0)
    {
        error = ERROR_INSUFFICIENT_BUFFER;
        return INVALID_HANDLE_VALUE;
    }
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, &descriptor, nullptr))
    {
        error = GetLastError();
        return INVALID_HANDLE_VALUE;
    }
    SECURITY_ATTRIBUTES attributes = {};
    attributes.nLength = sizeof attributes;
    attributes.lpSecurityDescriptor = descriptor;
    attributes.bInheritHandle = FALSE;
    // First instance only, one instance, no remote clients: nobody else can own the name while the game runs.
    HANDLE pipe = CreateNamedPipeW(kPipeName, PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
                                   PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1,
                                   kPipeBufferSize, kPipeBufferSize, 0, &attributes);
    error = GetLastError();
    LocalFree(descriptor);
    return pipe;
}

enum class ConnectResult
{
    Connected,
    Stopped,
    Failed,
};

ConnectResult wait_for_host(const Io &io) noexcept
{
    OVERLAPPED overlapped = {};
    overlapped.hEvent = io.connect_event;
    ResetEvent(io.connect_event);
    if (ConnectNamedPipe(g_pipe, &overlapped))
    {
        return ConnectResult::Connected;
    }
    const DWORD error = GetLastError();
    if (error == ERROR_PIPE_CONNECTED)
    {
        return ConnectResult::Connected;
    }
    if (error != ERROR_IO_PENDING)
    {
        post_error_code("ConnectNamedPipe failed", error);
        return ConnectResult::Failed;
    }
    const HANDLE handles[2] = {io.connect_event, io.stop};
    const DWORD waited = WaitForMultipleObjects(2, handles, FALSE, INFINITE);
    if (waited == WAIT_OBJECT_0)
    {
        DWORD ignored = 0;
        return GetOverlappedResult(g_pipe, &overlapped, &ignored, FALSE) ? ConnectResult::Connected
                                                                         : ConnectResult::Failed;
    }
    CancelIoEx(g_pipe, &overlapped);
    DWORD ignored = 0;
    GetOverlappedResult(g_pipe, &overlapped, &ignored, TRUE);
    return ConnectResult::Stopped;
}

// One host write with a 250 ms timeout. false = the connection must be closed.
bool write_all(const Io &io, const std::uint8_t *data, DWORD length) noexcept
{
    OVERLAPPED overlapped = {};
    overlapped.hEvent = io.write_event;
    ResetEvent(io.write_event);
    DWORD written = 0;
    if (!WriteFile(g_pipe, data, length, nullptr, &overlapped))
    {
        if (GetLastError() != ERROR_IO_PENDING)
        {
            return false;
        }
        const HANDLE handles[2] = {io.write_event, io.stop};
        if (WaitForMultipleObjects(2, handles, FALSE, kWriteTimeoutMs) != WAIT_OBJECT_0)
        {
            CancelIoEx(g_pipe, &overlapped);
            DWORD ignored = 0;
            GetOverlappedResult(g_pipe, &overlapped, &ignored, TRUE);
            post_protocol_error("host write timed out, closing the connection");
            return false;
        }
    }
    return GetOverlappedResult(g_pipe, &overlapped, &written, FALSE) && written == length;
}

// Sends every queued frame. false = write failed.
bool flush_out(const Io &io) noexcept
{
    for (;;)
    {
        std::uint8_t buffer[2048];
        size_t n = 0;
        {
            LockGuard guard;
            n = g_shared.bridge.pop_out(buffer, sizeof buffer);
        }
        if (n == 0)
        {
            return true;
        }
        if (!write_all(io, buffer, static_cast<DWORD>(n)))
        {
            return false;
        }
    }
}

void run_session(const Io &io) noexcept
{
    {
        LockGuard guard;
        g_shared.bridge.on_connect(now_ms());
    }
    post(SCS_LOG_TYPE_message, "host connected");

    std::uint8_t input[512];
    OVERLAPPED read_overlapped = {};
    bool read_pending = false;

    for (;;)
    {
        bool failed = false;
        {
            LockGuard guard;
            g_shared.bridge.update_listening(now_ms());
            failed = g_shared.bridge.failed();
        }
        if (failed)
        {
            post_protocol_error("outgoing queue full, closing the connection");
            break;
        }
        if (!flush_out(io))
        {
            break;
        }
        if (!read_pending)
        {
            read_overlapped = OVERLAPPED{};
            read_overlapped.hEvent = io.read_event;
            ResetEvent(io.read_event);
            if (!ReadFile(g_pipe, input, sizeof input, nullptr, &read_overlapped) && GetLastError() != ERROR_IO_PENDING)
            {
                break; // the host is gone
            }
            read_pending = true;
        }
        const HANDLE handles[3] = {io.read_event, g_wake, io.stop};
        const DWORD waited = WaitForMultipleObjects(3, handles, FALSE, kTickMs);
        if (waited == WAIT_OBJECT_0 + 2)
        {
            break; // stopping
        }
        if (waited == WAIT_OBJECT_0)
        {
            read_pending = false;
            DWORD received = 0;
            if (!GetOverlappedResult(g_pipe, &read_overlapped, &received, FALSE) || received == 0)
            {
                break; // the host closed the pipe
            }
            Bridge::HostResult result = Bridge::HostResult::Ok;
            {
                LockGuard guard;
                result = g_shared.bridge.on_host_bytes(input, received, now_ms());
            }
            if (result != Bridge::HostResult::Ok)
            {
                post_protocol_error("protocol error from the host, closing the connection");
                break;
            }
        }
    }

    if (read_pending)
    {
        CancelIoEx(g_pipe, &read_overlapped);
        DWORD ignored = 0;
        GetOverlappedResult(g_pipe, &read_overlapped, &ignored, TRUE);
    }
    {
        LockGuard guard;
        g_shared.bridge.on_disconnect(); // releases every held input, discards queued presses and frames
    }
    post(SCS_LOG_TYPE_message, "host disconnected");
}

void run_worker(const Worker &worker) noexcept
{
    Io io;
    io.stop = worker.stop;
    io.connect_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    io.read_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    io.write_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (io.connect_event == nullptr || io.read_event == nullptr || io.write_event == nullptr)
    {
        post(SCS_LOG_TYPE_error, "cannot create the worker's events, the pipe is not served");
    }
    else
    {
        unsigned create_failures = 0;
        unsigned connect_failures = 0;
        while (!stopped(io))
        {
            try
            {
                if (g_pipe == INVALID_HANDLE_VALUE)
                {
                    DWORD error = 0;
                    g_pipe = create_pipe(error);
                    if (g_pipe == INVALID_HANDLE_VALUE)
                    {
                        if (create_failures++ % 12 == 0) // first failure, then about once a minute
                        {
                            post_error_code("cannot create the pipe (another process may own the name), retrying",
                                            error);
                        }
                        if (sleep_or_stop(io, kCreateRetryMs))
                        {
                            break;
                        }
                        continue;
                    }
                    create_failures = 0;
                    post(SCS_LOG_TYPE_message, "pipe created, waiting for the host");
                }
                const ConnectResult connected = wait_for_host(io);
                if (connected == ConnectResult::Stopped)
                {
                    break;
                }
                if (connected == ConnectResult::Failed)
                {
                    DisconnectNamedPipe(g_pipe); // retry on the same instance, never recreate it
                    if (connect_failures++ % 12 == 0)
                    {
                        post(SCS_LOG_TYPE_warning, "waiting for the host failed, retrying");
                    }
                    if (sleep_or_stop(io, kReconnectDelayMs))
                    {
                        break;
                    }
                    continue;
                }
                connect_failures = 0;
                run_session(io);
                DisconnectNamedPipe(g_pipe);
            }
            catch (...)
            {
                post(SCS_LOG_TYPE_error, "worker exception caught");
                {
                    LockGuard guard;
                    g_shared.bridge.on_disconnect();
                }
                if (g_pipe != INVALID_HANDLE_VALUE)
                {
                    DisconnectNamedPipe(g_pipe);
                }
                if (sleep_or_stop(io, kReconnectDelayMs))
                {
                    break;
                }
            }
        }
    }
    if (g_pipe != INVALID_HANDLE_VALUE)
    {
        DisconnectNamedPipe(g_pipe); // the instance itself stays for the next worker
    }
    {
        LockGuard guard;
        g_shared.bridge.on_disconnect();
    }
    if (io.connect_event != nullptr)
    {
        CloseHandle(io.connect_event);
    }
    if (io.read_event != nullptr)
    {
        CloseHandle(io.read_event);
    }
    if (io.write_event != nullptr)
    {
        CloseHandle(io.write_event);
    }
}

unsigned __stdcall worker_entry(void *argument)
{
    try
    {
        run_worker(*static_cast<const Worker *>(argument));
    }
    catch (...)
    {
    }
    return 0;
}

} // namespace

bool server_start() noexcept
{
    try
    {
        if (g_worker != nullptr)
        {
            return true;
        }
        if (g_wake == nullptr)
        {
            g_wake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            if (g_wake == nullptr)
            {
                return false;
            }
        }
        Worker *worker = new Worker;
        worker->stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (worker->stop == nullptr)
        {
            delete worker;
            return false;
        }
        worker->thread = reinterpret_cast<HANDLE>(_beginthreadex(nullptr, 0, worker_entry, worker, 0, nullptr));
        if (worker->thread == nullptr)
        {
            CloseHandle(worker->stop);
            delete worker;
            return false;
        }
        g_worker = worker;
        return true;
    }
    catch (...)
    {
        return false;
    }
}

void server_stop() noexcept
{
    try
    {
        Worker *worker = g_worker;
        g_worker = nullptr;
        if (worker == nullptr)
        {
            return;
        }
        SetEvent(worker->stop);
        if (WaitForSingleObject(worker->thread, kStopJoinMs) == WAIT_OBJECT_0)
        {
            CloseHandle(worker->thread);
            CloseHandle(worker->stop);
            delete worker;
        }
        else
        {
            // Never unload code a thread is still running: pin the DLL and leak the worker's few objects.
            HMODULE module = nullptr;
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                               reinterpret_cast<LPCWSTR>(&server_stop), &module);
            CloseHandle(worker->thread);
        }
    }
    catch (...)
    {
    }
}

void server_wake() noexcept
{
    if (g_wake != nullptr)
    {
        SetEvent(g_wake);
    }
}

void server_close_pipe() noexcept
{
    if (g_pipe != INVALID_HANDLE_VALUE)
    {
        CloseHandle(g_pipe);
        g_pipe = INVALID_HANDLE_VALUE;
    }
}

} // namespace bbs::plugin
