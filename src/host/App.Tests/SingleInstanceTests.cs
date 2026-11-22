using Xunit;

namespace ButtonBox.App.Tests;

public sealed class SingleInstanceTests
{
    private static (string Mutex, string Event) UniqueNames()
    {
        var id = Guid.NewGuid().ToString("N");
        return ($@"Local\bb-test-instance-{id}", $@"Local\bb-test-open-{id}");
    }

    [Fact]
    public void The_second_instance_is_refused_while_the_first_runs()
    {
        // Two processes would fight over the one server key and the fixed ports.
        var (mutex, evt) = UniqueNames();
        using var first = new SingleInstance(mutex, evt);
        using var second = new SingleInstance(mutex, evt);

        Assert.True(first.TryAcquire());
        Assert.False(second.TryAcquire());
    }

    [Fact]
    public async Task The_second_instance_asks_the_first_to_open_its_editor()
    {
        var (mutex, evt) = UniqueNames();
        using var first = new SingleInstance(mutex, evt);
        using var second = new SingleInstance(mutex, evt);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(first.TryAcquire());
        first.StartListening(() => opened.TrySetResult());
        Assert.False(second.TryAcquire());

        second.SignalRunning();

        await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Every_further_launch_signals_again()
    {
        var (mutex, evt) = UniqueNames();
        using var first = new SingleInstance(mutex, evt);
        var count = 0;
        var gate = new SemaphoreSlim(0);
        Assert.True(first.TryAcquire());
        first.StartListening(() =>
        {
            Interlocked.Increment(ref count);
            gate.Release();
        });

        for (var i = 0; i < 3; i++)
        {
            using var other = new SingleInstance(mutex, evt);
            Assert.False(other.TryAcquire());
            other.SignalRunning();
            Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }

        Assert.Equal(3, count);
    }

    [Fact]
    public void A_signal_sent_before_the_first_listens_is_not_lost()
    {
        // The second launch may come up between the first taking the mutex and starting to listen.
        var (mutex, evt) = UniqueNames();
        using var first = new SingleInstance(mutex, evt);
        using var second = new SingleInstance(mutex, evt);
        Assert.True(first.TryAcquire());
        Assert.False(second.TryAcquire());
        second.SignalRunning();
        using var seen = new ManualResetEventSlim();

        first.StartListening(seen.Set);

        Assert.True(seen.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Once_the_first_instance_ends_a_new_one_can_start()
    {
        var (mutex, evt) = UniqueNames();
        var first = new SingleInstance(mutex, evt);
        Assert.True(first.TryAcquire());
        first.Dispose();

        using var next = new SingleInstance(mutex, evt);

        Assert.True(next.TryAcquire());
    }

    [Fact]
    public void Different_names_do_not_interfere_and_the_product_names_are_per_session()
    {
        var a = UniqueNames();
        var b = UniqueNames();
        using var one = new SingleInstance(a.Mutex, a.Event);
        using var two = new SingleInstance(b.Mutex, b.Event);

        Assert.True(one.TryAcquire());
        Assert.True(two.TryAcquire());
    }

    [Fact]
    public void A_listener_that_throws_does_not_end_the_listening()
    {
        var (mutex, evt) = UniqueNames();
        using var first = new SingleInstance(mutex, evt);
        using var second = new SingleInstance(mutex, evt);
        var calls = 0;
        using var again = new ManualResetEventSlim();
        Assert.True(first.TryAcquire());
        first.StartListening(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("boom");
            }

            again.Set();
        });

        second.SignalRunning();
        SpinWait.SpinUntil(() => Volatile.Read(ref calls) >= 1, TimeSpan.FromSeconds(10));
        second.SignalRunning();

        Assert.True(again.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }
}
