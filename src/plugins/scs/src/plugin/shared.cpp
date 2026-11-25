#include "shared.h"

#include <chrono>

namespace bbs::plugin
{

Shared g_shared;

std::uint64_t now_ms() noexcept
{
    return static_cast<std::uint64_t>(
        std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch())
            .count());
}

} // namespace bbs::plugin
