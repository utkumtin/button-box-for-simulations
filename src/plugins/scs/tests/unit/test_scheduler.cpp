// Press scheduler and listening detector tests (decisions §4.1, §10).

#include "test_util.h"

#include "listening.h"
#include "scheduler.h"
#include "tables.h"

#include <cstdint>
#include <string>

namespace
{

const size_t kLightOn = bbs::find_input("lighton");
const size_t kHighBeam = bbs::find_input("hblight");
const size_t kIgnitionStart = bbs::find_input("ignitionstrt");

// Drives a scheduler with polled frames at a fixed frame interval.
struct Frames
{
    bbs::PressScheduler scheduler;
    std::uint64_t now = 1000;
    std::uint64_t interval = 12; // about 80 fps

    // Runs one polled frame and returns which input is down in it as a string of 0/1 per tracked input.
    void frame()
    {
        now += interval;
        scheduler.on_frame(now);
    }

    // One character per frame for `input`: '1' down, '0' up.
    std::string run(size_t input, int frames)
    {
        std::string out;
        for (int i = 0; i < frames; ++i)
        {
            frame();
            out.push_back(scheduler.is_down(input) ? '1' : '0');
        }
        return out;
    }
};

} // namespace

TEST(press_is_three_frames_down_then_up)
{
    Frames f;
    CHECK(f.scheduler.submit(kLightOn, f.now, true));
    CHECK(f.run(kLightOn, 8) == "11100000");
    CHECK(!f.scheduler.busy());
}

TEST(presses_play_one_at_a_time_in_arrival_order_with_an_up_frame_between)
{
    Frames f;
    CHECK(f.scheduler.submit(kLightOn, f.now, true));
    CHECK(f.scheduler.submit(kHighBeam, f.now, true)); // lighton then hblight, as for HIGH from OFF
    std::string a;
    std::string b;
    for (int i = 0; i < 12; ++i)
    {
        f.frame();
        const bool a_down = f.scheduler.is_down(kLightOn);
        const bool b_down = f.scheduler.is_down(kHighBeam);
        CHECK(!(a_down && b_down)); // never two at once
        a.push_back(a_down ? '1' : '0');
        b.push_back(b_down ? '1' : '0');
    }
    // hblight goes down 4 frames after lighton (3 down + 1 up in between)
    CHECK(a == "111000000000");
    CHECK(b == "000011100000");
}

TEST(second_press_of_a_pair_is_not_dropped_at_a_slow_frame_rate)
{
    // 15 fps: the pair needs 4 frames = 264 ms from arrival to the start of the second press. The wait that counts
    // against the 250 ms limit starts when the press can first start, not when it arrived.
    Frames f;
    f.interval = 66;
    CHECK(f.scheduler.submit(kLightOn, f.now, true));
    CHECK(f.scheduler.submit(kHighBeam, f.now, true));
    std::string b = f.run(kHighBeam, 4); // frames 1-4: lighton down 3, up 1
    CHECK(b == "0000");
    b = f.run(kHighBeam, 4);
    CHECK(b == "1110");
}

TEST(press_that_waited_250ms_at_the_head_is_dropped)
{
    // Arrives while idle, the game does not poll for a while: the press must not fire on the next poll.
    {
        Frames f;
        CHECK(f.scheduler.submit(kLightOn, f.now, true));
        f.now += 249;
        f.scheduler.on_frame(f.now);
        CHECK(f.scheduler.is_down(kLightOn)); // 249 ms: still fresh
    }
    {
        Frames f;
        CHECK(f.scheduler.submit(kLightOn, f.now, true));
        f.now += 250;
        f.scheduler.on_frame(f.now);
        CHECK(!f.scheduler.is_down(kLightOn)); // 250 ms: dropped
        CHECK(f.run(kLightOn, 6) == "000000");
        CHECK(f.scheduler.queued() == 0);
    }
}

TEST(every_stale_press_in_the_queue_is_dropped_not_just_the_head)
{
    Frames f;
    CHECK(f.scheduler.submit(kLightOn, f.now, true));
    CHECK(f.scheduler.submit(kHighBeam, f.now, true));
    f.now += 400; // the game did not poll
    f.scheduler.on_frame(f.now);
    CHECK(!f.scheduler.is_down(kLightOn));
    CHECK(!f.scheduler.is_down(kHighBeam));
    CHECK(f.scheduler.queued() == 0);
    CHECK(f.run(kHighBeam, 6) == "000000");
}

TEST(press_arriving_while_not_listening_is_dropped)
{
    Frames f;
    CHECK(!f.scheduler.submit(kLightOn, f.now, false));
    CHECK(f.scheduler.queued() == 0);
    CHECK(f.run(kLightOn, 6) == "000000");
}

TEST(queue_is_discarded_when_listening_drops)
{
    Frames f;
    CHECK(f.scheduler.submit(kLightOn, f.now, true));
    CHECK(f.scheduler.submit(kHighBeam, f.now, true));
    CHECK(f.scheduler.submit(kIgnitionStart, f.now, true));
    f.scheduler.on_listening_lost();
    CHECK(f.scheduler.queued() == 0);
    CHECK(f.run(kLightOn, 4) == "0000");
    CHECK(f.run(kHighBeam, 4) == "0000");
    CHECK(f.run(kIgnitionStart, 4) == "0000");
}

