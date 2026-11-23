using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Discovery;

namespace ButtonBox.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppHost _host;
    private readonly LocalServer _server;
    private readonly NetworkWatcher _network;
    private readonly Icon _icon = TrayIcon.Create();
    private readonly NotifyIcon _notifyIcon;
    private readonly EditorWindow _editor;
    private readonly Control _ui = new();
    private (LanServerState State, string? Owner)? _notified;
    private bool _quitting;

    public TrayApplicationContext(SingleInstance instance, AppHost host, LocalServer server, NetworkWatcher network)
    {
        _host = host;
        _server = server;
        _network = network;
        _ = _ui.Handle; // forces the handle on this (UI) thread so other threads can marshal to it
        _editor = new EditorWindow(server.BaseAddress, _icon, host.Secret);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open editor", null, (_, _) => OpenEditor());
        menu.Items.Add(new ProfileMenu(host.Profiles, text => _notifyIcon!.ShowBalloonTip(10_000, ProductInfo.Name, text, ToolTipIcon.Warning)).Item);
        menu.Items.Add(new SimulatorMenu(host.Simulator).Item);
        menu.Items.Add("Quit", null, async (_, _) => await QuitAsync());

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = ProductInfo.Name,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenEditor();

        host.StatusChanged += () => Post(CheckStatus);
        instance.StartListening(() => Post(OpenEditor));
        CheckStatus();
        _ = host.StartAsync(); // never throws: a failure becomes the status startFailed
    }

    private void Post(Action action)
    {
        try
        {
            if (!_quitting && _ui.IsHandleCreated && !_ui.IsDisposed)
            {
                _ui.BeginInvoke(action);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private async void OpenEditor()
    {
        try
        {
            await _editor.ShowEditorAsync();
        }
        catch (Exception)
        {
            _notifyIcon.ShowBalloonTip(10_000, ProductInfo.Name, "The editor could not be opened.", ToolTipIcon.Warning);
        }
    }

    /// <summary>One balloon per change of a problem state. The retry timer repeats the same state every 30 seconds without a new balloon.</summary>
    private void CheckStatus()
    {
        if (_quitting)
        {
            return;
        }

        var status = _host.Status;
        var message = StatusMessages.For(status);
        if (message is null)
        {
            _notified = null; // a later relapse is a new problem and is announced again
            return;
        }

        var key = (status.LanServer, status.PortOwner);
        if (_notified == key)
        {
            return;
        }

        _notified = key;
        _notifyIcon.ShowBalloonTip(15_000, ProductInfo.Name, message, ToolTipIcon.Warning);
    }

    private async Task QuitAsync()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _editor.AllowClose = true;
            _editor.Close();
            // The single-instance mutex is released last, by Main: the server key must not be touched by a new process before the LAN side stopped.
            await Quietly(_host.DisposeAsync().AsTask());
            await Quietly(_server.DisposeAsync().AsTask());
            _network.Dispose();
        }
        finally
        {
            _ui.Dispose();
            _icon.Dispose();
            ExitThread();
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // Shutting down; nothing useful can be done with a failure here.
        }
    }
}
