#include "log_ring.h"

#include <windows.h>

#include <atomic>
#include <cstdio>

namespace bbs::plugin
{

namespace
{

constexpr int kEntries = 16;
constexpr size_t kTextSize = 192;

struct Entry
{
    scs_log_type_t type = SCS_LOG_TYPE_message;
    char text[kTextSize] = {};
};

SRWLOCK g_lock = SRWLOCK_INIT;
Entry g_entries[kEntries];
int g_head = 0;
int g_count = 0;
std::atomic<int> g_pending{0};

} // namespace

void log_post(scs_log_type_t type, const char *text) noexcept
{
    AcquireSRWLockExclusive(&g_lock);
    if (g_count < kEntries)
    {
        Entry &entry = g_entries[(g_head + g_count) % kEntries];
        entry.type = type;
        std::snprintf(entry.text, sizeof entry.text, "[button_box] %s", text);
        ++g_count;
        g_pending.store(g_count);
    }
    ReleaseSRWLockExclusive(&g_lock);
}

bool log_pending() noexcept
{
    return g_pending.load() != 0;
}

void log_flush(scs_log_t sink) noexcept
{
    for (;;)
    {
        Entry entry;
        AcquireSRWLockExclusive(&g_lock);
        const bool any = g_count > 0;
        if (any)
        {
            entry = g_entries[g_head];
            g_head = (g_head + 1) % kEntries;
            --g_count;
            g_pending.store(g_count);
        }
        ReleaseSRWLockExclusive(&g_lock);
        if (!any)
        {
            return;
        }
        if (sink != nullptr)
        {
            sink(entry.type, entry.text);
        }
    }
}

} // namespace bbs::plugin
