using System.Text.Json;
using ButtonBox.App.Pairing;
using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;
using ButtonBox.Security;

namespace ButtonBox.App.Profiles;

/// <summary>
/// What the gate found. <see cref="Profile"/> is the parsed profile, set whenever the bytes parsed (also when later steps found
/// problems). <see cref="StructureValid"/> is true once <see cref="ProfileValidator"/> accepted it, so its fields can be read safely.
/// </summary>
public sealed record GateResult(Profile? Profile, IReadOnlyList<string> Problems, bool StructureValid)
{
    public bool Ok => Problems.Count == 0 && Profile is not null;
}

/// <summary>
/// The one validation pipeline for a profile that is loaded, saved or imported (decisions §9): size, JSON depth, deserialization,
/// hand-written bounds, the game's catalog, the profile check, known templates, minimum widget size, overlap and the asset library.
/// At most 100 problems, each one sentence of at most 300 characters; no input bytes are echoed.
/// </summary>
public sealed class ProfileGate(CatalogStore catalogs, TemplateStore templates, AssetLibrary assets)
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxDepth = 32;

    private const int MaxProblems = ProfileValidator.MaxProblems;
    private const int MaxProblemLength = 300;

    /// <summary>Size, depth and deserialization. The profile is not checked yet; call <see cref="Check"/>.</summary>
    public GateResult Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxBytes)
        {
            return Fail("The profile file is larger than 1 MiB.");
        }

        try
        {
            if (!DepthOk(json))
            {
                return Fail($"The profile is nested more than {MaxDepth} levels deep.");
            }

            var profile = JsonSerializer.Deserialize<Profile>(json, ContractJson.Options);
            return profile is null ? Fail("The file is not valid JSON or not a profile.") : new GateResult(profile, [], false);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return Fail("The file is not valid JSON or not a profile.");
        }
    }

    /// <summary>
    /// The full gate, for Save and Import: <see cref="CheckStored"/> plus known templates, minimum widget size, overlap and the
    /// asset library. Later steps run only when the bounds check passed, because they read every field.
    /// </summary>
    public GateResult Check(Profile profile)
    {
        var problems = Structural(profile, out var structureValid);
        if (!structureValid)
        {
            return Finish(profile, problems, structureValid: false);
        }

        CheckLayouts(profile, problems);
        CheckAssets(profile, problems);
        return Finish(profile, problems, structureValid: true);
    }

    /// <summary>
    /// The gate for a profile file that is already stored: bounds check, the game's catalog and the profile check, which is all the
    /// engine needs. Environment and layout rules (templates, size, overlap, images) do not make a stored file damaged; Save and
    /// Import still apply them through <see cref="Check"/>.
    /// </summary>
    public GateResult CheckStored(Profile profile)
    {
        var problems = Structural(profile, out var structureValid);
        return Finish(profile, problems, structureValid);
    }

    private List<string> Structural(Profile profile, out bool structureValid)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var problems = new List<string>(ProfileValidator.Problems(profile));
        structureValid = problems.Count == 0;
        if (!structureValid)
        {
            return problems;
        }

        if (!catalogs.TryGet(profile.Game, out var catalog))
        {
            problems.Add("There is no action catalog for the profile's game.");
        }
        else
        {
            problems.AddRange(ProfileCheck.Problems(profile, catalog));
        }

        return problems;
    }

    /// <summary>Every image the profile refers to (looks and rotary layers), without repeats. The profile must have passed <see cref="ProfileValidator"/>.</summary>
    public static IReadOnlyList<string> AssetIds(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var widget in profile.Layouts.SelectMany(layout => layout.Widgets))
        {
            var look = widget switch
            {
                ToggleWidget toggle => toggle.Look,
                DualActionWidget dual => dual.Look,
                _ => null,
            };
            if (look is ImagesLook images)
            {
                ids.Add(images.Image);
                if (images.On is { } on)
                {
                    ids.Add(on);
                }
            }

            if (widget is SwitchWidget { Rotary: { } rotary })
            {
                foreach (var layer in new[] { rotary.Base, rotary.Knob, rotary.Highlight })
                {
                    if (layer is not null)
                    {
                        ids.Add(layer.Asset);
                    }
                }
            }
        }

        return [.. ids];
    }

    private void CheckLayouts(Profile profile, List<string> problems)
    {
        foreach (var layout in profile.Layouts)
        {
            var where = $"Layout '{layout.TemplateId}'";
            if (!templates.TryGet(layout.TemplateId, out var template))
            {
                problems.Add($"{where}: the device template is not known.");
                continue;
            }

            foreach (var widget in layout.Widgets)
            {
                if (!WidgetSize.Fits(widget, layout.Grid, template.Screen))
                {
                    var (width, height) = WidgetSize.Minimum(WidgetSize.TypeOf(widget));
                    problems.Add($"{where}, widget '{widget.Id}': smaller than the minimum of {width:0} x {height:0} pt on this device.");
                }
            }

            // Cell spans are half-open ranges: widgets that only touch at an edge do not overlap.
            for (var i = 0; i < layout.Widgets.Count; i++)
            {
                for (var j = i + 1; j < layout.Widgets.Count; j++)
                {
                    if (Overlap(layout.Widgets[i].Cell, layout.Widgets[j].Cell))
                    {
                        problems.Add($"{where}: widgets '{layout.Widgets[i].Id}' and '{layout.Widgets[j].Id}' overlap.");
                    }
                }
            }
        }
    }

    private void CheckAssets(Profile profile, List<string> problems)
    {
        foreach (var id in AssetIds(profile))
        {
            if (!assets.Contains(id))
            {
                problems.Add($"The image {id} is not in the asset library.");
            }
        }
    }

    private static bool Overlap(GridCell a, GridCell b) =>
        a.Col < b.Col + b.ColSpan && b.Col < a.Col + a.ColSpan
        && a.Row < b.Row + b.RowSpan && b.Row < a.Row + a.RowSpan;

    // The serializer's own limit is 64; the schema never needs more than a handful of levels.
    private static bool DepthOk(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 128 });
        while (reader.Read())
        {
            // The first array or object is at depth 0, so the 33rd nested level starts at depth 32.
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && reader.CurrentDepth >= MaxDepth)
            {
                return false;
            }
        }

        return true;
    }

    private static GateResult Fail(string problem) => new(null, [problem], false);

    private static GateResult Finish(Profile profile, List<string> problems, bool structureValid)
    {
        var limited = problems.Take(MaxProblems).Select(p => p.Length <= MaxProblemLength ? p : p[..MaxProblemLength]).ToList();
        return new GateResult(profile, limited, structureValid);
    }
}
