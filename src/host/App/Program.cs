namespace ButtonBox.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var server = LocalServer.StartAsync(Path.Combine(root, "client"), Path.Combine(root, "editor"))
            .GetAwaiter().GetResult();
        Application.Run(new TrayApplicationContext(server));
    }
}
