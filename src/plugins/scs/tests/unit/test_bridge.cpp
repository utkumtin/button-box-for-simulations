// Bridge tests: the host protocol, hello and snapshot, avail, listening, presses and the bounded outgoing queue, with
// an injected clock and frame ticks.

#include "test_util.h"

#include "bridge.h"
#include "plugin_info.h"
#include "protocol.h"
#include "tables.h"

#include <cstdint>
#include <string>
#include <vector>

namespace
{

const size_t kLightOn = bbs::find_input("lighton");
const size_t kHighBeam = bbs::find_input("hblight");
const size_t kElectric = bbs::find_channel("truck.electric.enabled");
const size_t kEngine = bbs::find_channel("truck.engine.enabled");
const size_t kRetarder = bbs::find_channel("truck.brake.retarder");
const size_t kCruise = bbs::find_channel("truck.cruise_control");

// A bridge plus a fake game loop and host.
struct Rig
{
    bbs::Bridge bridge;
    std::uint64_t now = 1000;

    Rig()
    {
        bridge.set_game(bbs::Game::Ats);
    }

    void host_sends(const std::string &payload)
    {
        std::uint8_t frame[2 + bbs::kMaxPayload];
        const size_t n = bbs::encode_frame(payload, frame, sizeof frame);
        CHECK(n > 0);
        CHECK(bridge.on_host_bytes(frame, n, now) == bbs::Bridge::HostResult::Ok);
    }

    // One polled game frame: poll, then the telemetry frame end. Returns the inputs the game is told about.
    std::vector<std::pair<size_t, bool>> frame(std::uint64_t ms = 12)
    {
        now += ms;
        bridge.on_poll_frame(now);
        std::vector<std::pair<size_t, bool>> events;
        size_t index = 0;
        bool value = false;
        while (bridge.next_input_event(index, value))
        {
            events.emplace_back(index, value);
        }
        bridge.on_frame_end(now);
        return events;
    }

    // Every payload the plugin has queued for the host.
    std::vector<std::string> drain()
    {
        std::uint8_t buffer[4096];
        std::vector<std::string> out;
        bbs::FrameReader reader;
        for (;;)
        {
            const size_t n = bridge.pop_out(buffer, sizeof buffer);
            if (n == 0)
            {
                break;
            }
            size_t pos = 0;
            while (pos < n)
            {
                pos += reader.feed(buffer + pos, n - pos);
                CHECK(reader.status() != bbs::FrameReader::Status::Error);
                if (reader.status() == bbs::FrameReader::Status::Frame)
                {
                    out.emplace_back(reader.payload());
                    reader.next();
                }
            }
        }
        return out;
    }

    void start_game()
    {
        bridge.on_started(now);
        frame();
        frame(); // listening needs two close polls
    }

    // Connect a host, let the game poll twice (a connect forgets the polling history), drop the opening frames.
    void connect_settled()
    {
        bridge.on_connect(now);
        frame();
        frame();
        (void)drain();
    }
};

bool has(const std::vector<std::string> &frames, const std::string &payload)
{
    for (const std::string &f : frames)
    {
        if (f == payload)
        {
            return true;
        }
    }
    return false;
}

} // namespace

TEST(connect_sends_hello_then_the_snapshot_then_avail_then_listening)
{
    Rig r;
    r.bridge.on_channel_bool(kElectric, true);
    r.bridge.on_channel_bool(kEngine, false);
    r.bridge.on_channel_u32(kRetarder, 2);
    r.bridge.on_channel_float(kCruise, 22.0f);
    r.bridge.on_paused(r.now);
    r.bridge.on_connect(r.now);
    const std::vector<std::string> f = r.drain();
    CHECK(f.size() == 7);
    CHECK(f.size() == 7 && f[0] == std::string("hello 1 ats ") + bbs::kPluginVersion);
    // only channels with a value, in channels.inc order
    CHECK(f.size() == 7 && f[1] == "tel truck.electric.enabled 1");
    CHECK(f.size() == 7 && f[2] == "tel truck.engine.enabled 0");
    CHECK(f.size() == 7 && f[3] == "tel truck.brake.retarder 2");
    CHECK(f.size() == 7 && f[4] == "tel truck.cruise_control 1");
    CHECK(f.size() == 7 && f[5] == "avail 1");
    CHECK(f.size() == 7 && f[6] == "listening 0");
}

TEST(connect_without_a_truck_has_no_tel_and_avail_zero)
{
    Rig r;
    r.bridge.on_connect(r.now);
    const std::vector<std::string> f = r.drain();
    CHECK(f.size() == 3);
    CHECK(f.size() == 3 && f[0].rfind("hello 1 ats ", 0) == 0);
    CHECK(f.size() == 3 && f[1] == "avail 0");
    CHECK(f.size() == 3 && f[2] == "listening 0");
}

