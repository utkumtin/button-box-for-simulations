using System.Collections.Concurrent;
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
/// </summary>
public sealed class GameSession : IAsyncDisposable
{
    public const int OutputQueueCapacity = 32;

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);
    private static readonly byte[] NoLayoutJson = Serialize(new NoLayoutMessage());

    private readonly ProfileEngine _engine;
    private readonly IOutputBackend _backend;
    private readonly IStateProvider _state;
    private readonly ConcurrentDictionary<string, byte[]> _layoutFrames = new(StringComparer.Ordinal);

    // Mode Wait, not DropWrite: with Wait a full channel makes TryWrite return false, so a drop is observable. DropWrite
    // would return true and silently discard.
    private readonly Channel<GameOutput> _queue = Channel.CreateBounded<GameOutput>(new BoundedChannelOptions(OutputQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });

    private readonly CancellationTokenSource _cancel = new();
    private readonly object _gate = new();
    private readonly Task _consumer;
    private PhoneLink[] _links = [];
    private int _disposed;

    public GameSession(ProfileEngine engine, IOutputBackend backend, IStateProvider state)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(state);
        _engine = engine;
        _backend = backend;
        _state = state;
        _state.Changed += OnStateChanged;
        _consumer = Task.Run(ConsumeAsync);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _state.Changed -= OnStateChanged;
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

    /// <summary>Puts the phone into the fan-out. Until its sender has been started the marks are only flags and nothing is sent.</summary>
    internal PhoneLink Open(string templateId, FrameSender sender)
    {
        var layout = _engine.ForTemplate(templateId);
        var link = new PhoneLink(this, templateId, layout, layout is null ? NoLayoutJson : LayoutFrame(layout), sender);
        lock (_gate)
        {
            _links = [.. _links, link];
        }

        return link;
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
        if (link.Layout is not { } layout)
        {
            return;
        }

        var result = layout.Handle(message, _state.Current);
        if (result.Outcome != HandleOutcome.Accepted)
        {
            return;
        }

        foreach (var output in result.Outputs)
        {
            _queue.Writer.TryWrite(output); // false = full = dropped, by design
        }

        if (result.StateChanged)
        {
            // Devices with the same template share the commanded switch positions.
            foreach (var other in Volatile.Read(ref _links))
            {
                if (other.TemplateId == link.TemplateId && other.Layout is not null)
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

    private static byte[] Serialize(HostMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, ContractJson.Options);

    private byte[] LayoutFrame(LayoutEngine layout) =>
        _layoutFrames.GetOrAdd(layout.Layout.TemplateId, _ => Serialize(new LayoutMessage { Layout = layout.Layout }));

    // Runs on the Simulator's timer threads and on the consumer: it marks flags and nothing else.
    private void OnStateChanged(object? sender, TelemetrySnapshot snapshot)
    {
        try
        {
            foreach (var link in Volatile.Read(ref _links))
            {
                link.Sender.MarkStatus();
                if (link.Layout is not null)
                {
                    link.Sender.MarkState();
                }
            }
        }
        catch (Exception)
        {
            // A failing handler must not break the state provider.
        }
    }

    private async Task ConsumeAsync()
    {
        var token = _cancel.Token;
        try
        {
            await foreach (var output in _queue.Reader.ReadAllAsync(token))
            {
                try
                {
                    await _backend.PressAsync(output, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // One failed press must not stop the ones behind it.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}

/// <summary>One phone in the fan-out: its template, its layout engine (null: no layout) and its sender.</summary>
internal sealed class PhoneLink(GameSession owner, string templateId, LayoutEngine? layout, byte[] layoutFrame, FrameSender sender) : IDisposable
{
    public string TemplateId { get; } = templateId;

    public LayoutEngine? Layout { get; } = layout;

    /// <summary>The serialized <c>layout</c> or <c>noLayout</c> message.</summary>
    public byte[] LayoutFrame { get; } = layoutFrame;

    public FrameSender Sender { get; } = sender;

    public byte[] BuildStatus() => owner.BuildStatus(Layout);

    public byte[] BuildState() => owner.BuildState(Layout!);

    public void Handle(ClientMessage message) => owner.Handle(this, message);

    public void Dispose() => owner.Leave(this);
}
