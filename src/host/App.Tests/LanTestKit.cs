using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ButtonBox.App.Lan;
using ButtonBox.App.Pairing;
using ButtonBox.Security;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ButtonBox.App.Tests;

/// <summary>
/// Everything a LAN test needs: a temp data directory, a real CA with a unique CNG key name (deleted again), a registry,
/// templates and a fake clock. Never touches %AppData%, TCP 443/80/8080 or UDP 5353.
/// </summary>
internal sealed class LanFixture : IAsyncDisposable
{
    public const string Label = "buttonbox-abcd";
    public const string TemplateId = "iphone-14-pro";
    public static readonly IPAddress FakeLan = IPAddress.Parse("192.168.77.10");

    public const string ShippedTemplateJson =
        """{"schemaVersion":1,"id":"iphone-14-pro","name":"iPhone 14 Pro","screen":{"width":852,"height":393,"safeArea":{"top":0,"right":59,"bottom":20,"left":59},"pixelRatio":3},"cutout":"dynamicIsland","generic":false}""";

    public LanFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "bb-lan-" + Guid.NewGuid().ToString("N"));
        KeyName = "bb-test-" + Guid.NewGuid().ToString("N");
        ClientDir = Path.Combine(Root, "client");
        ShippedDir = Path.Combine(Root, "shipped");
        GenericDir = Path.Combine(Root, "generic");
        Directory.CreateDirectory(ClientDir);
        Directory.CreateDirectory(ShippedDir);
        File.WriteAllText(Path.Combine(ClientDir, "index.html"), "CLIENT-MARKER");
        File.WriteAllText(Path.Combine(ShippedDir, "iphone-14-pro.json"), ShippedTemplateJson);
        Ca = CertificateAuthority.LoadOrCreate(Path.Combine(Root, "ca"), KeyName);
        Registry = DeviceRegistry.Load(Path.Combine(Root, "devices.json"), Time);
        Templates = new TemplateStore(ShippedDir, GenericDir);
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public string Root { get; }

    public string KeyName { get; }

    public string ClientDir { get; }

    public string ShippedDir { get; }

    public string GenericDir { get; }

    public CertificateAuthority Ca { get; }

    public DeviceRegistry Registry { get; }

    public TemplateStore Templates { get; }

    public FakeTimeProvider Time { get; } = new();

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public static byte[] MakeLogo()
    {
        using var bitmap = new Bitmap(300, 200);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Orange);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>A trust callback that really validates: the chain must lead to <paramref name="root"/> and the name must match.</summary>
    public static RemoteCertificateValidationCallback Trust(byte[] rootDer) => (_, certificate, _, errors) =>
    {
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0
            || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
        {
            return false;
        }

        using var root = X509CertificateLoader.LoadCertificate(rootDer);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return chain.Build(leaf);
    };

    /// <summary>A handler that dials loopback whatever the URL host is, so the real host name is validated against the certificate.</summary>
    public static SocketsHttpHandler Handler(byte[] trustedRootDer)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        handler.SslOptions.RemoteCertificateValidationCallback = Trust(trustedRootDer);
        return handler;
    }

    public ValueTask DisposeAsync()
    {
        Ca.Dispose();
        CertificateAuthority.DeleteServerKey(KeyName);
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temp folder.
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>A running LAN server with its connection table and pairing session, on free loopback ports.</summary>
internal sealed class LanRig : IAsyncDisposable
{
    private readonly X509Certificate2 _certificate;
    private readonly List<HttpMessageInvoker> _invokers = [];

    private LanRig(LanFixture fixture, X509Certificate2 certificate, DeviceConnections connections, PairingSession pairing, LanServer server, int primary, int fallback)
    {
        Fixture = fixture;
        _certificate = certificate;
        Connections = connections;
        Pairing = pairing;
        Server = server;
        PrimaryPort = primary;
        FallbackPort = fallback;
    }

    public LanFixture Fixture { get; }

    public DeviceConnections Connections { get; }

    public PairingSession Pairing { get; }

    public LanServer Server { get; }

    public int PrimaryPort { get; }

    public int FallbackPort { get; }

    public int HttpsPort => Server.Port;

    public string DefaultOrigin => $"https://{LanFixture.Label}.local:{HttpsPort}";

    public static async Task<LanRig> StartAsync(LanFixture fixture, DeviceConnectionsOptions? connectionOptions = null, byte[]? icon = null, GameSession? session = null, IAssetSource? assets = null)
    {
        var certificate = fixture.Ca.IssueServerCertificate(LanFixture.Label + ".local", [LanFixture.FakeLan]);
        var connections = new DeviceConnections(fixture.Registry, connectionOptions ?? new DeviceConnectionsOptions { Time = fixture.Time }, session);
        var httpsPort = LanFixture.FreePort();
        var primary = LanFixture.FreePort();
        var fallback = LanFixture.FreePort();
        var pairing = new PairingSession(new PairingSessionOptions
        {
            Authority = fixture.Ca,
            Registry = fixture.Registry,
            Templates = fixture.Templates,
            MdnsLabel = LanFixture.Label,
            BindAddresses = () => [IPAddress.Loopback],
            HttpsPort = httpsPort,
            PrimaryPort = primary,
            FallbackPort = fallback,
            Time = fixture.Time,
        });
        var server = await LanServer.StartAsync(new LanServerOptions
        {
            BindAddresses = [IPAddress.Loopback],
            LanAddresses = [LanFixture.FakeLan],
            MdnsLabel = LanFixture.Label,
            Port = httpsPort,
            Certificate = certificate,
            ClientDir = fixture.ClientDir,
            AppleTouchIconPng = AppleTouchIcon.Render(icon ?? LanFixture.MakeLogo()),
            Connections = connections,
            Pairing = pairing,
            Assets = assets,
            Time = fixture.Time,
        });
        return new LanRig(fixture, certificate, connections, pairing, server, primary, fallback);
    }

    public HttpClient CreateClient()
    {
        var client = new HttpClient(LanFixture.Handler(Fixture.Ca.CertificateDer), disposeHandler: true)
        {
            BaseAddress = new Uri($"https://{LanFixture.Label}.local:{HttpsPort}/"),
        };
        return client;
    }

    /// <summary>Opens /ws. <paramref name="origin"/> null sends no Origin header.</summary>
    public async Task<ClientWebSocket> ConnectAsync(bool http2 = false, string? origin = "default")
    {
        var invoker = new HttpMessageInvoker(LanFixture.Handler(Fixture.Ca.CertificateDer), disposeHandler: true);
        _invokers.Add(invoker);
        var socket = new ClientWebSocket();
        try
        {
            if (http2)
            {
                socket.Options.HttpVersion = HttpVersion.Version20;
                socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            }

            var effective = origin == "default" ? DefaultOrigin : origin;
            if (effective is not null)
            {
                socket.Options.SetRequestHeader("Origin", effective);
            }

            await socket.ConnectAsync(new Uri($"wss://{LanFixture.Label}.local:{HttpsPort}/ws"), invoker, LanFixture.Ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Opens /ws, sends hello and returns after the welcome arrived.</summary>
    public async Task<ClientWebSocket> ConnectAuthenticatedAsync(string token, bool http2 = false)
    {
        var socket = await ConnectAsync(http2);
        await SendTextAsync(socket, Hello(token));
        var welcome = await ReadTextAsync(socket);
        Assert.Equal("""{"type":"welcome"}""", welcome);
        return socket;
    }

    public static string Hello(string token) => $$"""{"type":"hello","token":"{{token}}"}""";

    public const string GestureJson = """{"type":"gesture","widgetId":"w1","gesture":"tap"}""";

    public static Task SendTextAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, LanFixture.Ct);

    public static async Task<string> ReadTextAsync(WebSocket socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(LanFixture.Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[1024];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    /// <summary>Reads one whole text message (any size), not just the first 1 KiB.</summary>
    public static async Task<string> ReadFrameAsync(WebSocket socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(LanFixture.Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    /// <summary>Reads every text frame until the server's close frame arrives. Returns the frames and the close code.</summary>
    public static async Task<(List<string> Frames, int CloseCode)> ReadUntilCloseAsync(WebSocket socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(LanFixture.Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[4096];
        var frames = new List<string>();
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                var code = (int)result.CloseStatus!.Value;
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token); // answer, so the server need not wait to cut us off
                }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // Already gone.
                }

                return (frames, code);
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                frames.Add(Encoding.UTF8.GetString(message.ToArray()));
                message.SetLength(0);
            }
        }
    }

    /// <summary>Reads until the server's close frame arrives and returns its code.</summary>
    public static async Task<int> ReadCloseCodeAsync(WebSocket socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(LanFixture.Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (int)result.CloseStatus!.Value;
            }
        }
    }

    public static async Task<T> WaitForAsync<T>(Func<T> read, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var value = read();
            if (done(value))
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not reached in time.");
            }

            await Task.Delay(20, LanFixture.Ct);
        }
    }

    public static StringContent Json(string json, string mediaType = "application/json") =>
        new(json, Encoding.UTF8, new MediaTypeHeaderValue(mediaType));

    public static string PairJson(string code, int width = 852, int height = 393, double pixelRatio = 3, int right = 59) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $$$"""{"code":"{{{code}}}","screen":{"width":{{{width}}},"height":{{{height}}},"safeArea":{"top":0,"right":{{{right}}},"bottom":20,"left":59},"pixelRatio":{{{pixelRatio}}}}}""");

    public async ValueTask DisposeAsync()
    {
        await Pairing.DisposeAsync();
        await Server.DisposeAsync();
        await Connections.DisposeAsync();
        _certificate.Dispose();
        foreach (var invoker in _invokers)
        {
            invoker.Dispose();
        }
    }
}