TEST(game_name_in_hello_follows_the_game)
{
    Rig r;
    r.bridge.set_game(bbs::Game::Ets2);
    r.bridge.on_connect(r.now);
    const std::vector<std::string> f = r.drain();
    CHECK(!f.empty() && f[0].rfind("hello 1 ets2 ", 0) == 0);
}

TEST(nothing_is_queued_while_no_host_is_connected)
{
    Rig r;
    r.start_game();
    r.bridge.on_channel_bool(kElectric, true);
    r.frame();
    r.bridge.on_paused(r.now);
    CHECK(!r.bridge.out_pending());
}

TEST(changes_are_sent_once_per_frame_and_only_when_they_change)
{
    Rig r;
    r.start_game();
    r.connect_settled();

    r.bridge.on_channel_bool(kEngine, true);
    CHECK(!r.bridge.out_pending()); // nothing until the frame ends
    r.frame();
    std::vector<std::string> f = r.drain();
    CHECK(f.size() == 1 && f[0] == "tel truck.engine.enabled 1");

    r.bridge.on_channel_bool(kEngine, true); // same value
    r.frame();
    CHECK(r.drain().empty());

    // two changes in one frame, the last value wins
    r.bridge.on_channel_bool(kEngine, false);
    r.bridge.on_channel_bool(kEngine, true);
    r.bridge.on_channel_bool(kEngine, false);
    r.frame();
    f = r.drain();
    CHECK(f.size() == 1 && f[0] == "tel truck.engine.enabled 0");
}

TEST(a_channel_value_of_the_wrong_kind_is_ignored)
{
    Rig r;
    r.connect_settled();
    r.bridge.on_channel_float(kEngine, 1.0f);  // bool channel
    r.bridge.on_channel_bool(kRetarder, true); // u32 channel
    r.bridge.on_channel_u32(kCruise, 5);       // float channel
    r.frame();
    CHECK(r.drain().empty());
}

TEST(avail_follows_the_truck_channel_having_a_value)
{
    Rig r;
    r.connect_settled();
    CHECK(!r.bridge.avail());

    r.bridge.on_channel_bool(kElectric, false); // truck present, ignition off: still a value
    r.frame();
    std::vector<std::string> f = r.drain();
    CHECK(has(f, "avail 1"));
    CHECK(has(f, "tel truck.electric.enabled 0"));
    CHECK(r.bridge.avail());

    r.bridge.on_channel_none(kElectric); // main menu
    r.frame();
    f = r.drain();
    CHECK(f.size() == 1 && f[0] == "avail 0");

    r.bridge.on_channel_none(kElectric); // repeated none: nothing new
    r.frame();
    CHECK(r.drain().empty());
}

TEST(telemetry_shutdown_reports_avail_zero_and_listening_zero)
{
    Rig r;
    r.start_game();
    r.bridge.on_channel_bool(kElectric, true);
    r.frame();
    r.connect_settled();
    r.frame();
    r.bridge.reset_telemetry(r.now); // input stays up, frame_end stops
    const std::vector<std::string> f = r.drain();
    CHECK(has(f, "avail 0"));
    CHECK(has(f, "listening 0"));
}

TEST(ping_is_answered_with_the_same_number)
{
    Rig r;
    r.connect_settled();
    r.host_sends("ping 4294967295");
    r.host_sends("ping 0");
    const std::vector<std::string> f = r.drain();
    CHECK(f.size() == 2 && f[0] == "pong 4294967295" && f[1] == "pong 0");
}

TEST(protocol_errors_from_the_host_are_reported)
{
    const std::vector<std::string> bad = {
        "press nosuchinput", // not registered
        "press LightOn",     // grammar
        "tap lighton 3",     // the S2 command is gone
        "hello 1 ets2 1.0",  // plugin-to-host message
        "ping",
    };
    for (const std::string &payload : bad)
    {
        Rig r;
        r.bridge.on_connect(r.now);
        std::uint8_t frame[2 + bbs::kMaxPayload];
        const size_t n = bbs::encode_frame(payload, frame, sizeof frame);
        CHECK(r.bridge.on_host_bytes(frame, n, r.now) == bbs::Bridge::HostResult::ProtocolError);
    }
    // a bad length and a non-printable byte
    Rig r;
    r.bridge.on_connect(r.now);
    const std::uint8_t zero_length[2] = {0, 0};
    CHECK(r.bridge.on_host_bytes(zero_length, 2, r.now) == bbs::Bridge::HostResult::ProtocolError);
    Rig r2;
    r2.bridge.on_connect(r2.now);
    const std::uint8_t control[5] = {3, 0, 'a', 0x07, 'b'};
    CHECK(r2.bridge.on_host_bytes(control, 5, r2.now) == bbs::Bridge::HostResult::ProtocolError);
}

