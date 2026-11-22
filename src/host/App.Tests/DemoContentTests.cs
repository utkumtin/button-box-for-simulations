using ButtonBox.App.Lan;
using ButtonBox.Core.Contracts;
using Xunit;

namespace ButtonBox.App.Tests;

public sealed class DemoContentTests
{
    [Fact]
    public void The_embedded_demo_profile_and_catalog_load_and_pass_the_profile_check()
    {
        var demo = DemoContent.Load();

        Assert.Equal(Game.Ets2, demo.Profile.Game);
        var layout = demo.Engine.ForTemplate("iphone-14-pro");
        Assert.NotNull(layout);
        Assert.NotEmpty(layout.Layout.Widgets);
        Assert.Null(demo.Engine.ForTemplate("a-template-the-demo-does-not-cover"));
    }

    [Fact]
    public void Every_image_the_demo_profile_references_is_in_the_asset_store_under_its_computed_hash()
    {
        // The profile names images by hash; a hash that the host cannot serve is a broken button on every phone.
        var demo = DemoContent.Load();
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layout in demo.Profile.Layouts)
        {
            foreach (var widget in layout.Widgets)
            {
                var look = widget switch
                {
                    ToggleWidget toggle => toggle.Look,
                    DualActionWidget dual => dual.Look,
                    _ => null,
                };
                if (look is ImagesLook images)
                {
                    referenced.Add(images.Image);
                    if (images.On is { } on)
                    {
                        referenced.Add(on);
                    }
                }

                if (widget is SwitchWidget { Rotary: { } rotary })
                {
                    foreach (var layer in new[] { rotary.Base, rotary.Knob, rotary.Highlight })
                    {
                        if (layer is not null)
                        {
                            referenced.Add(layer.Asset);
                        }
                    }
                }
            }
        }

        Assert.NotEmpty(referenced);
        foreach (var id in referenced)
        {
            Assert.True(demo.Assets.TryGet(id, out var bytes), $"Asset {id} is referenced by the demo profile but not served.");
            Assert.NotEmpty(bytes);
        }
    }

    [Fact]
    public void Asset_ids_are_computed_from_the_bytes_not_taken_from_anywhere()
    {
        var store = new AssetStore([[1, 2, 3]]);

        var id = Assert.Single(store.Ids);
        Assert.Equal("039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", id);
        Assert.True(store.TryGet(id, out var bytes));
        Assert.Equal([1, 2, 3], bytes);
    }
}
