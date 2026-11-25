// Loads the built plugin DLL with a fake SDK and talks to its real pipe like the host does.
// Usage: bbs_scs_harness <path to button_box_scs.dll>. Exit code 0 = every check passed.
//
// The DLL serves the real pipe name (\\.\pipe\button-box-for-simulations-scs), so the harness cannot run while a game
// with the plugin, or another test that serves the name, is running; it waits a few seconds for the name to be free.

#include <windows.h>

#include <aclapi.h>
#include <sddl.h>

#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <functional>
#include <string>
#include <vector>

#include "fake_game.h"
#include "pipe_client.h"
#include "plugin_info.h"
#include "tables.h"

namespace
{

int g_failures = 0;
int g_checks = 0;

void check(bool ok, const char *format, ...)
{
    char text[512];
    va_list args;
    va_start(args, format);
    std::vsnprintf(text, sizeof text, format, args);
    va_end(args);
    ++g_checks;
    if (!ok)
    {
        ++g_failures;
        std::printf("FAIL: %s\n", text);
    }
}

using harness::FakeGame;
using harness::InputEvent;
using harness::PipeClient;

const size_t kLightOn = bbs::find_input("lighton");
const size_t kHighBeam = bbs::find_input("hblight");
const size_t kIgnitionStart = bbs::find_input("ignitionstrt");

// Waits for `done()` (reading the pipe meanwhile) until the deadline, and returns as soon as it is true. While the
// game polls from its own thread the pump only waits; otherwise it drives the frames itself.
bool pump(FakeGame &game, PipeClient &client, const std::function<bool()> &done, DWORD timeout_ms = 20000)
{
    const ULONGLONG deadline = GetTickCount64() + timeout_ms;
    for (;;)
    {
        if (game.polling())
        {
            Sleep(2);
        }
        else
        {
            game.frame();
        }
        client.poll();
        if (done())
        {
            return true;
        }
        if (GetTickCount64() >= deadline)
        {
            return false;
        }
    }
}

// Lets `frames` polled frames pass (the plugin must stay quiet meanwhile).
void run_frames(FakeGame &game, PipeClient &client, int frames)
{
    const int target = game.frame_number() + frames;
    const ULONGLONG deadline = GetTickCount64() + 30000;
    while (game.frame_number() < target && GetTickCount64() < deadline)
    {
        if (game.polling())
        {
            Sleep(2);
        }
        else
        {
            game.frame();
        }
        client.poll();
    }
    client.poll();
}

// Like pump, but never drives the game: for the checks where the game must not poll.
bool wait_for(PipeClient &client, const std::function<bool()> &done, DWORD timeout_ms = 20000)
{
    const ULONGLONG deadline = GetTickCount64() + timeout_ms;
    for (;;)
    {
        client.poll();
        if (done())
        {
            return true;
        }
        if (GetTickCount64() >= deadline)
        {
            return false;
        }
        Sleep(2);
    }
}

// Round trip through the worker without driving the game.
bool sync_quiet(PipeClient &client, unsigned token)
{
    const std::string pong = "pong " + std::to_string(token);
    client.send_payload("ping " + std::to_string(token));
    return wait_for(client, [&] { return client.has(pong); });
}

// Round trip through the worker: everything sent before is handled when the pong arrives (frames are in order).
bool sync_with_worker(FakeGame &game, PipeClient &client, unsigned token)
{
    const std::string pong = "pong " + std::to_string(token);
    client.send_payload("ping " + std::to_string(token));
    return pump(game, client, [&] { return client.has(pong); });
}

int event_frame(const FakeGame &game, size_t from, size_t input, bool value)
{
    const std::vector<InputEvent> log = game.input_log();
    for (size_t i = from; i < log.size(); ++i)
    {
        if (log[i].index == input && log[i].value == value)
        {
            return log[i].frame;
        }
    }
    return -1;
}

std::vector<std::string> exported_names(HMODULE module)
{
    std::vector<std::string> names;
    const auto *base = reinterpret_cast<const BYTE *>(module);
    const auto *dos = reinterpret_cast<const IMAGE_DOS_HEADER *>(base);
    const auto *nt = reinterpret_cast<const IMAGE_NT_HEADERS *>(base + dos->e_lfanew);
    const IMAGE_DATA_DIRECTORY &dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (dir.VirtualAddress == 0)
    {
        return names;
    }
    const auto *exports = reinterpret_cast<const IMAGE_EXPORT_DIRECTORY *>(base + dir.VirtualAddress);
    const auto *name_rvas = reinterpret_cast<const DWORD *>(base + exports->AddressOfNames);
    for (DWORD i = 0; i < exports->NumberOfNames; ++i)
    {
        names.emplace_back(reinterpret_cast<const char *>(base + name_rvas[i]));
    }
    if (exports->NumberOfFunctions != exports->NumberOfNames)
    {
        names.emplace_back("<function without a name>");
    }
    return names;
}

bool pipe_name_is_free()
{
    // WaitNamedPipe does not connect: FILE_NOT_FOUND means no instance of the pipe exists.
    return !WaitNamedPipeW(bbs::kPipeName, 1) && GetLastError() == ERROR_FILE_NOT_FOUND;
}

bool wait_for_free_pipe_name(DWORD timeout_ms)
{
    const ULONGLONG deadline = GetTickCount64() + timeout_ms;
    while (!pipe_name_is_free())
    {
        if (GetTickCount64() >= deadline)
        {
            return false;
        }
        Sleep(100);
    }
    return true;
}

harness::Exports load_exports(HMODULE module)
{
    harness::Exports e;
    e.telemetry_init = reinterpret_cast<decltype(e.telemetry_init)>(GetProcAddress(module, "scs_telemetry_init"));
    e.telemetry_shutdown =
        reinterpret_cast<decltype(e.telemetry_shutdown)>(GetProcAddress(module, "scs_telemetry_shutdown"));
    e.input_init = reinterpret_cast<decltype(e.input_init)>(GetProcAddress(module, "scs_input_init"));
    e.input_shutdown = reinterpret_cast<decltype(e.input_shutdown)>(GetProcAddress(module, "scs_input_shutdown"));
    return e;
}

// ---- scenarios -------------------------------------------------------------------------------------------------

void check_registrations(const FakeGame &game)
{
    // events
    bool frame_end = false;
    bool paused = false;
    bool started = false;
    for (const auto &e : game.events())
    {
        frame_end = frame_end || e.event == SCS_TELEMETRY_EVENT_frame_end;
        paused = paused || e.event == SCS_TELEMETRY_EVENT_paused;
        started = started || e.event == SCS_TELEMETRY_EVENT_started;
    }
    check(frame_end && paused && started, "frame_end, paused and started events are registered");

    // channels: exactly channels.inc, with the matching SDK type and the "no value" flag
    check(game.channels().size() == bbs::kChannelCount, "%zu channels registered, expected %zu", game.channels().size(),
          bbs::kChannelCount);
    for (size_t i = 0; i < bbs::kChannelCount; ++i)
    {
        bool found = false;
        for (const auto &c : game.channels())
        {
            if (c.name != bbs::kChannels[i].name)
            {
                continue;
            }
            found = true;
            scs_value_type_t expected = SCS_VALUE_TYPE_INVALID;
            switch (bbs::kChannels[i].mapping)
            {
            case bbs::Mapping::Bool:
                expected = SCS_VALUE_TYPE_bool;
                break;
            case bbs::Mapping::U32:
                expected = SCS_VALUE_TYPE_u32;
                break;
            case bbs::Mapping::Positive:
                expected = SCS_VALUE_TYPE_float;
                break;
            }
            check(c.type == expected, "channel %s has the SDK type matching its mapping", c.name.c_str());
            check((c.flags & SCS_TELEMETRY_CHANNEL_FLAG_no_value) != 0, "channel %s has the no_value flag",
                  c.name.c_str());
            check(c.index == SCS_U32_NIL, "channel %s is not indexed", c.name.c_str());
        }
        check(found, "channel %s is registered", bbs::kChannels[i].name);
    }

    // exactly one semantical device with the inputs of inputs.inc, bool, in order
    check(game.devices().size() == 1, "exactly one input device is registered (%zu)", game.devices().size());
    if (game.devices().size() == 1)
    {
        const auto &d = game.devices()[0];
        check(d.type == SCS_INPUT_DEVICE_TYPE_semantical, "the device is semantical");
        check(d.event_callback != nullptr, "the device has an event callback");
        check(d.input_names.size() == bbs::kInputCount, "%zu inputs registered, expected %zu", d.input_names.size(),
              bbs::kInputCount);
        for (size_t i = 0; i < d.input_names.size() && i < bbs::kInputCount; ++i)
        {
            check(d.input_names[i] == bbs::kInputNames[i], "input %zu is %s (got %s)", i, bbs::kInputNames[i],
                  d.input_names[i].c_str());
            check(d.input_types[i] == SCS_VALUE_TYPE_bool, "input %s is a bool input", bbs::kInputNames[i]);
        }
    }
}

void check_pipe_security(PipeClient &client)
{
    // The pipe's owner and its only DACL entry are the current user (the host checks both).
    HANDLE token = nullptr;
    OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token);
    BYTE user_buffer[256];
    DWORD length = 0;
    const bool got =
        token != nullptr && GetTokenInformation(token, TokenUser, user_buffer, sizeof user_buffer, &length);
    if (token != nullptr)
    {
        CloseHandle(token);
    }
    check(got, "current user SID is readable");
    if (!got)
    {
        return;
    }
    PSID user = reinterpret_cast<TOKEN_USER *>(user_buffer)->User.Sid;
    PSID owner = nullptr;
    PACL dacl = nullptr;
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    const DWORD status =
        GetSecurityInfo(client.handle(), SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
                        &owner, nullptr, &dacl, nullptr, &descriptor);
    check(status == ERROR_SUCCESS, "pipe security is readable (%lu)", status);
    if (status != ERROR_SUCCESS)
    {
        return;
    }
    check(owner != nullptr && EqualSid(owner, user), "the pipe owner is the current user");
    check(dacl != nullptr && dacl->AceCount == 1, "the pipe DACL has exactly one entry");
    if (dacl != nullptr && dacl->AceCount == 1)
    {
        ACCESS_ALLOWED_ACE *ace = nullptr;
        const bool have = GetAce(dacl, 0, reinterpret_cast<LPVOID *>(&ace)) != 0;
        check(have && ace->Header.AceType == ACCESS_ALLOWED_ACE_TYPE, "the DACL entry is an allow entry");
        check(have && EqualSid(reinterpret_cast<PSID>(&ace->SidStart), user), "the DACL entry is the current user");
        check(have && ace->Mask == FILE_ALL_ACCESS, "the DACL entry grants full access");
    }
    SECURITY_DESCRIPTOR_CONTROL control = 0;
    DWORD revision = 0;
    check(GetSecurityDescriptorControl(descriptor, &control, &revision) && (control & SE_DACL_PROTECTED) != 0,
          "the DACL is protected (no inherited entries)");
    LocalFree(descriptor);
}

