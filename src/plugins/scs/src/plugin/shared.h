// State shared between the game's main thread (SDK callbacks) and the pipe worker thread: the pure logic behind one
// short lock. Neither thread does I/O or waits for the other while holding the lock.
#pragma once

#include <windows.h>

#include <cstdint>

#include "bridge.h"

namespace bbs::plugin
{

struct Shared
{
    SRWLOCK lock = SRWLOCK_INIT;
    Bridge bridge;
};

extern Shared g_shared;

class LockGuard
{
  public:
    LockGuard() noexcept
    {
        AcquireSRWLockExclusive(&g_shared.lock);
    }

    ~LockGuard()
    {
        ReleaseSRWLockExclusive(&g_shared.lock);
    }

    LockGuard(const LockGuard &) = delete;
    LockGuard &operator=(const LockGuard &) = delete;
};

// Milliseconds on a steady clock; the clock of every timestamp handed to the Bridge.
std::uint64_t now_ms() noexcept;

} // namespace bbs::plugin
