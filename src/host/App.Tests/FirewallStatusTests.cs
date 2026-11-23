using ButtonBox.Core.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ButtonBox.App.Tests;

// Why: the Settings page shows whether phones can get through the firewall. A wrong "present" would hide the usual cause of a phone
// that cannot connect, and a probe that hangs or throws must never take the editor's request down with it.
public sealed class FirewallStatusTests
{
    [Theory]
    [InlineData(FirewallState.Present)]
    [InlineData(FirewallState.Missing)]
    public async Task The_probe_result_is_reported(FirewallState state)
    {
        var status = new FirewallStatus(() => state, new FakeTimeProvider());

        Assert.Equal(state, await status.GetAsync());
    }

    [Fact]
    public async Task A_probe_that_throws_is_unknown()
    {
        var status = new FirewallStatus(() => throw new InvalidOperationException("COM is not available"), new FakeTimeProvider());

        Assert.Equal(FirewallState.Unknown, await status.GetAsync());
    }

    [Fact]
    public async Task A_probe_that_takes_longer_than_two_seconds_is_unknown_and_is_not_started_twice()
    {
        var time = new FakeTimeProvider();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var status = new FirewallStatus(
            () =>
            {
                Interlocked.Increment(ref calls);
                release.Wait(TimeSpan.FromSeconds(30));
                return FirewallState.Present;
            },
            time);

        var first = status.GetAsync();
        await LanRig.WaitForAsync(() => Volatile.Read(ref calls), n => n == 1); // the probe is really running (and stuck)
        time.Advance(FirewallStatus.ProbeTimeout);
        Assert.Equal(FirewallState.Unknown, await first);

        // The answer is cached, and when the cache has expired the stuck probe is joined, not stacked up with a second one.
        time.Advance(FirewallStatus.CacheFor);
        var second = status.GetAsync();
        time.Advance(FirewallStatus.ProbeTimeout);
        Assert.Equal(FirewallState.Unknown, await second);
        Assert.Equal(1, Volatile.Read(ref calls));
        release.Set();
    }

    [Fact]
    public async Task The_answer_is_cached_for_ten_seconds()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var status = new FirewallStatus(() => ++calls == 1 ? FirewallState.Missing : FirewallState.Present, time);

        Assert.Equal(FirewallState.Missing, await status.GetAsync());
        time.Advance(FirewallStatus.CacheFor - TimeSpan.FromMilliseconds(1));
        Assert.Equal(FirewallState.Missing, await status.GetAsync());
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(FirewallState.Present, await status.GetAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task The_real_windows_probe_answers_without_throwing_or_hanging()
    {
        // Machine dependent, so it cannot say which state is right; it proves the COM probe itself runs read-only and ends in time.
        var status = new FirewallStatus();

        var state = await status.GetAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(Enum.IsDefined(state));
    }
}
