using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ButtonBox.Output.Scs.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace ButtonBox.Output.Scs.Tests;

/// <summary>An in-process stand-in for the game plugin: a real named-pipe server with the plugin's ACL.</summary>
internal sealed class FakePlugin : IDisposable
{
    private readonly NamedPipeServerStream _server;
    private readonly FrameReader _reader = new();
    private readonly byte[] _chunk = new byte[FrameReader.MaxChunk];

    public FakePlugin(string? name = null, PipeSecurity? security = null, int inBufferSize = 4096)
    {
        Name = name ?? NewName();
        _server = NamedPipeServerStreamAcl.Create(
            Name,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize,
            4096,
            security ?? UserOnly());
    }

    public string Name { get; }

    public NamedPipeServerStream Server => _server;

    public static string NewName() => "bbfs-test-" + Guid.NewGuid().ToString("N");

    /// <summary>Owner and the only allow ACE are the current user, like the real plugin's pipe.</summary>
    public static PipeSecurity UserOnly()
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var security = new PipeSecurity();
        security.SetOwner(me);
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.SetAccessRuleProtection(true, false);
        return security;
    }

    public async Task WaitForClientAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _server.WaitForConnectionAsync(cts.Token);
    }

    public Task SendAsync(string payload) => SendRawAsync(ScsProtocol.Frame(Encoding.ASCII.GetBytes(payload)));

    public async Task SendRawAsync(byte[] bytes)
    {
        try
        {
            await _server.WriteAsync(bytes);
        }
        catch (IOException)
        {
            // The host already closed the connection (e.g. it rejected an earlier message); the test checks that separately.
        }
    }

    /// <summary>The next payload as text; null when the client closed the pipe (or nothing arrived within 5 s and <paramref name="nullOnTimeout"/>).</summary>
    public async Task<string?> ReadPayloadAsync(bool nullOnTimeout = false)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                if (_reader.TryRead(out var payload) == FrameReadResult.Frame)
                {
                    return Encoding.ASCII.GetString(payload);
                }

                var read = await _server.ReadAsync(_chunk, cts.Token);
                if (read == 0)
                {
                    return null;
                }

                _reader.Push(_chunk.AsSpan(0, read));
            }
        }
        catch (OperationCanceledException) when (nullOnTimeout)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>True when the client has closed its end (a read returns 0 or fails) with no data pending.</summary>
    public async Task<bool> SawEndOfStreamAsync() => await ReadPayloadAsync() is null;

    public void Disconnect() => _server.Disconnect();

    public void Dispose() => _server.Dispose();

    /// <summary>The standard session start: hello, a few channels, avail and listening.</summary>
    public async Task SendSnapshotAsync(bool avail = true, bool listening = true)
    {
        await SendAsync("hello 1 ets2 1.0.0");
        await SendAsync("tel truck.engine.enabled 1");
        await SendAsync("tel truck.wipers 2");
        await SendAsync($"avail {(avail ? 1 : 0)}");
        await SendAsync($"listening {(listening ? 1 : 0)}");
    }
}

internal static class Wait
{
    /// <summary>Polls in real time until the condition holds; fails the test after the timeout.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(2);
        }
    }

    /// <summary>Advances the fake clock in small steps until the condition holds (the code under test sets its timers asynchronously).</summary>
    public static async Task PumpUntilAsync(FakeTimeProvider time, Func<bool> condition, string what, TimeSpan? step = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 10000)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            time.Advance(step ?? TimeSpan.FromMilliseconds(100));
            await Task.Delay(2);
        }
    }
}
