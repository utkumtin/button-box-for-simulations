using ButtonBox.App.Profiles;

namespace ButtonBox.App;

/// <summary>
/// The tray's "Profiles" submenu (decisions §11): one item per healthy profile, a check mark on the active one, rebuilt when the menu
/// opens. A click activates through the store, the same path (and publish) as the editor. UI thread only.
/// </summary>
public sealed class ProfileMenu
{
    public const string FailureText = "The profile could not be switched. Open the editor to see why.";

    private readonly ProfileStore _store;
    private readonly Action<string> _notify;

    /// <param name="notify">Shows a fixed-text balloon.</param>
    public ProfileMenu(ProfileStore store, Action<string> notify)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(notify);
        _store = store;
        _notify = notify;
        Item = new ToolStripMenuItem("Profiles");
        Item.DropDownOpening += (_, _) => Refresh();
        Refresh();
    }

    public ToolStripMenuItem Item { get; }

    /// <summary>Re-reads the list. Damaged profiles are not offered (they cannot be activated).</summary>
    public void Refresh()
    {
        var items = Item.DropDownItems;
        while (items.Count > 0)
        {
            var old = items[0];
            items.RemoveAt(0);
            old.Dispose();
        }

        var list = _store.List();
        foreach (var profile in list.Profiles.Where(p => !p.Damaged))
        {
            var id = profile.Id;
            // '&' is the mnemonic marker in a menu text.
            var item = new ToolStripMenuItem((profile.Name ?? id).Replace("&", "&&", StringComparison.Ordinal))
            {
                Checked = string.Equals(id, list.ActiveId, StringComparison.Ordinal),
            };
            item.Click += (_, _) => Activate(id);
            items.Add(item);
        }

        if (items.Count == 0)
        {
            items.Add(new ToolStripMenuItem("No profiles") { Enabled = false });
        }
    }

    /// <summary>Switches to the profile; a refusal or failure shows <see cref="FailureText"/>.</summary>
    public void Activate(string id)
    {
        try
        {
            if (!_store.Activate(id).Ok)
            {
                _notify(FailureText);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _notify(FailureText);
        }
    }
}
