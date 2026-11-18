using ButtonBox.Core;

namespace ButtonBox.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly LocalServer _server;
    private readonly Icon _icon = TrayIcon.Create();
    private readonly NotifyIcon _notifyIcon;
    private readonly EditorWindow _editor;

    public TrayApplicationContext(LocalServer server)
    {
        _server = server;
        _editor = new EditorWindow(server.BaseAddress, _icon);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open editor", null, async (_, _) => await _editor.ShowEditorAsync());
        menu.Items.Add("Quit", null, async (_, _) => await QuitAsync());

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = ProductInfo.Name,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += async (_, _) => await _editor.ShowEditorAsync();
    }

    private async Task QuitAsync()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _editor.AllowClose = true;
        _editor.Close();
        await _server.DisposeAsync();
        _icon.Dispose();
        ExitThread();
    }
}
