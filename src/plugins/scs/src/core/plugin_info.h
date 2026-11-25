// Identity of the plugin and the pipe contract (protocol/PROTOCOL.md). Shared by the DLL and the tests.
#pragma once

namespace bbs
{

// The pipe name of protocol/vectors.txt ("pipe-name" line); the unit tests check it.
inline constexpr wchar_t kPipeName[] = L"\\\\.\\pipe\\button-box-for-simulations-scs";

// Sent in "hello"; the host refuses any other protocol version.
inline constexpr unsigned kProtocolVersion = 1;

// Sent in "hello" for logs and support; the host decides "update" by the DLL's hash, not by this string.
inline constexpr char kPluginVersion[] = "0.5.0";

// Largest frame payload (protocol/PROTOCOL.md "Frames").
inline constexpr unsigned kMaxPayload = 256;

// Returns kPluginVersion.
const char *plugin_version() noexcept;

} // namespace bbs
