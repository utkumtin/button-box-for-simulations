// The pipe worker: one thread serving the plugin's named pipe (PROTOCOL.md). The pipe instance is created once, kept
// for the DLL's lifetime and re-used for every host (disconnect, then wait for the next one); it is closed only when
// the DLL is unloaded.
#pragma once

namespace bbs::plugin
{

// Main thread. Starts the worker (if not running). false = the thread could not be started.
bool server_start() noexcept;

// Main thread. Stops the worker and waits up to 2 s; if it does not finish, it is detached and the DLL is pinned so
// its code stays mapped.
void server_stop() noexcept;

// Any thread. Tells the worker that frames were queued.
void server_wake() noexcept;

// DLL_PROCESS_DETACH (FreeLibrary only): closes the pipe instance.
void server_close_pipe() noexcept;

} // namespace bbs::plugin