TEST(listening_drop_releases_the_running_press_and_waits_one_up_frame)
{
    Frames f;
    CHECK(f.scheduler.submit(kIgnitionStart, f.now, true));
    CHECK(f.run(kIgnitionStart, 1) == "1");
    f.scheduler.on_listening_lost();
    CHECK(f.scheduler.queued() == 0);
    // the same press again right away must not run into the old one: the game sees an up frame first
    CHECK(f.scheduler.submit(kIgnitionStart, f.now, true));
    CHECK(f.run(kIgnitionStart, 6) == "011100");
}

TEST(release_all_mid_press_ends_it_and_clears_the_queue)
{
    Frames f;
    CHECK(f.scheduler.submit(kLightOn, f.now, true));
    CHECK(f.scheduler.submit(kHighBeam, f.now, true));
    CHECK(f.run(kLightOn, 2) == "11");
    f.scheduler.release_all();
    CHECK(f.scheduler.queued() == 0);
    CHECK(!f.scheduler.is_down(kLightOn));
    CHECK(f.run(kLightOn, 3) == "000");
    CHECK(f.run(kHighBeam, 3) == "000");
    CHECK(!f.scheduler.busy());
}

TEST(release_all_when_idle_changes_nothing)
{
    Frames f;
    f.scheduler.release_all();
    CHECK(f.scheduler.submit(kLightOn, f.now, true));
    CHECK(f.run(kLightOn, 5) == "11100"); // no extra up frame is inserted for an idle scheduler
}

TEST(queue_is_bounded)
{
    Frames f;
    int accepted = 0;
    for (size_t i = 0; i < bbs::PressScheduler::kQueueCapacity + 10; ++i)
    {
        accepted += f.scheduler.submit(kLightOn, f.now, true) ? 1 : 0;
    }
    CHECK(accepted == static_cast<int>(bbs::PressScheduler::kQueueCapacity));
    CHECK(f.scheduler.queued() == bbs::PressScheduler::kQueueCapacity);
}

TEST(press_of_an_unknown_input_index_is_refused)
{
    Frames f;
    CHECK(!f.scheduler.submit(bbs::kInputCount, f.now, true));
    CHECK(!f.scheduler.submit(bbs::kNotFound, f.now, true));
    CHECK(f.scheduler.queued() == 0);
}

TEST(listening_needs_a_poll_within_250ms_and_no_pause)
{
    bbs::ListeningDetector d;
    d.set_paused(false);
    CHECK(!d.listening(1000)); // never polled
    d.on_poll(1000);
    CHECK(!d.listening(1000)); // one poll is not steady yet
    d.on_poll(1012);
    CHECK(d.listening(1012));
    CHECK(d.listening(1262));  // a 250 ms gap still listens
    CHECK(!d.listening(1263)); // a 251 ms gap does not
    d.on_poll(1400);           // one poll after the gap
    CHECK(!d.listening(1400));
    d.on_poll(1412); // recovery with the second fast poll
    CHECK(d.listening(1412));
    CHECK(d.listening(1662));
    d.set_paused(true);
    CHECK(!d.listening(1412)); // paused wins over fresh polls
    d.set_paused(false);
    CHECK(d.listening(1412));
}

TEST(listening_starts_paused_like_the_sdk_does)
{
    bbs::ListeningDetector d;
    d.on_poll(5);
    d.on_poll(10);
    CHECK(!d.listening(10)); // telemetry starts paused until "started"
    d.set_paused(false);
    CHECK(d.listening(10));
}

TEST(listening_survives_a_clock_that_reads_slightly_behind_the_last_poll)
{
    // The worker reads the clock before taking the lock; the game thread may have polled a moment later.
    bbs::ListeningDetector d;
    d.set_paused(false);
    d.on_poll(1990);
    d.on_poll(2000);
    CHECK(d.listening(1999));
}

TEST(reset_polling_forgets_the_polls)
{
    bbs::ListeningDetector d;
    d.set_paused(false);
    d.on_poll(10);
    d.on_poll(20);
    CHECK(d.listening(20));
    d.reset_polling();
    CHECK(!d.listening(20));
    d.on_poll(30); // the history starts again: one poll is not enough
    CHECK(!d.listening(30));
}

TEST(a_game_polling_every_500ms_never_listens)
{
    // Unfocused ETS2/ATS polls about every 500 ms. The old rule reported listening 1 after each poll and 0 again 250 ms
    // later; the phone would flicker twice a second.
    bbs::ListeningDetector d;
    d.set_paused(false);
    std::uint64_t now = 1000;
    for (int poll = 0; poll < 12; ++poll)
    {
        d.on_poll(now);
        for (std::uint64_t t = 0; t <= 500; t += 10)
        {
            CHECK(!d.listening(now + t));
        }
        now += 500;
    }
}

TEST(two_fast_polls_after_background_polling_make_it_listening)
{
    bbs::ListeningDetector d;
    d.set_paused(false);
    d.on_poll(1000);
    d.on_poll(1500); // background
    CHECK(!d.listening(1500));
    d.on_poll(1512); // refocus: the second fast poll
    CHECK(d.listening(1512));
    CHECK(d.listening(1762)); // 250 ms after the last poll still counts
    CHECK(!d.listening(1763));
}

TEST(the_gap_between_the_last_two_polls_of_exactly_250ms_counts_as_steady)
{
    bbs::ListeningDetector d;
    d.set_paused(false);
    d.on_poll(1000);
    d.on_poll(1250);
    CHECK(d.listening(1250));
    d.on_poll(1501); // 251 ms since the previous poll: not steady
    CHECK(!d.listening(1501));
}
