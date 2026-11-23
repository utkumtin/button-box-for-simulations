using System.Text.Json.Nodes;
using ButtonBox.App.Profiles;
using ButtonBox.Core.Contracts;
using Xunit;

namespace ButtonBox.App.Tests;

// Why: a profile is only as good as the catalog it is checked against. A catalog file that is damaged, or that sits under the
// wrong game name, must take that game out of the editor instead of silently serving the wrong actions.
public sealed class CatalogStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bb-cat-" + Guid.NewGuid().ToString("N"));

    public CatalogStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Shipped(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "catalog", name + ".json"));

    private string Put(string name, string json)
    {
        File.WriteAllText(Path.Combine(_root, name + ".json"), json);
        return _root;
    }

    private static string Edit(string json, Action<JsonObject> change)
    {
        var node = (JsonObject)JsonNode.Parse(json)!;
        change(node);
        return node.ToJsonString();
    }

    [Fact]
    public void Both_shipped_catalogs_load_without_problems()
    {
        var store = new CatalogStore(Path.Combine(AppContext.BaseDirectory, "catalog"));

        Assert.Empty(store.Problems);
        Assert.Equal([Game.Ets2, Game.Ats], store.All.Select(c => c.Game));
        Assert.True(store.TryGet(Game.Ets2, out var ets2));
        Assert.Equal(Game.Ets2, ets2.Game);
        Assert.True(store.TryGet(Game.Ats, out _));
    }

    [Fact]
    public void A_game_without_a_catalog_cannot_be_chosen()
    {
        var store = new CatalogStore(Path.Combine(AppContext.BaseDirectory, "catalog"));

        Assert.False(store.TryGet(Game.Ac1, out _));
        Assert.False(store.TryGet(Game.Acc, out _));
    }

    [Fact]
    public void A_file_whose_game_differs_from_its_name_is_refused_and_only_that_game_is_lost()
    {
        // ats.json that holds the ETS2 catalog would give ATS profiles the wrong actions without anyone noticing.
        Put("ats", Shipped("ets2"));
        var dir = Put("ets2", Shipped("ets2"));

        var store = new CatalogStore(dir);

        Assert.False(store.TryGet(Game.Ats, out _));
        Assert.True(store.TryGet(Game.Ets2, out _));
        Assert.Contains("ats.json", Assert.Single(store.Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":1,"game":"ets2","actions":null}""")]
    [InlineData("""{"schemaVersion":2,"game":"ets2","actions":[]}""")]
    [InlineData("""{"schemaVersion":1,"game":"ets2","actions":[],"extra":1}""")]
    public void A_damaged_file_makes_its_game_unavailable_without_an_exception(string content)
    {
        var dir = Put("ets2", content);

        var store = new CatalogStore(dir);

        Assert.False(store.TryGet(Game.Ets2, out _));
        Assert.Single(store.Problems);
    }

    [Fact]
    public void A_catalog_with_a_repeated_action_id_is_refused()
    {
        // Two actions with one id would make a profile's reference ambiguous.
        var json = Edit(Shipped("ets2"), root =>
        {
            var actions = root["actions"]!.AsArray();
            actions.Add(actions[0]!.DeepClone());
        });

        var store = new CatalogStore(Put("ets2", json));

        Assert.False(store.TryGet(Game.Ets2, out _));
    }

    [Fact]
    public void An_scs_game_catalog_with_a_vjoy_output_is_refused()
    {
        var json = Edit(Shipped("ets2"), root =>
            root["actions"]!.AsArray()[0]!["output"] = new JsonObject { ["type"] = "vjoy", ["button"] = 1 });

        var store = new CatalogStore(Put("ets2", json));

        Assert.False(store.TryGet(Game.Ets2, out _));
    }

    [Fact]
    public void A_file_over_the_size_limit_is_refused_unread()
    {
        var dir = Put("ets2", new string(' ', 300 * 1024) + Shipped("ets2"));

        var store = new CatalogStore(dir);

        Assert.False(store.TryGet(Game.Ets2, out _));
        Assert.Contains("large", Assert.Single(store.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_folder_means_no_catalogs_and_no_exception()
    {
        var store = new CatalogStore(Path.Combine(_root, "nowhere"));

        Assert.Empty(store.All);
        Assert.False(store.TryGet(Game.Ets2, out _));
    }

    [Fact]
    public void Wire_names_are_the_schema_names()
    {
        Assert.Equal("ets2", CatalogStore.WireName(Game.Ets2));
        Assert.Equal("ats", CatalogStore.WireName(Game.Ats));
        Assert.Equal("acevo", CatalogStore.WireName(Game.Acevo));
    }
}
