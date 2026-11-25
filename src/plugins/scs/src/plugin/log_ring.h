// Log lines for the game log. The SDK's log function may only be called from the game's main thread, so other threads
// post into a small bounded ring and the main thread flushes it from its callbacks. Lines that do not fit are dropped.
#pragma once

#include <scssdk.h>

namespace bbs::plugin
{

// Any thread. `text` is copied (truncated to fit) behind a "[button_box] " prefix.
void log_post(scs_log_type_t type, const char *text) noexcept;

// Cheap check for the main thread's hot callbacks.
bool log_pending() noexcept;

// Main thread only: writes every posted line through `sink` (may be null: the lines are discarded).
void log_flush(scs_log_t sink) noexcept;

} // namespace bbs::plugin
