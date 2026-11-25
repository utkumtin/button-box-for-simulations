using System.Text.Json;
using System.Threading.Channels;
using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;

namespace ButtonBox.App.Lan;

/// <summary>
/// Between the phones and the game (decisions §3 "Gesture handling"): maps gestures through the engine, sends the resulting
/// outputs to the backend and fans telemetry out to every phone. One per app.
/// The receive loops never wait for the game: outputs go into one bounded queue (<see cref="OutputQueueCapacity"/>), one consumer
/// feeds the backend in order, and an output that does not fit is dropped (presses are never queued up behind a stuck game).
/// The active profile can change while phones are connected (<see cref="Publish"/>): every phone gets the new layout in place.
///
/// Lock order: the session lock (<c>_gate</c>) is taken first, then a layout engine's lock or the state provider's lock (a gesture reads
/// <c>_state.Current</c> under it). The handlers of <see cref="IStateProvider.Changed"/> and the timer take the session lock, so a provider
/// must never raise <c>Changed</c> while holding its own lock, or a gesture and a telemetry change would deadlock.
/// Widget state also changes without a gesture or a telemetry change (the amber timeout, the end of an inference pause, decisions §5):
/// one timer on the session's clock fires at the earliest <see cref="LayoutEngine.NextDeadline"/> and marks the phones concerned.
/// </summary>
public sealed class GameSession : IAsyncDisposable
{
    /// <summary>Capacity of the output queue, counted in gestures: the outputs of one gesture enter together or not at all.</summary>
    public const int OutputQueueCapacity = 32;

