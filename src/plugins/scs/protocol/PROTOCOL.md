# SCS plugin pipe protocol, version 1

The in-game plugin (`button_box_scs.dll`, loaded by ETS2 or ATS) and the host app talk over one local named pipe. The plugin
is the server, the host is the client. This file is the contract; `vectors.txt` next to it holds the test vectors that both the
C++ tests and the C# tests run.

## Pipe

- Name: `\\.\pipe\button-box-for-simulations-scs` (also the `pipe-name` line of `vectors.txt`; both sides test their constant
  against it).
- Byte mode, one instance, created with `FILE_FLAG_FIRST_PIPE_INSTANCE` and `PIPE_REJECT_REMOTE_CLIENTS`. The DACL grants full
  access to the current user only and the owner is the current user (`O:<user>D:P(A;;FA;;;<user>)`).
- The plugin keeps the one instance for as long as the DLL is loaded. After a host disconnects it calls `DisconnectNamedPipe`
  and waits for the next host on the same handle; it never closes and recreates the pipe while loaded.
- The host verifies the server on every connect before it reads or writes anything: the server process's image path, the pipe
  owner and the DACL (see the host's server check). It connects with Identification-level impersonation.

## Frames

Every message is one frame: a 2-byte unsigned length `N`, little endian, followed by `N` payload bytes.

- `1 <= N <= 256`. A length of 0 or above 256 is a protocol error.
- The payload is printable ASCII: tokens of bytes `0x21`-`0x7E`, separated by exactly one space (`0x20`). No leading or
  trailing space, no empty token. Any other byte is a protocol error.
- The first token is the command. An unknown command, a missing or extra argument, or an argument that does not match its
  grammar is a protocol error.

Argument grammars:

| Name | Grammar |
| --- | --- |
| `int` | `0` or `-?[1-9][0-9]*`, within -2147483648 to 2147483647 |
| `uint` | `0` or `[1-9][0-9]*`, within 0 to 4294967295 |
| `bit` | `0` or `1` |
| `name` | `[a-z][a-z0-9_.]*`, 1 to 64 bytes |
| `game` | `ets2` or `ats` |
| `version` | `[0-9A-Za-z.+-]+`, 1 to 32 bytes |

Every valid payload is canonical: decoding it and encoding the result gives the same bytes.

## Messages

Plugin to host:

| Payload | Meaning |
| --- | --- |
| `hello <protocol:uint> <game:game> <version:version>` | First frame after every connect. `protocol` is 1 for this version. |
| `tel <field:name> <value:int>` | A telemetry channel's current value (mapping in `src/channels.inc`). |
| `avail <bit>` | 1 while the game reports truck values; 0 in the main menu, while loading, or when there is no truck. |
| `listening <bit>` | 1 while the game accepts input: not paused, input polled within the last 250 ms, and the last two polls within 250 ms of each other (a game in the background polls about every 500 ms and is not listening). |
| `pong <uint>` | Answer to `ping` with the same number. |

Host to plugin:

| Payload | Meaning |
| --- | --- |
| `press <input:name>` | One momentary press of a semantical input. |
| `ping <uint>` | Liveness check. |

## Sequence

1. The host connects and verifies the server.
2. The plugin sends `hello`, then one `tel` for every registered channel that has a value, then `avail`, then `listening`.
   After that it sends only changes: a `tel` when a channel's integer value changes (checked once per game frame), `avail` and
   `listening` when they change.
3. The host waits at most 2 s for `hello`. A different protocol number ends the connection, and the host reports "plugin
   protocol mismatch" instead of retrying in a tight loop. The host sends nothing before a valid `hello`.
4. The host may then send `press` and `ping` at any time.

## Rules

- A protocol error closes the connection on the side that sees it. The plugin then releases every held input, discards every
  queued press and waits for the next host. The host reconnects after 1 s and gets a fresh snapshot.
- `press` of a name the plugin has not registered (`src/inputs.inc`) is a protocol error.
- `hello` more than once, or any plugin message before `hello`, is a protocol error.
- The plugin drops `press` (silently, not an error) that arrives while it reports `listening 0`, discards every queued press
  when listening drops to 0, and drops a press that has waited at the head of its queue for 250 ms without starting. So a press
  sent just before the game lost focus never fires on refocus, while the presses of one gesture (e.g. `ignitionon` then
  `ignitionstrt`) are not dropped merely because the game runs slowly.
- Presses are played one at a time in arrival order: at least 3 polled frames down, then at least 1 polled frame up.
- The plugin's outgoing queue is bounded (256 frames). If it is full, the plugin closes the connection rather than skip a change;
  the host resynchronises from the next snapshot.
- Every host write has a 250 ms timeout; a write that does not complete in time drops the press and closes the connection.