TEST(frames_split_across_reads_still_work)
{
    Rig r;
    r.connect_settled();
    std::uint8_t frame[2 + bbs::kMaxPayload];
    const size_t n = bbs::encode_frame("ping 7", frame, sizeof frame);
    for (size_t i = 0; i < n; ++i)
    {
        CHECK(r.bridge.on_host_bytes(frame + i, 1, r.now) == bbs::Bridge::HostResult::Ok);
    }
    const std::vector<std::string> f = r.drain();
    CHECK(f.size() == 1 && f[0] == "pong 7");
}

TEST(a_press_is_three_polled_frames_down_then_up)
{
    Rig r;
    r.start_game();
    r.connect_settled();
    r.host_sends("press lighton");

    // Per polled frame: the value change the game is told about. 1 at the first frame, 0 three frames later.
    std::string timeline;
    for (int i = 0; i < 7; ++i)
    {
        const auto events = r.frame();
        CHECK(events.size() <= 1);
        if (!events.empty())
        {
            CHECK(events[0].first == kLightOn);
            timeline += events[0].second ? "1" : "0";
        }
        else
        {
            timeline += ".";
        }
    }
    CHECK(timeline == "1..0...");
}

TEST(lighton_then_hblight_play_back_to_back_without_overlap)
{
    Rig r;
    r.start_game();
    r.connect_settled();
    r.host_sends("press lighton");
    r.host_sends("press hblight");

    std::vector<std::string> timeline;
    for (int i = 0; i < 10; ++i)
    {
        std::string line;
        for (const auto &e : r.frame())
        {
            line += (e.first == kLightOn ? "L" : "H");
            line += e.second ? "1" : "0";
        }
        timeline.push_back(line);
    }
    CHECK(timeline[0] == "L1");
    CHECK(timeline[1].empty() && timeline[2].empty());
    CHECK(timeline[3] == "L0");
    CHECK(timeline[4] == "H1"); // 4 frames after lighton
    CHECK(timeline[5].empty() && timeline[6].empty());
    CHECK(timeline[7] == "H0");
    CHECK(timeline[8].empty() && timeline[9].empty());
}

TEST(a_press_that_arrives_while_the_game_is_paused_is_dropped)
{
    Rig r;
    r.start_game();
    r.connect_settled();
    r.bridge.on_paused(r.now);
    CHECK(has(r.drain(), "listening 0"));
    r.host_sends("press lighton"); // no error: dropped silently
    for (int i = 0; i < 6; ++i)
    {
        CHECK(r.frame().empty());
    }
    r.bridge.on_started(r.now);
    for (int i = 0; i < 6; ++i)
    {
        CHECK(r.frame().empty()); // and it does not fire after the game resumes
    }
}

TEST(pausing_discards_the_queued_presses)
{
    Rig r;
    r.start_game();
    r.connect_settled();
    r.host_sends("press lighton");
    r.host_sends("press hblight");
    r.bridge.on_paused(r.now);
    r.bridge.on_started(r.now);
    for (int i = 0; i < 12; ++i)
    {
        for (const auto &e : r.frame())
        {
            CHECK(!e.second); // at most the release of a press that had started
        }
    }
    CHECK(!r.bridge.scheduler().busy());
}

TEST(listening_drops_after_a_250ms_poll_gap_and_recovers)
{
    Rig r;
    r.start_game();
    r.connect_settled();
    std::vector<std::string> f;
    CHECK(r.bridge.listening());

    r.now += 250; // the worker checks while the game does not poll
    r.bridge.update_listening(r.now);
    CHECK(r.drain().empty());
    r.now += 1; // 251 ms since the last poll
    r.bridge.update_listening(r.now);
    f = r.drain();
    CHECK(f.size() == 1 && f[0] == "listening 0");

    r.bridge.update_listening(r.now + 40); // repeated checks report nothing new
    CHECK(r.drain().empty());

    r.frame(); // one poll after the gap is not steady yet
    CHECK(r.drain().empty());
    r.frame(); // the second fast poll
    f = r.drain();
    CHECK(f.size() == 1 && f[0] == "listening 1");
}

TEST(a_press_sent_just_before_focus_loss_never_fires_on_refocus)
{
    Rig r;
    r.start_game();
    r.connect_settled();
    r.host_sends("press lighton"); // queued, the game stops polling before the next frame
    r.now += 300;
    r.bridge.update_listening(r.now); // the worker notices the gap
    CHECK(!r.bridge.listening());
    CHECK(r.frame(1).empty()); // the first poll after refocus: nothing is pressed
    for (int i = 0; i < 6; ++i)
    {
        CHECK(r.frame().empty());
    }
}

