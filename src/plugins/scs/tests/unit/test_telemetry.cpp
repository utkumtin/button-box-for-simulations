// Telemetry mapping and change detection tests (decisions §4.1).

#include "test_util.h"

#include "tables.h"
#include "telemetry.h"

#include <cmath>
#include <cstdint>
#include <limits>
#include <vector>

namespace
{

struct Change
{
    size_t channel;
    std::int32_t value;
};

std::vector<Change> changes_of(bbs::TelemetryStore &store)
{
    std::vector<Change> out;
    store.changes([&out](size_t channel, std::int32_t value) { out.push_back({channel, value}); });
    return out;
}

std::vector<Change> snapshot_of(bbs::TelemetryStore &store)
{
    std::vector<Change> out;
    store.snapshot([&out](size_t channel, std::int32_t value) { out.push_back({channel, value}); });
    return out;
}

} // namespace

TEST(bool_maps_to_zero_or_one)
{
    CHECK(bbs::map_bool(false) == 0);
    CHECK(bbs::map_bool(true) == 1);
}

TEST(u32_maps_to_its_value_clamped_to_int32)
{
    CHECK(bbs::map_u32(0) == 0);
    CHECK(bbs::map_u32(3) == 3);
    CHECK(bbs::map_u32(2147483647u) == 2147483647);
    CHECK(bbs::map_u32(2147483648u) == 2147483647);
    CHECK(bbs::map_u32(4294967295u) == 2147483647);
}

TEST(positive_is_one_only_while_the_float_is_above_zero)
{
    // truck.cruise_control: the set speed in m/s, 0 = off.
    CHECK(bbs::map_positive(0.0f) == 0);
    CHECK(bbs::map_positive(-0.0f) == 0);
    CHECK(bbs::map_positive(-5.0f) == 0);
    CHECK(bbs::map_positive(0.0001f) == 1);
    CHECK(bbs::map_positive(22.2f) == 1);
    CHECK(bbs::map_positive(std::numeric_limits<float>::infinity()) == 1);
    CHECK(bbs::map_positive(std::nanf("")) == 0);
}

TEST(a_new_value_is_reported_once_then_only_when_it_changes)
{
    bbs::TelemetryStore store;
    CHECK(changes_of(store).empty()); // no value, nothing to report

    store.set_bool(2, true);
    std::vector<Change> c = changes_of(store);
    CHECK(c.size() == 1 && c[0].channel == 2 && c[0].value == 1);
    CHECK(changes_of(store).empty());

    store.set_bool(2, true); // same value again
    CHECK(changes_of(store).empty());

    store.set_bool(2, false);
    c = changes_of(store);
    CHECK(c.size() == 1 && c[0].channel == 2 && c[0].value == 0);
}

TEST(several_channels_change_in_channel_order)
{
    bbs::TelemetryStore store;
    store.set_bool(4, true);
    store.set_bool(1, false);
    store.set_u32(9, 3);
    const std::vector<Change> c = changes_of(store);
    CHECK(c.size() == 3);
    CHECK(c.size() == 3 && c[0].channel == 1 && c[1].channel == 4 && c[2].channel == 9);
    CHECK(c.size() == 3 && c[0].value == 0 && c[1].value == 1 && c[2].value == 3);
}

TEST(a_channel_that_lost_its_value_is_not_reported_and_a_repeat_is_not_resent)
{
    bbs::TelemetryStore store;
    store.set_bool(0, true);
    CHECK(changes_of(store).size() == 1);

    store.set_none(0); // truck gone: the host learns this from "avail", not from a tel
    CHECK(!store.has_value(0));
    CHECK(changes_of(store).empty());

    store.set_bool(0, true); // the same value as the host already has
    CHECK(changes_of(store).empty());

    store.set_none(0);
    store.set_bool(0, false); // a different value must be sent
    const std::vector<Change> c = changes_of(store);
    CHECK(c.size() == 1 && c[0].value == 0);
}

TEST(snapshot_has_only_channels_with_a_value)
{
    bbs::TelemetryStore store;
    store.set_bool(3, true);
    store.set_u32(5, 2);
    store.set_none(7);
    store.set_bool(8, false);
    store.set_none(8);
    const std::vector<Change> s = snapshot_of(store);
    CHECK(s.size() == 2);
    CHECK(s.size() == 2 && s[0].channel == 3 && s[0].value == 1 && s[1].channel == 5 && s[1].value == 2);
}

TEST(a_new_connection_gets_every_value_again)
{
    bbs::TelemetryStore store;
    store.set_bool(0, true);
    store.set_bool(1, false);
    CHECK(changes_of(store).size() == 2);
    CHECK(changes_of(store).empty());

    store.forget_reported();
    CHECK(snapshot_of(store).size() == 2);
    CHECK(changes_of(store).empty()); // the snapshot counts as reported
}

TEST(the_snapshot_and_the_changes_do_not_overlap_or_lose_a_value)
{
    bbs::TelemetryStore store;
    store.set_bool(0, true);
    (void)snapshot_of(store);
    store.set_bool(0, false);
    const std::vector<Change> c = changes_of(store);
    CHECK(c.size() == 1 && c[0].value == 0);
}

TEST(clear_values_forgets_everything)
{
    bbs::TelemetryStore store;
    store.set_bool(0, true);
    store.clear_values();
    CHECK(!store.has_value(0));
    CHECK(snapshot_of(store).empty());
}

TEST(out_of_range_channels_are_ignored)
{
    bbs::TelemetryStore store;
    store.set_bool(bbs::kChannelCount, true);
    store.set_u32(bbs::kNotFound, 5);
    store.set_none(bbs::kChannelCount + 3);
    CHECK(!store.has_value(bbs::kChannelCount));
    CHECK(snapshot_of(store).empty());
}
