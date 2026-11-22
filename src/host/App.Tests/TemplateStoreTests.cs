using ButtonBox.App.Pairing;
using ButtonBox.Core.Contracts;
using Xunit;

namespace ButtonBox.App.Tests;

public sealed class TemplateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bb-tpl-" + Guid.NewGuid().ToString("N"));
    private readonly string _shipped;
    private readonly string _generic;

    public TemplateStoreTests()
    {
        _shipped = Path.Combine(_root, "shipped");
        _generic = Path.Combine(_root, "generic");
        Directory.CreateDirectory(_shipped);
        File.WriteAllText(Path.Combine(_shipped, "iphone-14-pro.json"), LanFixture.ShippedTemplateJson);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ScreenInfo Screen(int width = 800, int height = 400, double top = 0, double right = 50, double bottom = 10, double left = 50, double pixelRatio = 2) =>
        new() { Width = width, Height = height, SafeArea = new SafeArea { Top = top, Right = right, Bottom = bottom, Left = left }, PixelRatio = pixelRatio };

    [Fact]
    public void The_real_shipped_templates_are_valid()
    {
        // The repository's own template files must pass the same validation as any untrusted file.
        var real = Path.Combine(AppContext.BaseDirectory, "templates");
        Assert.True(Directory.Exists(real), "The build must copy templates/*.json next to the executable.");

        var store = new TemplateStore(real, _generic);

        Assert.Empty(store.Problems);
        Assert.True(store.TryGet("iphone-14-pro", out var template));
        Assert.Equal("iPhone 14 Pro", template.Name);
        Assert.False(template.Generic);
    }

    [Fact]
    public void An_invalid_file_is_skipped_and_reported_without_hiding_the_good_ones()
    {
        var good = LanFixture.ShippedTemplateJson;
        File.WriteAllText(Path.Combine(_shipped, "garbage.json"), "{ nope");
        File.WriteAllText(Path.Combine(_shipped, "unknown-field.json"), good.Replace("\"generic\":false", "\"generic\":false,\"x\":1", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(_shipped, "bad-screen.json"), good.Replace("852", "5", StringComparison.Ordinal).Replace("iphone-14-pro", "tiny", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(_shipped, "wrong-flag.json"), good.Replace("\"generic\":false", "\"generic\":true", StringComparison.Ordinal).Replace("iphone-14-pro", "flag", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(_shipped, "int-enum.json"), good.Replace("\"dynamicIsland\"", "2", StringComparison.Ordinal).Replace("iphone-14-pro", "enum", StringComparison.Ordinal));

        var store = new TemplateStore(_shipped, _generic);

        Assert.Equal(["iphone-14-pro"], store.All.Select(t => t.Id));
        Assert.Equal(5, store.Problems.Count);
        Assert.All(store.Problems, p => Assert.DoesNotContain("nope", p, StringComparison.Ordinal));
    }

    [Fact]
    public void A_duplicate_id_keeps_the_shipped_template()
    {
        Directory.CreateDirectory(_generic);
        var imposter = LanFixture.ShippedTemplateJson.Replace("\"generic\":false", "\"generic\":true", StringComparison.Ordinal).Replace("iPhone 14 Pro", "Imposter", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_generic, "iphone-14-pro.json"), imposter);

        var store = new TemplateStore(_shipped, _generic);

        Assert.True(store.TryGet("iphone-14-pro", out var template));
        Assert.Equal("iPhone 14 Pro", template.Name);
        Assert.Single(store.All);
        Assert.Single(store.Problems);
    }

    [Fact]
    public void The_generic_template_for_a_screen_is_stable_and_persisted_atomically()
    {
        var store = new TemplateStore(_shipped, _generic);

        var first = store.GetOrAddGeneric(Screen());
        var again = store.GetOrAddGeneric(Screen());

        Assert.Equal(first.Id, again.Id);
        Assert.Matches("^generic-[0-9a-f]{12}$", first.Id);
        Assert.Equal("Generic 800×400", first.Name);
        Assert.Equal(Cutout.Unknown, first.Cutout);
        Assert.True(first.Generic);
        Assert.Equal(2, store.All.Count);
        Assert.Equal([Path.Combine(_generic, first.Id + ".json")], Directory.GetFiles(_generic)); // no temp file left behind

        var reloaded = new TemplateStore(_shipped, _generic);
        Assert.True(reloaded.TryGet(first.Id, out var loaded));
        Assert.Equal(800, loaded.Screen.Width);
        Assert.Equal(first.Id, reloaded.GetOrAddGeneric(Screen()).Id);
    }

    [Theory]
    [InlineData(801, 400, 0, 50, 10, 50, 2)]
    [InlineData(800, 401, 0, 50, 10, 50, 2)]
    [InlineData(800, 400, 1, 50, 10, 50, 2)]
    [InlineData(800, 400, 0, 51, 10, 50, 2)]
    [InlineData(800, 400, 0, 50, 11, 50, 2)]
    [InlineData(800, 400, 0, 50, 10, 51, 2)]
    [InlineData(800, 400, 0, 50, 10, 50, 3)]
    [InlineData(800, 400, 0, 50, 10, 50, 2.5)]
    public void Any_single_field_difference_gives_another_generic_id(int w, int h, double top, double right, double bottom, double left, double pixelRatio)
    {
        // The id covers every field, so an inset-only difference is its own template instead of silently reusing a wrong one.
        var store = new TemplateStore(_shipped, _generic);

        var baseline = store.GetOrAddGeneric(Screen());
        var other = store.GetOrAddGeneric(Screen(w, h, top, right, bottom, left, pixelRatio));

        Assert.NotEqual(baseline.Id, other.Id);
    }

    [Fact]
    public void An_invalid_screen_is_refused_and_nothing_is_written()
    {
        var store = new TemplateStore(_shipped, _generic);

        Assert.Throws<ArgumentException>(() => store.GetOrAddGeneric(Screen(width: 5)));

        Assert.False(Directory.Exists(_generic) && Directory.GetFiles(_generic).Length > 0);
    }

    [Fact]
    public void Mismatch_compares_size_and_pixel_ratio_and_ignores_the_insets()
    {
        var store = new TemplateStore(_shipped, _generic);
        Assert.True(store.TryGet("iphone-14-pro", out var template));

        Assert.False(TemplateStore.Mismatches(template, Screen(852, 393, 7, 1, 2, 3, 3)));
        Assert.True(TemplateStore.Mismatches(template, Screen(851, 393, 0, 59, 20, 59, 3)));
        Assert.True(TemplateStore.Mismatches(template, Screen(852, 392, 0, 59, 20, 59, 3)));
        Assert.True(TemplateStore.Mismatches(template, Screen(852, 393, 0, 59, 20, 59, 2)));
    }
}
