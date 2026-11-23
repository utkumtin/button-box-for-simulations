using System.Windows.Forms;
using ButtonBox.App.Profiles;
using ButtonBox.Core.Contracts;
using Xunit;

namespace ButtonBox.App.Tests;

// Why: the tray is how a driver switches profile without alt-tabbing out of the game; it must go through the store (so the phones
// follow) and must never offer a profile that cannot run.
public sealed class ProfileMenuTests
{

    // Creating a menu item installs a WinForms synchronization context on the calling thread; no message loop runs it in a test,
    // so every later await on that thread would wait forever.
    static ProfileMenuTests() => WindowsFormsSynchronizationContext.AutoInstall = false;

    private static ProfileMenu NewMenu(ProfileStore store, Action<string> notify)
    {
        var menu = new ProfileMenu(store, notify);
        SynchronizationContext.SetSynchronizationContext(null); // belt and braces: nothing may post to a context nobody pumps
        return menu;
    }

    private static string Duplicate(Api4Rig rig, string name)
    {
        var copy = rig.Store.Duplicate(rig.ActiveId).Value!;
        copy.Name = name;
        Assert.True(rig.Store.Save(copy.Id, copy).Ok);
        return copy.Id;
    }

    [Fact]
    public async Task The_menu_lists_healthy_profiles_with_a_check_on_the_active_one_and_hides_damaged_ones()
    {
        await using var rig = await Api4Rig.StartAsync();
        Duplicate(rig, "Night & day");
        File.WriteAllText(Path.Combine(rig.DataDir, "profiles", "broken.json"), "not json");
        var menu = NewMenu(rig.Store, _ => { });

        menu.Refresh();

        var items = menu.Item.DropDownItems.OfType<ToolStripMenuItem>().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(["Night && day"], items.Where(i => !i.Checked).Select(i => i.Text)); // '&' is escaped for the menu
        Assert.Single(items, i => i.Checked);
        Assert.DoesNotContain(items, i => i.Text == "broken");
    }

    [Fact]
    public async Task Clicking_a_profile_activates_it_and_the_phone_gets_its_layout()
    {
        await using var rig = await Api4Rig.StartAsync(phone: true);
        var phone = await rig.ConnectPhoneAsync();
        await phone.WaitLayoutsAsync(1);
        var menu = NewMenu(rig.Store, text => Assert.Fail("no balloon expected: " + text));
        var third = Duplicate(rig, "Third");
        var relabelled = rig.Store.Get(third).Value!;
        relabelled.Layouts[0].Widgets.Single(w => w.Id == "lights").Label = "From tray";
        Assert.True(rig.Store.Save(third, relabelled).Ok);
        menu.Refresh();

        menu.Item.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Text == "Third").PerformClick();

        Assert.Equal(third, rig.Store.ActiveId);
        await phone.WaitLayoutsAsync(2, "From tray");
        menu.Refresh();
        Assert.Equal(["Third"], menu.Item.DropDownItems.OfType<ToolStripMenuItem>().Where(i => i.Checked).Select(i => i.Text));
    }

    [Fact]
    public async Task A_profile_that_cannot_be_activated_shows_the_fixed_balloon_and_changes_nothing()
    {
        await using var rig = await Api4Rig.StartAsync();
        var shown = new List<string>();
        var menu = NewMenu(rig.Store, shown.Add);
        var active = rig.Store.ActiveId;

        menu.Activate("nobody");

        Assert.Equal([ProfileMenu.FailureText], shown);
        Assert.Equal(active, rig.Store.ActiveId);
    }

    [Fact]
    public async Task Without_any_profile_the_menu_says_so()
    {
        await using var rig = await Api4Rig.StartAsync();
        foreach (var file in Directory.GetFiles(Path.Combine(rig.DataDir, "profiles")))
        {
            File.Delete(file);
        }

        var menu = NewMenu(rig.Store, _ => { });
        menu.Refresh();

        var only = Assert.Single(menu.Item.DropDownItems.OfType<ToolStripMenuItem>());
        Assert.Equal("No profiles", only.Text);
        Assert.False(only.Enabled);
    }
}
