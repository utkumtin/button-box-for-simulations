using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ButtonBox.App.Profiles;
using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;
using Xunit;

namespace ButtonBox.App.Tests;

// Why: profiles are the user's work. The store must never lose or rewrite one it cannot trust, never publish to the phones what
// is not on disk, and never let a bad file take the app down (decisions §3 "Publishing", §6).
public sealed partial class ProfileStoreTests : IDisposable
{
    private static readonly TelemetrySnapshot Listening = new(true, true, new Dictionary<string, int>());

    private readonly ProfileKit _kit = new();
    private readonly ProfileStore _store;

    public ProfileStoreTests()
    {
        _store = _kit.NewStore();
    }

    public void Dispose() => _kit.Dispose();

    [GeneratedRegex(@"\Aets2-night-[0-9a-f]{4}\z")]
    private static partial Regex NightId();

    private Profile Imported(ProfileStore? store = null, Action<System.Text.Json.Nodes.JsonObject>? edit = null)
    {
        var json = edit is null ? ProfileKit.Bytes(ProfileKit.Demo()) : ProfileKit.Mutate(edit);
        var result = (store ?? _store).Import(json);
        Assert.True(result.Ok, string.Join(" ", result.Problems));
        return result.Value!;
    }

    private static string Fingerprint(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static int? LightsPosition(ProfileEngine engine) =>
        engine.ForTemplate(ProfileKit.Template)!.Project(Listening).Widgets["lights"].Position;

    private static void SelectLights(ProfileEngine engine, int position) =>
        Assert.Equal(HandleOutcome.Accepted, engine.ForTemplate(ProfileKit.Template)!.Handle(new SelectMessage { WidgetId = "lights", Position = position }, Listening).Outcome);

    private string WriteRaw(string id, byte[] bytes)
    {
        Directory.CreateDirectory(_kit.ProfilesDir);
        var path = _kit.ProfileFile(id);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ---- create and ids ----

    [Fact]
    public void Create_writes_one_file_named_by_a_slug_of_the_name_plus_four_hex_digits()
    {
        var result = _store.Create("ETS2 Night", Game.Ets2);

        Assert.True(result.Ok);
        var profile = result.Value!;
        Assert.Matches(NightId(), profile.Id);
        Assert.Equal(profile.Id + ".json", Path.GetFileName(Assert.Single(Directory.GetFiles(_kit.ProfilesDir))));
        var stored = ProfileKit.Parse(File.ReadAllBytes(_kit.ProfileFile(profile.Id)));
        Assert.Equal("ETS2 Night", stored.Name);
        Assert.Equal(Game.Ets2, stored.Game);
        Assert.Empty(stored.Layouts);
        Assert.False(Assert.Single(_store.List().Profiles).Damaged);
    }

    [Theory]
    [InlineData("Hello,   World!!", "hello-world")]
    [InlineData("  --A--  ", "a")]
    [InlineData("***", "profile")]
    [InlineData("ÄÖ", "profile")]
    [InlineData("ETS2 Night", "ets2-night")]
    public void A_slug_is_lowercase_letters_digits_and_single_dashes_and_never_empty(string name, string slug)
    {
        Assert.Equal(slug, ProfileStore.Slug(name));
    }

    [Fact]
    public void A_slug_is_cut_to_40_characters_and_never_ends_in_a_dash()
    {
        // An id must start and end cleanly: "…-" followed by the hex suffix would read as a double dash.
        var name = new string('a', 39) + "-b" + new string('c', 30);

        var slug = ProfileStore.Slug(name);

        Assert.Equal(new string('a', 39), slug);
        Assert.Equal(40, ProfileStore.Slug(new string('x', 100)).Length);
    }

    [Fact]
    public void Create_refuses_a_game_without_a_catalog_and_a_bad_name_and_writes_nothing()
    {
        var noCatalog = _store.Create("Assetto", Game.Ac1);
        var badName = _store.Create("Line\nbreak", Game.Ets2);

        Assert.Equal(StoreStatus.Invalid, noCatalog.Status);
        Assert.Contains(noCatalog.Problems, p => p.Contains("catalog", StringComparison.Ordinal));
        Assert.Equal(StoreStatus.Invalid, badName.Status);
        Assert.False(Directory.Exists(_kit.ProfilesDir) && Directory.GetFiles(_kit.ProfilesDir).Length > 0);
    }

    // ---- save ----

    [Fact]
    public void Save_persists_the_profile_under_the_same_file_and_id_even_when_the_name_changes()
    {
        // Renaming a profile never renames its file (decisions §6): the id is the identity.
        var profile = Imported();
        var before = Directory.GetFiles(_kit.ProfilesDir).Single();
        profile.Name = "Renamed";

        var saved = _store.Save(profile.Id, profile);

        Assert.True(saved.Ok);
        Assert.Equal([before], Directory.GetFiles(_kit.ProfilesDir));
        var reopened = _kit.NewStore().Get(profile.Id).Value!;
        Assert.Equal("Renamed", reopened.Name);
        Assert.Equal(profile.Id, reopened.Id);
    }

    [Fact]
    public void Save_refuses_a_different_id_a_changed_game_and_a_profile_the_gate_refuses_and_leaves_the_file_alone()
    {
        var profile = Imported();
        var path = _kit.ProfileFile(profile.Id);
        var before = Fingerprint(path);

        var otherId = ProfileKit.Parse(ProfileKit.Bytes(profile));
        otherId.Id = "someone-else";
        var otherGame = ProfileKit.Parse(ProfileKit.Bytes(profile));
        otherGame.Game = Game.Ats;
        var overlapping = ProfileKit.Parse(ProfileKit.Mutate(root =>
        {
            var copy = ProfileKit.Widget(root, "beacon").DeepClone().AsObject();
            copy["id"] = "beacon-twin";
            ProfileKit.Layout(root)["widgets"]!.AsArray().Add(copy);
        }));
        overlapping.Id = profile.Id;

        Assert.Equal(StoreStatus.Invalid, _store.Save(profile.Id, otherId).Status);
        Assert.Equal(StoreStatus.Invalid, _store.Save(profile.Id, otherGame).Status);
        Assert.Equal(StoreStatus.Invalid, _store.Save(profile.Id, overlapping).Status);
        Assert.Equal(before, Fingerprint(path));
    }

    [Fact]
    public void Save_of_an_unknown_or_malformed_id_is_not_found_and_never_touches_a_path_outside_the_folder()
    {
        var profile = ProfileKit.Demo();

        Assert.Equal(StoreStatus.NotFound, _store.Save("demo", profile).Status);
        Assert.Equal(StoreStatus.NotFound, _store.Save("../escape", profile).Status);
        Assert.Equal(StoreStatus.NotFound, _store.Get("..\\escape").Status);
        Assert.Equal(StoreStatus.NotFound, _store.Delete("a/b").Status);
        Assert.Equal(StoreStatus.NotFound, _store.Activate("").Status);
    }

    // ---- publishing ----

    [Fact]
    public void Saving_the_active_profile_publishes_a_new_engine_and_saving_another_one_does_not()
    {
        var active = Imported();
        var other = Imported();
        Assert.True(_store.Activate(active.Id).Ok);
        var engineBefore = _kit.Session.Engine;
        Assert.NotNull(engineBefore);

        other.Name = "Not active";
        Assert.True(_store.Save(other.Id, other).Ok);
        Assert.Same(engineBefore, _kit.Session.Engine);

        var edited = ProfileKit.Parse(ProfileKit.Bytes(active));
        edited.Layouts[0].Widgets.Single(w => w.Id == "beacon").Label = "CHANGED";
        Assert.True(_store.Save(active.Id, edited).Ok);

        Assert.NotSame(engineBefore, _kit.Session.Engine);
        var beacon = _kit.Session.Engine!.ForTemplate(ProfileKit.Template)!.Layout.Widgets.Single(w => w.Id == "beacon");
        Assert.Equal("CHANGED", beacon.Label);
    }

    [Fact]
    public void A_save_keeps_the_switch_positions_and_switching_profile_resets_them()
    {
        // decisions §3 "Publishing": the driver's rotary stays where it is while the layout is edited; a profile switch resets.
        var profile = Imported();
        Assert.True(_store.Activate(profile.Id).Ok);
        SelectLights(_kit.Session.Engine!, 2);
        var edited = ProfileKit.Parse(ProfileKit.Bytes(profile));
        edited.Layouts[0].Widgets.Single(w => w.Id == "beacon").Label = "CHANGED";

        Assert.True(_store.Save(profile.Id, edited).Ok);
        Assert.Equal(2, LightsPosition(_kit.Session.Engine!));

        var other = Imported();
        Assert.True(_store.Activate(other.Id).Ok); // another profile: a profile switch
        Assert.Equal(0, LightsPosition(_kit.Session.Engine!));
    }

    [Fact]
    public void A_failed_write_changes_nothing_on_disk_and_nothing_on_the_phones()
    {
        // decisions §3 "Publishing": validate, write, then swap. A phone must never show a layout that is not on disk.
        var failing = false;
        var store = _kit.NewStore((path, bytes) =>
        {
            if (failing)
            {
                throw new IOException("disk full");
            }

            AtomicFile.WriteAllBytes(path, bytes);
        });
        var profile = Imported(store);
        Assert.True(store.Activate(profile.Id).Ok);
        var engineBefore = _kit.Session.Engine;
        var path = _kit.ProfileFile(profile.Id);
        var before = Fingerprint(path);
        var edited = ProfileKit.Parse(ProfileKit.Bytes(profile));
        edited.Layouts[0].Widgets.Single(w => w.Id == "beacon").Label = "CHANGED";
        failing = true;

        var save = store.Save(profile.Id, edited);

        Assert.Equal(StoreStatus.WriteFailed, save.Status);
        Assert.Same(engineBefore, _kit.Session.Engine);
        Assert.Equal(before, Fingerprint(path));
        Assert.Equal(StoreStatus.WriteFailed, store.Create("New", Game.Ets2).Status);
        Assert.Single(Directory.GetFiles(_kit.ProfilesDir));
    }

    [Fact]
    public void A_failed_write_of_the_active_file_keeps_the_old_active_profile_and_the_phones_on_it()
    {
        var failing = false;
        var store = _kit.NewStore((path, bytes) =>
        {
            if (failing)
            {
                throw new IOException("disk full");
            }

            AtomicFile.WriteAllBytes(path, bytes);
        });
        var first = Imported(store);
        var second = Imported(store);
        Assert.True(store.Activate(first.Id).Ok);
        var engineBefore = _kit.Session.Engine;
        failing = true;

        var result = store.Activate(second.Id);

        Assert.Equal(StoreStatus.WriteFailed, result.Status);
        Assert.Equal(first.Id, store.ActiveId);
        Assert.Same(engineBefore, _kit.Session.Engine);
    }

    // ---- activation and the active file ----

    [Fact]
    public void Activate_writes_the_active_file_publishes_the_engine_and_survives_a_restart()
    {
        var profile = Imported();
        Assert.Null(_kit.Session.Engine);

        Assert.True(_store.Activate(profile.Id).Ok);

        Assert.Equal(profile.Id, _store.ActiveId);
        Assert.Equal(profile.Id, _store.List().ActiveId);
        Assert.NotNull(_kit.Session.Engine);
        Assert.Equal($$"""{"schemaVersion":1,"profileId":"{{profile.Id}}"}""", File.ReadAllText(_kit.ActiveFile));
        var restarted = _kit.NewStore();
        Assert.Equal(profile.Id, restarted.ActiveId);
        Assert.NotNull(restarted.CreateActiveEngine());
    }

    [Fact]
    public void Activate_of_an_unknown_profile_is_not_found_and_changes_nothing()
    {
        Assert.Equal(StoreStatus.NotFound, _store.Activate("nothing-0000").Status);
        Assert.Null(_store.ActiveId);
        Assert.False(File.Exists(_kit.ActiveFile));
    }

    [Fact]
    public void A_missing_active_file_means_no_active_profile_and_no_problem()
    {
        Imported();

        Assert.Null(_store.ActiveId);
        Assert.Null(_store.CreateActiveEngine());
        Assert.Empty(_store.Problems);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("""{"schemaVersion":1}""")]
    [InlineData("""{"schemaVersion":2,"profileId":"demo-1234"}""")]
    [InlineData("""{"schemaVersion":1,"profileId":"../x"}""")]
    [InlineData("""{"schemaVersion":1,"profileId":"demo-1234","extra":1}""")]
    public void A_damaged_active_file_means_no_active_profile_is_recorded_and_is_overwritten_by_the_next_activation(string content)
    {
        // It holds nothing precious (one id), so the next activation replaces it; until then phones get noLayout.
        var profile = Imported();
        File.WriteAllText(_kit.ActiveFile, content);

        var restarted = _kit.NewStore();

        Assert.Null(restarted.ActiveId);
        Assert.Null(restarted.CreateActiveEngine());
        Assert.Single(restarted.Problems);
        Assert.True(restarted.Activate(profile.Id).Ok);
        Assert.Equal(profile.Id, _kit.NewStore().ActiveId);
    }

    [Fact]
    public void An_active_id_without_a_file_is_no_active_profile()
    {
        File.WriteAllText(_kit.ActiveFile, """{"schemaVersion":1,"profileId":"gone-0000"}""");

        var store = _kit.NewStore();

        Assert.Null(store.ActiveId);
        Assert.Null(store.CreateActiveEngine());
    }

    // ---- damaged files ----

    private IReadOnlyList<string> DamagedFiles()
    {
        var badAction = ProfileKit.Parse(ProfileKit.Mutate(root => ProfileKit.Widget(root, "beacon")["action"] = "no-such-action"));
        var other = ProfileKit.Demo(); // its id "demo" differs from the file name
        return
        [
            WriteRaw("garbage-0001", "{ this is not json"u8.ToArray()),
            WriteRaw("mismatch-0002", ProfileKit.Bytes(other)),
            WriteRaw("noaction-0003", ProfileKit.Bytes(WithId(badAction, "noaction-0003"))),
            WriteRaw("huge-0004", new byte[ProfileGate.MaxBytes + 1]),
            WriteRaw("empty-0005", []),
        ];
    }

    private static Profile WithId(Profile profile, string id)
    {
        profile.Id = id;
        return profile;
    }

    [Fact]
    public void A_damaged_file_is_listed_as_damaged_and_no_operation_but_delete_ever_rewrites_it()
    {
        var healthy = Imported();
        var paths = DamagedFiles();
        var before = paths.ToDictionary(p => p, Fingerprint);

        var list = _store.List();
        foreach (var path in paths)
        {
            var id = ProfileKit.IdOf(path);
            Assert.Equal(StoreStatus.Conflict, _store.Get(id).Status);
            Assert.Equal(StoreStatus.Conflict, _store.Save(id, ProfileKit.Parse(ProfileKit.Bytes(WithId(ProfileKit.Demo(), id)))).Status);
            Assert.Equal(StoreStatus.Conflict, _store.Activate(id).Status);
            Assert.Equal(StoreStatus.Conflict, _store.Duplicate(id).Status);
            Assert.False(string.IsNullOrWhiteSpace(Assert.Single(_store.Get(id).Problems.Take(1))));
        }

        // The healthy one comes first, then the damaged ones by id, without name and game.
        Assert.Equal(healthy.Id, list.Profiles[0].Id);
        Assert.All(list.Profiles.Skip(1), s =>
        {
            Assert.True(s.Damaged);
            Assert.Null(s.Name);
            Assert.Null(s.Game);
        });
        Assert.Equal(paths.Count, list.Profiles.Count(s => s.Damaged));
        Assert.Equal(before, paths.ToDictionary(p => p, Fingerprint));
        Assert.Null(_store.ActiveId);
    }

    [Fact]
    public void A_damaged_file_can_be_deleted_because_the_user_asked()
    {
        var path = DamagedFiles()[0];

        var result = _store.Delete(ProfileKit.IdOf(path));

        Assert.True(result.Ok);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void When_the_active_profile_is_damaged_the_active_profile_is_none_the_active_file_is_untouched_and_the_phones_get_noLayout()
    {
        var path = DamagedFiles()[0];
        var id = ProfileKit.IdOf(path);
        var activeJson = $$"""{"schemaVersion":1,"profileId":"{{id}}"}""";
        File.WriteAllText(_kit.ActiveFile, activeJson);

        var restarted = _kit.NewStore();

        Assert.Null(restarted.ActiveId);
        Assert.Null(restarted.List().ActiveId);
        Assert.Null(restarted.CreateActiveEngine());
        Assert.Equal(activeJson, File.ReadAllText(_kit.ActiveFile));
    }

    [Fact]
    public void Deleting_the_damaged_active_profile_tells_the_phones_there_is_no_layout()
    {
        var profile = Imported();
        Assert.True(_store.Activate(profile.Id).Ok);
        Assert.NotNull(_kit.Session.Engine);
        File.WriteAllBytes(_kit.ProfileFile(profile.Id), "{"u8.ToArray()); // damaged behind the app's back

        var result = _store.Delete(profile.Id);

        Assert.True(result.Ok);
        Assert.Null(_kit.Session.Engine);
        Assert.Null(_store.ActiveId);
    }

    // ---- delete ----

    [Fact]
    public void The_active_profile_cannot_be_deleted_but_any_other_can()
    {
        var active = Imported();
        var other = Imported();
        Assert.True(_store.Activate(active.Id).Ok);

        var refused = _store.Delete(active.Id);
        var deleted = _store.Delete(other.Id);

        Assert.Equal(StoreStatus.Conflict, refused.Status);
        Assert.True(File.Exists(_kit.ProfileFile(active.Id)));
        Assert.True(deleted.Ok);
        Assert.False(File.Exists(_kit.ProfileFile(other.Id)));
        Assert.Equal(StoreStatus.NotFound, _store.Delete(other.Id).Status);
        Assert.NotNull(_kit.Session.Engine);
    }

    // ---- import and duplicate ----

    [Fact]
    public void An_import_always_gets_a_new_id_and_never_overwrites_a_profile()
    {
        var original = Imported();
        var path = _kit.ProfileFile(original.Id);
        var before = Fingerprint(path);
        // The exported file of an existing profile: it carries the id that is already taken.
        var export = File.ReadAllBytes(path);

        var again = _store.Import(export);

        Assert.True(again.Ok);
        Assert.NotEqual(original.Id, again.Value!.Id);
        Assert.Equal(before, Fingerprint(path));
        Assert.Equal(2, Directory.GetFiles(_kit.ProfilesDir).Length);
        Assert.Equal(original.Name, again.Value.Name);
        Assert.True(_store.Get(again.Value.Id).Ok);
    }

    [Fact]
    public void An_import_that_refers_to_a_missing_image_is_refused_naming_it_and_writes_nothing()
    {
        var missing = new string('7', 64);

        var result = _store.Import(ProfileKit.Mutate(root => ProfileKit.Widget(root, "engine")["look"]!["image"] = missing));

        Assert.Equal(StoreStatus.Invalid, result.Status);
        Assert.Contains(result.Problems, p => p.Contains(missing, StringComparison.Ordinal));
        Assert.False(Directory.Exists(_kit.ProfilesDir) && Directory.GetFiles(_kit.ProfilesDir).Length > 0);
    }

    [Fact]
    public void An_import_over_1_MiB_or_that_is_not_json_is_refused()
    {
        Assert.Equal(StoreStatus.Invalid, _store.Import(new byte[ProfileGate.MaxBytes + 1]).Status);
        Assert.Equal(StoreStatus.Invalid, _store.Import("nope"u8).Status);
    }

    [Fact]
    public void A_duplicate_has_a_new_id_the_name_with_copy_and_the_same_layouts()
    {
        var original = Imported();

        var copy = _store.Duplicate(original.Id).Value!;

        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal(original.Name + " copy", copy.Name);
        Assert.Equal(
            JsonSerializer.Serialize(original.Layouts, ContractJson.Options),
            JsonSerializer.Serialize(_store.Get(copy.Id).Value!.Layouts, ContractJson.Options));
        Assert.Equal(StoreStatus.NotFound, _store.Duplicate("nothing-0000").Status);
    }

    [Fact]
    public void A_duplicate_name_is_cut_to_64_characters_without_splitting_a_surrogate_pair()
    {
        // Counted in characters the user sees, as the schema does: a UTF-16 cut could leave half an emoji, which is not valid text.
        var original = Imported(edit: root => root["name"] = new string('a', 62) + "\U0001F600");

        var copy = _store.Duplicate(original.Id).Value!;

        Assert.Equal(64, copy.Name.EnumerateRunes().Count());
        Assert.Equal(copy.Name, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(copy.Name)));
        Assert.EndsWith("\U0001F600 ", copy.Name, StringComparison.Ordinal);
        Assert.True(_store.Get(copy.Id).Ok);
    }

    // ---- which profile uses which image ----

    [Fact]
    public void The_store_reports_which_profiles_use_an_image_so_that_the_library_can_refuse_to_delete_it()
    {
        // decisions §7: an asset can be deleted only while no profile uses it.
        var demo = Imported();
        var empty = _store.Create("Empty", Game.Ets2).Value!;
        var imageId = ProfileGate.AssetIds(demo)[0];

        Assert.Equal([demo.Id], _store.ProfilesUsing(imageId));
        Assert.Equal(ProfileGate.AssetIds(demo).ToHashSet(), _store.UsedAssetIds());
        Assert.DoesNotContain(empty.Id, _store.ProfilesUsing(imageId));
        Assert.Empty(_store.ProfilesUsing(new string('9', 64)));
    }

    [Fact]
    public void A_profile_that_only_misses_an_image_still_counts_as_using_its_other_images()
    {
        // Deleting an image out from under a profile that merely lost one of its files would make it unfixable.
        var demo = Imported();
        var imageId = ProfileGate.AssetIds(demo)[0];
        Assert.True(_kit.Assets.Delete(ProfileGate.AssetIds(demo)[1]));

        Assert.False(_store.List().Profiles.Single().Damaged);
        Assert.Contains(demo.Id, _store.ProfilesUsing(imageId));
    }

    [Fact]
    public void Asset_usage_is_one_pass_from_image_to_the_profiles_that_use_it()
    {
        var first = Imported();
        var second = Imported();
        _store.Create("Empty", Game.Ets2);

        var usage = _store.AssetUsage();

        Assert.Equal(ProfileGate.AssetIds(first).Order(StringComparer.Ordinal), usage.Keys.Order(StringComparer.Ordinal));
        Assert.All(usage.Values, ids => Assert.Equal(new[] { first.Id, second.Id }.Order(StringComparer.Ordinal), ids));
    }

    // ---- what "damaged" means for a stored file ----

    public static TheoryData<string> LayoutFaults() => ["missing-image", "unknown-template", "overlap"];

    private static byte[] WithFault(string fault, string id) => ProfileKit.Mutate(root =>
    {
        root["id"] = id;
        switch (fault)
        {
            case "missing-image":
                ProfileKit.Widget(root, "engine")["look"]!["image"] = new string('0', 64);
                break;
            case "unknown-template":
                ProfileKit.Layout(root)["templateId"] = "no-such-phone";
                break;
            default:
                var copy = ProfileKit.Widget(root, "beacon").DeepClone().AsObject();
                copy["id"] = "beacon-twin";
                ProfileKit.Layout(root)["widgets"]!.AsArray().Add(copy);
                break;
        }
    });

    [Theory]
    [MemberData(nameof(LayoutFaults))]
    public void A_stored_profile_with_an_environment_or_layout_fault_is_not_damaged_it_runs_and_it_cannot_be_saved_until_fixed(string fault)
    {
        // A missing image, a template removed by an update or an overlap must not take the user's profile away from them or
        // from the phones (the engine needs only the profile check), but a Save still has to be refused until it is fixed.
        var id = "faulty-0001";
        WriteRaw(id, WithFault(fault, id));

        Assert.False(_store.List().Profiles.Single().Damaged);
        var opened = _store.Get(id);
        Assert.True(opened.Ok);
        Assert.True(_store.Duplicate(id).Ok);
        Assert.True(_store.Activate(id).Ok);
        Assert.Equal(id, _store.ActiveId);
        Assert.NotNull(_kit.Session.Engine);

        var save = _store.Save(id, opened.Value!);
        Assert.Equal(StoreStatus.Invalid, save.Status);
        Assert.NotEmpty(save.Problems);
    }

    // ---- activating what already runs, and the cache ----

    [Fact]
    public void Activating_the_profile_that_already_runs_changes_nothing()
    {
        // The tray and the top bar can send this by a stray click; it must not reset the driver's switches or rewrite the file.
        var writes = 0;
        var store = _kit.NewStore((path, bytes) =>
        {
            writes++;
            AtomicFile.WriteAllBytes(path, bytes);
        });
        var profile = Imported(store);
        Assert.True(store.Activate(profile.Id).Ok);
        var engine = _kit.Session.Engine!;
        SelectLights(engine, 2);
        var writesBefore = writes;

        var again = store.Activate(profile.Id);

        Assert.True(again.Ok);
        Assert.Equal(writesBefore, writes);
        Assert.Same(engine, _kit.Session.Engine);
        Assert.Equal(2, LightsPosition(_kit.Session.Engine!));
    }

    [Fact]
    public void Activating_the_active_profile_after_a_start_without_an_engine_still_publishes_it()
    {
        // The file was damaged at start, so the phones got noLayout; once repaired, the same id must reach them.
        var id = "repaired-0001";
        var path = WriteRaw(id, "{"u8.ToArray());
        File.WriteAllText(_kit.ActiveFile, $$"""{"schemaVersion":1,"profileId":"{{id}}"}""");
        var store = _kit.NewStore();
        Assert.Null(store.ActiveId);
        File.WriteAllBytes(path, ProfileKit.Mutate(root => root["id"] = id));

        Assert.True(store.Activate(id).Ok);

        Assert.NotNull(_kit.Session.Engine);
    }

    [Fact]
    public void A_file_edited_outside_the_app_is_picked_up_by_the_next_list_and_files_added_or_removed_show_up()
    {
        var profile = Imported();
        Assert.Equal("Simulator demo", _store.List().Profiles.Single().Name);
        var path = _kit.ProfileFile(profile.Id);

        File.WriteAllBytes(path, ProfileKit.Mutate(root =>
        {
            root["id"] = profile.Id;
            root["name"] = "Edited outside";
        }));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        Assert.Equal("Edited outside", _store.List().Profiles.Single().Name);

        WriteRaw("added-0001", "{"u8.ToArray());
        Assert.Equal(2, _store.List().Profiles.Count);
        File.Delete(path);
        Assert.Equal(["added-0001"], _store.List().Profiles.Select(p => p.Id));
    }

    [Fact]
    public void An_unchanged_file_is_not_read_and_gated_again_so_polling_the_list_is_cheap()
    {
        // The cache key is (path, length, last write time). Content swapped under an identical key is not noticed, which is exactly
        // what shows that the file is not parsed on every List; a real edit changes the time (see the test above).
        var profile = Imported();
        Assert.False(_store.List().Profiles.Single().Damaged);
        var path = _kit.ProfileFile(profile.Id);
        var length = new FileInfo(path).Length;
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, new byte[length]);
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.False(_store.List().Profiles.Single().Damaged);
    }

    [Fact]
    public void A_returned_profile_is_a_copy_so_a_failed_save_cannot_change_what_the_store_believes()
    {
        var profile = Imported();
        var opened = _store.Get(profile.Id).Value!;
        opened.Name = "Changed in the editor";

        Assert.Equal("Simulator demo", _store.Get(profile.Id).Value!.Name);
    }

    // ---- concurrency ----

    [Fact]
    public void Concurrent_saves_leave_one_valid_profile_and_no_temporary_files()
    {
        var profile = Imported();
        Assert.True(_store.Activate(profile.Id).Ok);

        Parallel.For(0, 16, i =>
        {
            var edited = ProfileKit.Parse(ProfileKit.Bytes(profile));
            edited.Name = "Name " + i;
            Assert.True(_store.Save(profile.Id, edited).Ok);
        });

        var stored = _store.Get(profile.Id).Value!;
        Assert.Matches(@"\AName \d+\z", stored.Name);
        Assert.Empty(Directory.GetFiles(_kit.ProfilesDir, ".*"));
    }
}
