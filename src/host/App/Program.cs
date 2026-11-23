using System.Reflection;
using ButtonBox.App.Lan;
using ButtonBox.Core;
using ButtonBox.Discovery;

namespace ButtonBox.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // First of all: no file, key or port may be touched by a second process (decisions §12).
        using var instance = SingleInstance.ForProduct();
        if (!instance.TryAcquire())
        {
            instance.SignalRunning();
            return;
        }

        ApplicationConfiguration.Initialize();
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var network = new NetworkWatcher();
        var host = AppHost.Create(new AppHostOptions
        {
            DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductInfo.Name),
            ShippedTemplatesDir = Path.Combine(AppContext.BaseDirectory, "templates"),
            ShippedCatalogsDir = Path.Combine(AppContext.BaseDirectory, "catalog"),
            ClientDir = Path.Combine(root, "client"),
            LogoPng = ReadLogo(),
            Network = network,
        });
        var server = LocalServer.StartAsync(Path.Combine(root, "client"), Path.Combine(root, "editor"), new EditorApiOptions
        {
            Secret = host.Secret,
            Registry = host.Registry,
            Templates = host.Templates,
            Pairing = () => host.Lan?.Pairing,
            IsConnected = id => host.Lan?.Connections.IsConnected(id) ?? false,
            Status = () => host.Status,
            Profiles = host.Profiles,
            Catalogs = host.Catalogs,
            Assets = host.Assets,
            Session = host.Session,
            Telemetry = host.Simulator,
            CaFingerprint = () => host.Lan?.CaFingerprint,
            Firewall = new FirewallStatus(),
            RegenerateCa = () => host.Lan is { } lan ? lan.RegenerateCertificateAuthorityAsync() : Task.FromResult(CaRegenerationResult.Unavailable),
        }).GetAwaiter().GetResult();
        Application.Run(new TrayApplicationContext(instance, host, server, network));
    }

    private static byte[] ReadLogo()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("tray-logo.png")
            ?? throw new InvalidOperationException("Embedded tray logo is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
