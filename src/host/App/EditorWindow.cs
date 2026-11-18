using System.ComponentModel;
using System.Runtime.InteropServices;
using ButtonBox.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ButtonBox.App;

/// <summary>Editor window. Closing hides it; the process lives in the tray.</summary>
internal sealed class EditorWindow : Form
{
    private readonly Uri _baseAddress;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private bool _initStarted;

    public EditorWindow(Uri baseAddress, Icon icon)
    {
        _baseAddress = baseAddress;
        Text = ProductInfo.Name;
        Icon = icon;
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_webView);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }

    public async Task ShowEditorAsync()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        Activate();

        if (_initStarted)
        {
            return;
        }
        _initStarted = true;

        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductInfo.Name, "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _webView.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            _initStarted = false;
            MessageBox.Show(
                "The Microsoft Edge WebView2 Runtime is missing or could not be started, so the editor cannot open.\n\n" + ex.Message,
                ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Hide();
            return;
        }

        var core = _webView.CoreWebView2;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
#endif
        var origin = _baseAddress.GetLeftPart(UriPartial.Authority) + "/";
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(origin, StringComparison.Ordinal))
            {
                e.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.Navigate(new Uri(_baseAddress, "editor/").AbsoluteUri);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