void check_second_server_fails()
{
    // First instance only: nobody else can create the pipe while the plugin owns it.
    HANDLE second = CreateNamedPipeW(bbs::kPipeName, PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
                                     PIPE_TYPE_BYTE | PIPE_WAIT, 1, 512, 512, 0, nullptr);
    const DWORD error = GetLastError();
    check(second == INVALID_HANDLE_VALUE, "a second pipe server on the same name must fail (error %lu)", error);
    if (second != INVALID_HANDLE_VALUE)
    {
        CloseHandle(second);
    }
    // Even without the first-instance flag no extra instance can be created (one instance only).
    HANDLE third =
        CreateNamedPipeW(bbs::kPipeName, PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_WAIT, 1, 512, 512, 0, nullptr);
    check(third == INVALID_HANDLE_VALUE, "a second instance of the pipe must fail");
    if (third != INVALID_HANDLE_VALUE)
    {
        CloseHandle(third);
    }
}

// Connects a client, with a started game, and waits for the opening frames.
bool connect_and_wait_for_hello(FakeGame &game, PipeClient &client)
{
    if (!client.connect())
    {
        return false;
    }
    // The game is started and polls fast: listening 1 follows the hello (a connect forgets the polls seen).
    return pump(game, client, [&] { return client.has("avail 0") || client.has("avail 1"); }) &&
           pump(game, client, [&] { return client.has("listening 1"); });
}

} // namespace