TEST(a_press_that_arrives_after_a_long_gap_is_dropped_even_before_the_worker_noticed)
{
    Rig r;
    r.start_game();
    r.connect_settled();
    r.now += 400; // no poll for 400 ms, the worker has not run yet
    r.host_sends("press lighton");
    CHECK(!r.bridge.listening());
    CHECK(r.bridge.scheduler().queued() == 0);
}

TEST(disconnect_mid_press_releases_the_input_on_the_next_poll)
{
    Rig r;
    r.start_game();
    r.connect_settled();
    r.host_sends("press ignitionstrt");
    const auto down = r.frame();
    CHECK(down.size() == 1 && down[0].second);
    r.bridge.on_disconnect();
    CHECK(!r.bridge.connected());
    const auto up = r.frame();
    CHECK(up.size() == 1 && up[0].first == down[0].first && !up[0].second);
    for (int i = 0; i < 6; ++i)
    {
        CHECK(r.frame().empty());
    }
}

TEST(disconnect_discards_queued_presses_and_frames)
{
    Rig r;
    r.start_game();
    r.connect_settled();
    r.host_sends("press lighton");
    r.host_sends("press hblight");
    r.bridge.on_disconnect();
    CHECK(!r.bridge.out_pending());
    CHECK(r.bridge.scheduler().queued() == 0);
    // a reconnect starts from a clean slate with a fresh hello
    r.bridge.on_connect(r.now);
    const std::vector<std::string> f = r.drain();
    CHECK(!f.empty() && f[0].rfind("hello 1 ", 0) == 0);
}

TEST(device_reactivation_releases_a_running_press)
{
    Rig r;
    r.start_game();
    r.connect_settled();
    r.host_sends("press lighton");
    CHECK(r.frame().size() == 1); // down
    r.bridge.on_device_activated(r.now);
    // the game treats the device as all-zero after activation, so nothing is owed, and the press does not continue
    for (int i = 0; i < 8; ++i)
    {
        CHECK(r.frame().empty());
    }
}

TEST(outgoing_queue_overflow_fails_the_connection_instead_of_dropping_a_frame)
{
    Rig r;
    r.connect_settled();
    CHECK(!r.bridge.failed());
    // the host never reads: ping it more often than the queue can hold
    std::uint8_t frame[2 + bbs::kMaxPayload];
    const size_t n = bbs::encode_frame("ping 1", frame, sizeof frame);
    for (size_t i = 0; i < bbs::OutQueue::kCapacity; ++i)
    {
        CHECK(r.bridge.on_host_bytes(frame, n, r.now) == bbs::Bridge::HostResult::Ok);
    }
    CHECK(!r.bridge.failed()); // exactly full is fine
    CHECK(r.bridge.on_host_bytes(frame, n, r.now) == bbs::Bridge::HostResult::Ok);
    CHECK(r.bridge.failed());
    // the frames that did fit are intact and in order
    const std::vector<std::string> f = r.drain();
    CHECK(f.size() == bbs::OutQueue::kCapacity);
    // a reconnect resets the failure
    r.bridge.on_disconnect();
    CHECK(!r.bridge.failed());
    r.bridge.on_connect(r.now);
    CHECK(!r.bridge.failed());
}

TEST(reconnect_resends_a_full_snapshot)
{
    Rig r;
    r.start_game();
    r.bridge.on_channel_bool(kEngine, true);
    r.connect_settled();
    r.bridge.on_disconnect();
    r.bridge.on_connect(r.now);
    const std::vector<std::string> f = r.drain();
    CHECK(has(f, "tel truck.engine.enabled 1"));
}

TEST(a_press_after_a_background_poll_never_fires)
{
    Rig r;
    r.start_game();
    r.connect_settled();
    r.now += 600; // the game loses focus and polls only every 500 ms
    (void)r.frame(0);
    r.host_sends("press lighton"); // arrives right after a background poll
    CHECK(r.bridge.scheduler().queued() == 0);
    for (int i = 0; i < 4; ++i)
    {
        r.now += 500;
        CHECK(r.frame(0).empty());
        CHECK(!r.bridge.listening());
    }
    CHECK(!has(r.drain(), "listening 1")); // no flapping
    r.frame();                             // refocus: fast polls
    CHECK(r.bridge.listening());
}

TEST(a_connect_forgets_the_polls_so_listening_needs_two_close_polls)
{
    Rig r;
    r.start_game();
    r.bridge.on_connect(r.now);
    std::vector<std::string> f = r.drain();
    CHECK(has(f, "listening 0"));
    r.frame();
    CHECK(r.drain().empty());
    r.frame();
    f = r.drain();
    CHECK(f.size() == 1 && f[0] == "listening 1");
}