    /// <summary>The consumer stops waiting for one press after this long and moves on (decisions §3).</summary>
    public static readonly TimeSpan PressTimeout = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);
    private static readonly byte[] NoLayoutJson = Serialize(new NoLayoutMessage());

    private readonly IOutputBackend _backend;
    private readonly IStateProvider _state;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;

    // Mode Wait, not DropWrite: with Wait a full channel makes TryWrite return false, so a drop is observable. DropWrite
    // would return true and silently discard.
    private readonly Channel<IReadOnlyList<GameOutput>> _queue = Channel.CreateBounded<IReadOnlyList<GameOutput>>(new BoundedChannelOptions(OutputQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    private readonly CancellationTokenSource _cancel = new();

    // Guards the active generation, the links array and every link's view. Held for reference swaps, dictionary lookups and one
    // engine call (CPU only, no I/O, no awaits): Publish, Open and Handle see each other's effects as a whole, never half.
    private readonly object _gate = new();
    private readonly Task _consumer;
    private Generation _generation;
    private PhoneLink[] _links = [];
    private long _epoch;
    private int _disposed;

    /// <param name="engine">The active profile's engine, or null when there is no (usable) active profile: every phone gets noLayout.</param>
    /// <param name="backend">Where the outputs go.</param>
    /// <param name="state">Telemetry for status and state frames.</param>
    /// <param name="time">The clock for the press timeout and the widget timer; null is the system clock. Give the engine the same clock.</param>
    public GameSession(ProfileEngine? engine, IOutputBackend backend, IStateProvider state, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(state);
        _generation = new Generation(engine);
        _backend = backend;
        _state = state;
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        lock (_gate)
        {
            // The engine may be handed over after telemetry started: its knobs start from what the game shows now.
            ObserveCurrentGeneration(_state.Current);
            ArmTimer();
        }

        _state.Changed += OnStateChanged;
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>The engine of the active profile, or null when there is none.</summary>
    public ProfileEngine? Engine => Volatile.Read(ref _generation).Engine;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _state.Changed -= OnStateChanged;
        lock (_gate)
        {
            _timer.Dispose();
        }

        _queue.Writer.TryComplete();
        await _cancel.CancelAsync();
        try
        {
            // A backend that ignores the token must not hold the shutdown.
            await _consumer.WaitAsync(StopTimeout);
        }
        catch (TimeoutException)
        {
            // Abandoned: the consumer ends whenever the backend returns.
        }
    }

    /// <summary>
    /// Makes <paramref name="engine"/> the active one (null: no active profile) and updates every connected phone in place: new
    /// layout or noLayout, then a fresh status and state (decisions §3 "Publishing"). Never waits for a socket or a slow phone:
    /// it swaps references and marks flags, so it is safe on the UI thread and in request handlers.
    /// </summary>
    /// <param name="engine">The new engine. Publishing the engine that is already active with <paramref name="keepSwitchPositions"/> is a no-op.</param>
    /// <param name="keepSwitchPositions">True on Save (positions carry over where the switch is unchanged), false on a profile switch (every switch resets, also in a reused engine).</param>
    public void Publish(ProfileEngine? engine, bool keepSwitchPositions)
    {
        PhoneLink[] links;
        lock (_gate)
        {
            var previous = _generation.Engine;
            if (keepSwitchPositions && ReferenceEquals(previous, engine))
            {
                return;
            }

            // Under the same lock as Handle: no gesture changes a position of the old engine between this copy and the swap.
            if (!keepSwitchPositions)
            {
                engine?.ResetSwitchPositions();
            }
            else if (previous is not null)
            {
                engine?.CarryPositionsFrom(previous);
            }

            // The layout-frame cache belongs to the generation: a new engine never serves a frame of the old one.
            _generation = new Generation(engine);
            ObserveCurrentGeneration(_state.Current); // the knobs of the new engine start from what the game shows now
            ArmTimer();
            links = _links;
            foreach (var link in links)
            {
                link.View = NewView(link.TemplateId);
            }
        }

        // Marks only set flags and wake the phone's pump. The layout, status and state frames are built there, from the new view.
        foreach (var link in links)
        {
            link.Sender.MarkLayout();
        }
    }

    /// <summary>
    /// Editor test mode (decisions §11): sends a catalog action of the active profile to the game as if a phone had pressed it. It
    /// uses the same bounded queue as phone presses and skips only the "game not listening" check, so a test works while the game
    /// is paused. Never waits for the game; safe on any thread.
    /// </summary>
    public TestPressResult TestPress(string actionId)
    {
        ArgumentNullException.ThrowIfNull(actionId);
        if (Engine is not { } engine)
        {
            return TestPressResult.NoActiveProfile;
        }

        if (!engine.TryGetOutput(actionId, out var output))
        {
            return TestPressResult.UnknownAction;
        }

        return _queue.Writer.TryWrite([output]) ? TestPressResult.Sent : TestPressResult.QueueFull;
    }

    /// <summary>Puts the phone into the fan-out. Until its sender has been started the marks are only flags and nothing is sent.</summary>
    internal PhoneLink Open(string templateId, FrameSender sender)
    {
        // The engine lookup and the join are one step under the lock Publish swaps under: a phone that connects while a publish runs
        // either joins before it (and is swapped by it) or after it (and starts on the new engine). It can never keep the old one.
        lock (_gate)
        {
            var link = new PhoneLink(this, templateId, sender, NewView(templateId));
            _links = [.. _links, link];
            return link;
        }
    }

    internal void Leave(PhoneLink link)
    {
        lock (_gate)
        {
            _links = [.. _links.Where(l => !ReferenceEquals(l, link))];
        }
    }

    /// <summary>
    /// A validated gesture or select from a phone. Never waits for the game and never throws for a bad widget or gesture:
    /// a stale message is dropped, not a reason to close the connection.
    /// </summary>
    internal void Handle(PhoneLink link, ClientMessage message)
    {
        HandleResult result;
        lock (_gate)
        {
            // The layout is read once and used in the same step a publish would swap it in: a gesture is judged against exactly one
            // layout, and one that does not fit the new layout is Invalid there and dropped. A position it commands is in the engine
            // Publish carries over from, never lost in between.
            if (link.View.Layout is not { } layout)
            {
                return;
            }

            result = layout.Handle(message, _state.Current);
            if (result.Outcome == HandleOutcome.Accepted)
            {
                ArmTimer(); // the gesture may have started an amber timer or an inference pause
            }
        }

        if (result.Outcome != HandleOutcome.Accepted)
        {
            return;
        }

        // A gesture accepted just before a publish still reaches the game: it was valid for the layout the user touched, and the
        // game cannot take a press back. Acceptable; only the phone's own view is switched.
        // The outputs of one gesture enter the queue together or not at all: a full queue drops the whole gesture.
        if (result.Outputs.Count > 0)
        {
            _queue.Writer.TryWrite(result.Outputs); // false = full = dropped, by design
        }

        if (result.StateChanged)
        {
            // Devices with the same template share the commanded switch positions.
            foreach (var other in Volatile.Read(ref _links))
            {
                if (other.TemplateId == link.TemplateId && other.View.Layout is not null)
                {
                    other.Sender.MarkState();
                }
            }
        }
    }

    internal byte[] BuildStatus(LayoutEngine? layout)
    {
        var snapshot = _state.Current;
        // A phone without a layout still shows the game's status.
        return Serialize(layout?.Status(snapshot) ?? new StatusMessage { Telemetry = snapshot.Available, GameListening = snapshot.GameListening });
    }

    internal byte[] BuildState(LayoutEngine layout) => Serialize(layout.Project(_state.Current));

    internal static byte[] Serialize(HostMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, ContractJson.Options);

    // Must be called under _gate.
    private LinkView NewView(string templateId)
    {
        var generation = _generation;
        var layout = generation.Engine?.ForTemplate(templateId);
        var frame = layout is null ? NoLayoutJson : generation.LayoutFrame(layout);
        return new LinkView(layout, frame, ++_epoch);
    }

    // Runs on the provider's threads. It takes the session lock (see the class remarks), moves knobs that telemetry moved, marks flags
    // and nothing else, so a keyboard change moves a knob in the same state frame as the telemetry change.
    private void OnStateChanged(object? sender, TelemetrySnapshot snapshot)
    {
        try
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                ObserveCurrentGeneration(_state.Current); // not the event's snapshot: providers may raise from several threads, so an older one can arrive late
                ArmTimer();
                foreach (var link in _links)
                {
                    link.Sender.MarkStatus();
                    if (link.View.Layout is not null)
                    {
                        link.Sender.MarkState();
                    }
                }
            }
        }
        catch (Exception)
        {
            // A failing handler must not break the state provider.
        }
    }

    // Runs on the session clock's timer thread. Never touches a socket and never blocks: it observes, marks flags and re-arms.
    private void OnTimer()
    {
        try
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                var changed = ObserveCurrentGeneration(_state.Current); // the newest state, read under the lock like a gesture does
                foreach (var link in _links)
                {
                    if (link.View.Layout is { } layout && changed.Contains(layout))
                    {
                        link.Sender.MarkState();
                    }
                }

                ArmTimer();
            }
        }
        catch (Exception)
        {
            // A failing timer callback must not take the process down.
        }
    }

    // Must be called under _gate. Returns the layout engines whose phone-visible state changed.
    private HashSet<LayoutEngine> ObserveCurrentGeneration(TelemetrySnapshot snapshot)
    {
        HashSet<LayoutEngine> changed = [];
        if (_generation.Engine is { } engine)
        {
            foreach (var layout in engine.Layouts)
            {
                if (layout.Observe(snapshot))
                {
                    changed.Add(layout);
                }
            }
        }

        return changed;
    }

    // Must be called under _gate. One timer for the earliest deadline of the current generation's layout engines. It is re-armed after
    // every gesture, publish, telemetry change and firing, so a deadline that Observe has handled never keeps the timer running.
    private void ArmTimer()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        DateTimeOffset? next = null;
        if (_generation.Engine is { } engine)
        {
            foreach (var layout in engine.Layouts)
            {
                if (layout.NextDeadline is { } deadline && (next is null || deadline < next))
                {
                    next = deadline;
                }
            }
        }

        if (next is null)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        // Bounded: a timer cannot be armed beyond ~49 days and would throw inside a gesture. Real deadlines are seconds away; a far one
        // (engine and session on different clocks) just wakes the timer, which observes nothing and re-arms.
        var due = next.Value - _time.GetUtcNow();
        _timer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due > MaxTimerDue ? MaxTimerDue : due, Timeout.InfiniteTimeSpan);
    }

    private static readonly TimeSpan MaxTimerDue = TimeSpan.FromHours(1);

    // An abandoned press that fails later must not end up as an unobserved task exception.
    private static void ObserveLater(Task press) =>
        _ = press.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private async Task ConsumeAsync()
    {
        var token = _cancel.Token;
        try
        {
            await foreach (var gesture in _queue.Reader.ReadAllAsync(token))
            {
                foreach (var output in gesture)
                {
                    try
                    {
                        // A press the game never answers is abandoned after PressTimeout and the consumer moves on. The abandoned call
                        // may still finish later in the backend: backends must tolerate overlapping calls.
                        var press = _backend.PressAsync(output, token);
                        ObserveLater(press);
                        await press.WaitAsync(PressTimeout, _time, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        // One failed or abandoned press must not stop the ones behind it.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}

/// <summary>The result of <see cref="GameSession.TestPress"/>.</summary>
public enum TestPressResult
{
    /// <summary>The output is in the queue to the game.</summary>
    Sent,

    /// <summary>There is no active profile, so there is no catalog to map the action through.</summary>
    NoActiveProfile,

    /// <summary>The active profile's catalog has no such action.</summary>
    UnknownAction,

    /// <summary>The queue to the game is full (a stuck game): dropped, like a phone press.</summary>
    QueueFull,
}

/// <summary>
/// What the host has published for one engine. Its layout-frame cache lives and dies with it, so a Save with the same template
/// never serves the frame of the previous engine.
/// </summary>
internal sealed class Generation(ProfileEngine? engine)
{
    private readonly Dictionary<string, byte[]> _frames = new(StringComparer.Ordinal);

    public ProfileEngine? Engine { get; } = engine;

    // Called under GameSession's lock only.
    public byte[] LayoutFrame(LayoutEngine layout)
    {
        var template = layout.Layout.TemplateId;
        if (!_frames.TryGetValue(template, out var frame))
        {
            frame = GameSession.Serialize(new LayoutMessage { Layout = layout.Layout });
            _frames[template] = frame;
        }

        return frame;
    }
}

/// <summary>
/// The layout of one phone as one immutable unit: the layout engine (null: no layout), the serialized <c>layout</c> or
/// <c>noLayout</c> message and an epoch that is unique per view. Everything built for a phone is built from one view, read once.
/// </summary>
internal sealed record LinkView(LayoutEngine? Layout, byte[] LayoutFrame, long Epoch);

/// <summary>One phone in the fan-out: its template, its current <see cref="LinkView"/> and its sender.</summary>
internal sealed class PhoneLink(GameSession owner, string templateId, FrameSender sender, LinkView view) : IDisposable
{
    private LinkView _view = view;

    // Epoch of the layout frame the pump built last. Only the sender's one pump calls the Build methods, in slot order.
    private long _sentEpoch = -1;

    public string TemplateId { get; } = templateId;

    public FrameSender Sender { get; } = sender;

    /// <summary>Read it once per use. Replaced by <see cref="GameSession.Publish"/> under the session lock.</summary>
    public LinkView View
    {
        get => Volatile.Read(ref _view);
        set => Volatile.Write(ref _view, value);
    }

    /// <summary>The <c>layout</c> or <c>noLayout</c> frame of the current view. Remembers which view the phone now shows.</summary>
    public byte[]? BuildLayout()
    {
        var view = View;
        _sentEpoch = view.Epoch;
        return view.LayoutFrame;
    }

    public byte[]? BuildStatus() => owner.BuildStatus(View.Layout);

    /// <summary>
    /// The state for the layout the phone was last sent, or null (nothing to send). Null without a layout, and also when a publish
    /// replaced the view after the phone's last layout frame: that publish marked layout, status and state, so the new layout and a
    /// fresh state follow. A state frame is therefore never built for a layout the phone has not received yet, and never sent after
    /// the frame of a newer layout (the pump builds and sends one frame at a time, in slot order).
    /// </summary>
    public byte[]? BuildState()
    {
        var view = View;
        if (view.Layout is null || view.Epoch != _sentEpoch)
        {
            return null;
        }

        return owner.BuildState(view.Layout);
    }

    public void Handle(ClientMessage message) => owner.Handle(this, message);

    public void Dispose() => owner.Leave(this);
}