int main(int argc, char **argv)
{
    if (argc != 2)
    {
        std::printf("usage: bbs_scs_harness <button_box_scs.dll>\n");
        return 2;
    }

    if (!wait_for_free_pipe_name(15000))
    {
        std::printf("FAIL: the pipe name %s is in use by another process (a running game with the plugin, or "
                    "another test); stop it and run again\n",
                    "button-box-for-simulations-scs");
        return 1;
    }

    wchar_t wide_path[MAX_PATH];
    if (MultiByteToWideChar(CP_ACP, 0, argv[1], -1, wide_path, MAX_PATH) == 0)
    {
        std::printf("FAIL: bad path %s\n", argv[1]);
        return 1;
    }
    HMODULE dll = LoadLibraryW(wide_path);
    if (dll == nullptr)
    {
        std::printf("FAIL: cannot load %s (error %lu)\n", argv[1], GetLastError());
        return 1;
    }

    // ---- exports ----
    const std::vector<std::string> names = exported_names(dll);
    check(names.size() == 4, "the DLL exports exactly 4 functions (%zu)", names.size());
    for (const char *wanted : {"scs_input_init", "scs_input_shutdown", "scs_telemetry_init", "scs_telemetry_shutdown"})
    {
        bool found = false;
        for (const std::string &n : names)
        {
            found = found || n == wanted;
        }
        check(found, "export %s exists", wanted);
    }
    const harness::Exports exports = load_exports(dll);
    if (exports.telemetry_init == nullptr || exports.telemetry_shutdown == nullptr || exports.input_init == nullptr ||
        exports.input_shutdown == nullptr)
    {
        std::printf("FAIL: an export is missing\n");
        return 1;
    }

    {
        FakeGame game(exports);

        // ---- refused configurations ----
        check(game.init_telemetry("eut2", SCS_MAKE_VERSION(2, 0)) == SCS_RESULT_unsupported,
              "telemetry: an unknown API version is unsupported");
        check(game.init_input("eut2", SCS_MAKE_VERSION(2, 0)) == SCS_RESULT_unsupported,
              "input: an unknown API version is unsupported");
        check(game.init_telemetry("acc") == SCS_RESULT_unsupported, "telemetry: an unknown game is unsupported");
        check(game.init_input("acc") == SCS_RESULT_unsupported, "input: an unknown game is unsupported");
        check(game.devices().empty() && game.channels().empty(), "nothing is registered for a refused init");
        check(exports.input_init(SCS_INPUT_VERSION_1_00, nullptr) == SCS_RESULT_invalid_parameter,
              "input: null params are refused");
        check(exports.telemetry_init(SCS_TELEMETRY_VERSION_1_01, nullptr) == SCS_RESULT_invalid_parameter,
              "telemetry: null params are refused");
        check(wait_for_free_pipe_name(500), "a refused init does not start the pipe");

        // ---- registrations, with another process squatting on the pipe name ----
        // The plugin must not share the name: with first-instance-only it cannot create the pipe, logs it, keeps
        // working without a host and takes the name when the squatter leaves (it retries every 5 s).
        HANDLE squatter = CreateNamedPipeW(bbs::kPipeName, PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_WAIT,
                                           PIPE_UNLIMITED_INSTANCES, 512, 512, 0, nullptr);
        check(squatter != INVALID_HANDLE_VALUE, "the test can squat on the pipe name");
        check(game.init_telemetry("eut2") == SCS_RESULT_ok, "telemetry init (ETS2)");
        check(game.init_input("eut2") == SCS_RESULT_ok, "input init (ETS2)");
        check_registrations(game);
        {
            // The worker's log lines reach the game log from the game thread's callbacks; wait for the first attempt.
            bool logged = false;
            const ULONGLONG deadline = GetTickCount64() + 30000;
            while (!logged && GetTickCount64() < deadline)
            {
                game.frame();
                for (const std::string &line : game.game_log())
                {
                    logged = logged || line.find("cannot create the pipe") != std::string::npos;
                }
            }
            check(logged, "the plugin logs that it cannot create the pipe while the name is taken");
        }
        if (squatter != INVALID_HANDLE_VALUE)
        {
            CloseHandle(squatter);
        }
        // From here on the game polls from its own thread at a steady 10 ms, as the real game does (~80 fps).
        game.start_polling();

        // ---- first connection: nothing known yet, game paused (telemetry starts paused) ----
        {
            PipeClient client;
            check(client.connect(30000), "the host connects once the plugin has taken the name");
            check(pump(game, client, [&] { return client.inbox.size() >= 3; }), "hello, avail and listening arrive");
            check(client.inbox.size() == 3, "exactly 3 opening frames while no truck exists (%zu)",
                  client.inbox.size());
            if (client.inbox.size() >= 3)
            {
                check(client.inbox[0] == std::string("hello 1 ets2 ") + bbs::kPluginVersion, "hello is '%s'",
                      client.inbox[0].c_str());
                check(client.inbox[1] == "avail 0", "avail 0 without a truck");
                check(client.inbox[2] == "listening 0", "listening 0 while the game is paused");
            }
            check_pipe_security(client);
            check_second_server_fails();

            // ---- the game starts: listening 1 ----
            game.fire_event(SCS_TELEMETRY_EVENT_started);
            check(pump(game, client, [&] { return client.has("listening 1"); }), "listening 1 after started");

            // ---- telemetry: truck appears, values change ----
            game.set_channel_bool("truck.electric.enabled", true);
            game.set_channel_bool("truck.light.beam.low", true);
            game.set_channel_u32("truck.brake.retarder", 3);
            game.set_channel_float("truck.cruise_control", 22.5f);
            check(pump(game, client, [&] { return client.has("avail 1"); }), "avail 1 when the truck has values");
            check(client.has("tel truck.electric.enabled 1"), "tel for a bool channel");
            check(client.has("tel truck.light.beam.low 1"), "tel for another bool channel");
            check(client.has("tel truck.brake.retarder 3"), "tel for the u32 channel is its level");
            check(client.has("tel truck.cruise_control 1"), "tel for the float channel is 1 while above 0");
            client.inbox.clear();
            run_frames(game, client, 10);
            check(client.inbox.empty(), "nothing is sent without a change (%zu frames)", client.inbox.size());

            game.set_channel_float("truck.cruise_control", 0.0f);
            game.set_channel_u32("truck.brake.retarder", 0);
            check(pump(game, client, [&] { return client.has("tel truck.cruise_control 0"); }), "cruise control off");
            check(client.has("tel truck.brake.retarder 0"), "retarder level back to 0");

            // an unrelated value of the wrong type is ignored, never a crash or a bogus tel
            scs_value_t wrong = {};
            wrong.type = SCS_VALUE_TYPE_float;
            wrong.value_float.value = 1.0f;
            game.call_channel_callback("truck.engine.enabled", &wrong, nullptr, false);
            client.inbox.clear();
            run_frames(game, client, 5);
            check(client.inbox.empty(), "a value of the wrong type changes nothing");

            // ---- ping ----
            check(client.send_payload("ping 42"), "ping is sent");
            check(pump(game, client, [&] { return client.has("pong 42"); }), "pong with the same number");

            // ---- a press: 3 polled frames down, then up ----
            {
                const size_t mark = game.input_log().size();
                check(client.send_payload("press lighton"), "press is sent");
                check(pump(game, client, [&] { return event_frame(game, mark, kLightOn, false) >= 0; }),
                      "the press reaches the game and is released");
                const int down = event_frame(game, mark, kLightOn, true);
                const int up = event_frame(game, mark, kLightOn, false);
                check(down >= 0 && up - down >= 3, "lighton is down for at least 3 polled frames (%d)", up - down);
                check(game.input_log().size() == mark + 2, "exactly one down and one up event");
            }

            // ---- lighton then hblight never overlap, one up frame in between ----
            {
                const size_t mark = game.input_log().size();
                check(client.send_payload("press lighton"), "press lighton");
                check(client.send_payload("press hblight"), "press hblight");
                check(pump(game, client, [&] { return event_frame(game, mark, kHighBeam, false) >= 0; }),
                      "both presses play");
                const int light_up = event_frame(game, mark, kLightOn, false);
                const int beam_down = event_frame(game, mark, kHighBeam, true);
                const int beam_up = event_frame(game, mark, kHighBeam, false);
                check(light_up >= 0 && beam_down > light_up, "hblight goes down after lighton came up (%d, %d)",
                      light_up, beam_down);
                check(beam_up - beam_down >= 3, "hblight is down for at least 3 frames");
                bool overlap = false;
                bool light = false;
                bool beam = false;
                const std::vector<InputEvent> events = game.input_log();
                for (size_t i = mark; i < events.size(); ++i)
                {
                    const InputEvent &e = events[i];
                    (e.index == kLightOn ? light : beam) = e.value;
                    overlap = overlap || (light && beam);
                }
                check(!overlap, "two presses are never down at the same time");
            }

            // ---- malformed frames and unregistered presses close the connection ----
            run_frames(game, client, 3);
        }

        struct BadInput
        {
            const char *what;
            std::vector<std::uint8_t> bytes;
        };
        const std::vector<BadInput> bad = {
            {"a zero length", {0x00, 0x00}},
            {"a length above 256", {0x01, 0x01}},
            {"a control byte", {0x03, 0x00, 'a', 0x07, 'b'}},
            {"an unknown command", {0x05, 0x00, 't', 'a', 'p', ' ', '1'}},
            {"a plugin-to-host command", {0x06, 0x00, 'a', 'v', 'a', 'i', 'l', ' '}},
        };
        for (const BadInput &b : bad)
        {
            PipeClient client;
            check(connect_and_wait_for_hello(game, client), "the next client connects (%s)", b.what);
            check(client.send_raw(b.bytes.data(), b.bytes.size()), "%s is sent", b.what);
            check(pump(game, client, [&] { return client.closed; }), "%s closes the connection", b.what);
            check(client.invalid_frames == 0, "the plugin sent only valid frames");
        }
        {
            PipeClient client;
            check(connect_and_wait_for_hello(game, client), "the next client connects (unregistered press)");
            const size_t mark = game.input_log().size();
            check(client.send_payload("press nosuchinput"), "an unregistered press is sent");
            check(pump(game, client, [&] { return client.closed; }), "an unregistered press closes the connection");
            run_frames(game, client, 10);
            check(game.input_log().size() == mark, "an unregistered press does nothing in the game");
        }
        {
            // Bad payload split over two writes still ends the connection (the reader keeps state).
            PipeClient client;
            check(connect_and_wait_for_hello(game, client), "the next client connects (split bad frame)");
            const std::uint8_t first[3] = {0x05, 0x00, 'p'};
            const std::uint8_t second[4] = {'r', 'e', 's', 's'};
            check(client.send_raw(first, sizeof first) && client.send_raw(second, sizeof second),
                  "the split frame is sent");
            check(pump(game, client, [&] { return client.closed; }), "'press' without an argument closes");
        }

        // ---- a new client gets a fresh snapshot with the current values ----
        {
            PipeClient client;
            check(client.connect(), "a later client connects");
            check(pump(game, client, [&] { return client.has("listening 1"); }), "the later client gets listening");
            check(client.inbox.size() >= 4 && client.inbox[0].rfind("hello 1 ets2 ", 0) == 0,
                  "hello comes first (%zu frames)", client.inbox.size());
            check(client.has("tel truck.electric.enabled 1") && client.has("tel truck.light.beam.low 1"),
                  "the snapshot has the current values");
            check(client.has("tel truck.brake.retarder 0") && client.has("tel truck.cruise_control 0"),
                  "the snapshot has values that are 0");
            check(!client.has("tel truck.engine.enabled 0") && !client.has("tel truck.engine.enabled 1"),
                  "the snapshot has no channel without a value");
            check(client.has("avail 1"), "avail 1 in the snapshot");
            size_t avail_index = 0;
            size_t listening_index = 0;
            for (size_t i = 0; i < client.inbox.size(); ++i)
            {
                if (client.inbox[i] == "avail 1")
                {
                    avail_index = i;
                }
                if (listening_index == 0 && client.inbox[i].rfind("listening", 0) == 0)
                {
                    listening_index = i;
                }
            }
            check(avail_index + 1 == listening_index, "avail then listening end the snapshot");
            check(client.invalid_frames == 0, "the plugin sent only valid frames");

            // ---- the truck goes away: no value on the avail channel ----
            client.inbox.clear();
            game.set_channel_none("truck.electric.enabled");
            check(pump(game, client, [&] { return client.has("avail 0"); }), "avail 0 when the truck has no value");
            client.inbox.clear();
            game.set_channel_bool("truck.electric.enabled", true);
            check(pump(game, client, [&] { return client.has("avail 1"); }), "avail 1 when it comes back");
        }

        // ---- the host disappears mid-press: the input is released at the next poll ----
        // The game's poller halts itself right after the frame in which the press goes down, so a release can only
        // come from the hang-up: a running poller would release the press by itself after 3 frames. The next poll is
        // then done by hand once the worker handled the hang-up (the pipe is connectable again only after that). If a
        // loaded machine kept the game from polling for more than 150 ms in between, the plugin's own listening drop
        // (250 ms) could release the press too and hide a missing release on hang-up; such an attempt proves nothing
        // and is repeated, up to 10 times.
        {
            bool measured = false;
            for (int attempt = 0; attempt < 10 && !measured; ++attempt)
            {
                PipeClient client;
                check(connect_and_wait_for_hello(game, client), "the next client connects (mid-press)");
                const size_t mark = game.input_log().size();
                game.halt_after_next_input();
                check(client.send_payload("press ignitionstrt"), "press ignitionstrt");
                check(pump(game, client, [&] { return event_frame(game, mark, kIgnitionStart, true) >= 0; }),
                      "the game sees the press go down");
                client.close();
                const ULONGLONG closed_at = GetTickCount64();
                check(WaitNamedPipeW(bbs::kPipeName, 5000) != 0, "the plugin handled the hang-up");
                const ULONGLONG gap_ms = GetTickCount64() - closed_at;
                game.frame(0);
                measured = gap_ms <= 150;
                if (measured)
                {
                    // released at the very next poll, not after the 3 frames the press would have lasted
                    check(event_frame(game, mark, kIgnitionStart, false) == game.frame_number(),
                          "the input is released at the next poll after the host disconnects");
                }
                game.resume_polling();
                run_frames(game, client, 10);
                check(game.input_log().size() == mark + 2, "no further events after the release");
            }
            check(measured, "a mid-press hang-up was measured within a 150 ms polling gap");
        }

        // ---- not listening: no poll for more than 250 ms ----
        // Waits are deadline-based with an early exit; the only fixed times are the polling gaps themselves, which the
        // plugin must treat as "not listening" and which a slow machine can only make longer.
        {
            PipeClient client;
            check(connect_and_wait_for_hello(game, client), "the next client connects (listening)");
            client.inbox.clear();
            game.stop_polling(); // the game stops polling (alt-tab)
            check(wait_for(client, [&] { return client.has("listening 0"); }),
                  "listening 0 reported while the game does not poll");
            const size_t mark = game.input_log().size();
            check(client.send_payload("press lighton"), "a press is sent while the game is not listening");
            check(sync_quiet(client, 1), "the plugin handled the press");
            game.start_polling();
            check(pump(game, client, [&] { return client.has("listening 1"); }), "listening 1 when polling resumes");
            run_frames(game, client, 30);
            check(game.input_log().size() == mark, "a press sent while not listening never fires");

            // background: the unfocused game polls about every 500 ms; the host must never see listening 1
            client.inbox.clear();
            game.stop_polling();
            for (int i = 0; i < 6; ++i)
            {
                game.frame(500);
                client.poll();
            }
            check(wait_for(client, [&] { return client.has("listening 0"); }),
                  "listening 0 while the game polls every 500 ms");
            check(!client.has("listening 1"), "a game polling every 500 ms never reports listening 1");
            check(client.send_payload("press lighton"), "a press is sent while the game polls slowly");
            game.frame(500);
            check(sync_quiet(client, 2), "the plugin handled the press");
            check(game.input_log().size() == mark, "a press sent while the game polls slowly never fires");
            check(!client.has("listening 1"), "still no listening 1 while the game polls every 500 ms");
            game.start_polling();
            check(pump(game, client, [&] { return client.has("listening 1"); }), "fast polls make it listening again");
            run_frames(game, client, 30);
            check(game.input_log().size() == mark, "nothing fires after the game resumes polling fast");

            // pause: presses are dropped, nothing queued fires after started
            client.inbox.clear();
            game.fire_event(SCS_TELEMETRY_EVENT_paused);
            check(pump(game, client, [&] { return client.has("listening 0"); }), "listening 0 while paused");
            check(client.send_payload("press lighton"), "a press is sent while paused");
            check(sync_with_worker(game, client, 3), "the plugin handled the press");
            run_frames(game, client, 10);
            client.inbox.clear();
            game.fire_event(SCS_TELEMETRY_EVENT_started);
            check(pump(game, client, [&] { return client.has("listening 1"); }), "listening 1 after started again");
            run_frames(game, client, 30);
            check(game.input_log().size() == mark, "a press sent while paused never fires");
        }

        // ---- odd callback arguments never crash or disconnect the device ----
        {
            check(game.call_input_event_callback(nullptr, 0) == SCS_RESULT_not_found, "null event_info is not found");
            scs_input_event_t event;
            std::memset(&event, 0, sizeof event);
            check(game.call_input_event_callback(&event, 0) == SCS_RESULT_not_found,
                  "an input callback without a pending event returns not found");
            game.call_channel_callback("truck.engine.enabled", nullptr, reinterpret_cast<scs_context_t>(9999), true);
            game.fire_event(static_cast<scs_event_t>(9999));
            game.fire_event(SCS_TELEMETRY_EVENT_configuration);
        }

        // ---- the second client right after the first, and a client that is rude ----
        {
            PipeClient first;
            check(connect_and_wait_for_hello(game, first), "client A connects");
            first.close();
            PipeClient second;
            check(connect_and_wait_for_hello(game, second), "client B connects right after A left");
            check(second.send_payload("press lighton"), "client B presses");
            const size_t mark = game.input_log().size();
            check(pump(game, second, [&] { return event_frame(game, mark, kLightOn, false) >= 0; }),
                  "client B's press plays");
        }

        // ---- init / shutdown in any order, repeated ----
        {
            check(wait_for_free_pipe_name(100) == false, "the pipe still exists before the cycles");
            game.shutdown_input();
            game.shutdown_telemetry();
            for (int round = 0; round < 12; ++round)
            {
                const bool tel_first = (round % 2) == 0;
                if (tel_first)
                {
                    check(game.init_telemetry(round % 4 == 0 ? "ats" : "eut2") == SCS_RESULT_ok,
                          "cycle: telemetry init");
                    check(game.init_input("eut2") == SCS_RESULT_ok, "cycle: input init");
                }
                else
                {
                    check(game.init_input("ats") == SCS_RESULT_ok, "cycle: input init first");
                    check(game.init_telemetry("ats") == SCS_RESULT_ok, "cycle: telemetry init second");
                }
                if (round % 3 == 0)
                {
                    PipeClient client;
                    check(client.connect(), "cycle: the host connects in round %d", round);
                    check(pump(game, client, [&] { return client.inbox.size() >= 3; }), "cycle: hello arrives");
                    check(!client.inbox.empty() && client.inbox[0].rfind("hello 1 ", 0) == 0, "cycle: hello first");
                }
                if (round % 4 == 1)
                {
                    // init again without shutdown: the plugin starts clean
                    check(game.init_telemetry("eut2") == SCS_RESULT_ok, "cycle: telemetry init without shutdown");
                    check(game.init_input("eut2") == SCS_RESULT_ok, "cycle: input init without shutdown");
                }
                // A shutdown must not hang; a worker that misses its 2 s join would pin the DLL, which the free pipe
                // name after FreeLibrary at the end of the run detects (a loaded machine can legitimately take longer
                // than 2 s).
                const ULONGLONG shutdown_start = GetTickCount64();
                if (tel_first)
                {
                    game.shutdown_telemetry();
                    game.shutdown_input();
                }
                else
                {
                    game.shutdown_input();
                    game.shutdown_telemetry();
                }
                check(GetTickCount64() - shutdown_start < 10000, "cycle: the worker stops promptly (%llu ms)",
                      static_cast<unsigned long long>(GetTickCount64() - shutdown_start));
            }
            // shutdown without init
            game.shutdown_input();
            game.shutdown_telemetry();
            check(wait_for_free_pipe_name(100) == false, "the pipe instance stays while the DLL is loaded");
        }

        // ---- after all of that the plugin still serves a host (game ATS this time) ----
        {
            check(game.init_telemetry("ats") == SCS_RESULT_ok, "final telemetry init (ATS)");
            check(game.init_input("ats") == SCS_RESULT_ok, "final input init (ATS)");
            game.start_polling();
            PipeClient client;
            check(client.connect(), "the host connects after the cycles");
            check(pump(game, client, [&] { return client.inbox.size() >= 3; }), "hello arrives after the cycles");
            check(!client.inbox.empty() && client.inbox[0] == std::string("hello 1 ats ") + bbs::kPluginVersion,
                  "hello names the game ats");
            game.fire_event(SCS_TELEMETRY_EVENT_started);
            check(pump(game, client, [&] { return client.has("listening 1"); }), "listening after the cycles");
            // shut down only telemetry while input stays: avail and listening drop for the connected host
            game.set_channel_bool("truck.electric.enabled", true);
            check(pump(game, client, [&] { return client.has("avail 1"); }), "avail 1 in the last round");
            client.inbox.clear();
            game.shutdown_telemetry();
            check(pump(game, client, [&] { return client.has("avail 0"); }),
                  "avail 0 when telemetry shuts down while input stays");
            game.shutdown_input();
            check(pump(game, client, [&] { return client.closed; }),
                  "the host connection ends when the plugin shuts down");
        }
    } // game

    // ---- unload: the pipe goes away with the DLL ----
    check(FreeLibrary(dll) != 0, "the DLL unloads");
    if (GetModuleHandleW(wide_path) != nullptr)
    {
        // The plugin pins its DLL when the worker misses the 2 s join at shutdown (never unload code a thread still
        // runs). That happens only when the machine starves the process for seconds; the pipe then lives on with it.
        std::printf("NOTE: the DLL stayed loaded (pinned after a slow worker stop); pipe-name release not checked\n");
    }
    else
    {
        check(wait_for_free_pipe_name(15000), "the pipe name is free after the DLL is unloaded");
    }

    std::printf("%s (%d checks, %d failure(s))\n", g_failures == 0 ? "PASS" : "FAIL", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
