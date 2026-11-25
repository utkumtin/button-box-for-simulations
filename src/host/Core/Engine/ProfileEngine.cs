using ButtonBox.Core.Contracts;

namespace ButtonBox.Core.Engine;

/// <summary>
/// One engine per profile. Holds one <see cref="LayoutEngine"/> per device template, so devices with the
/// same template share commanded switch positions.
/// </summary>
public sealed class ProfileEngine
{
    private readonly Dictionary<string, LayoutEngine> _layouts = new();
    private readonly Dictionary<string, GameOutput> _outputs = new();

    /// <param name="time">The clock for the behaviour timers (amber, inference pause); null is the system clock.</param>
    /// <exception cref="InvalidOperationException">The profile and catalog do not pass <see cref="ProfileCheck"/>.</exception>
    public ProfileEngine(Profile profile, Catalog catalog, TimeProvider? time = null)
    {
        var problems = ProfileCheck.Problems(profile, catalog);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Profile is not usable: " + string.Join(" ", problems));
        }

        Game = profile.Game;
        var outputs = _outputs;
        foreach (var action in catalog.Actions)
        {
            outputs[action.Id] = action.Output switch
            {
                ScsOutput scs => new ScsInput(scs.Input),
                VjoyOutput vjoy => new VJoyButton(vjoy.Button),
                _ => throw new InvalidOperationException($"Catalog action '{action.Id}' has an unknown output."),
            };
        }

        foreach (var layout in profile.Layouts)
        {
            _layouts.Add(layout.TemplateId, new LayoutEngine(layout, outputs, time ?? TimeProvider.System));
        }
    }

    /// <summary>The game of the profile; the app picks the output backend from it.</summary>
    public Game Game { get; }

    /// <summary>Every layout engine of the profile, one per template.</summary>
    public IReadOnlyCollection<LayoutEngine> Layouts => _layouts.Values;

    /// <summary>The engine for a device template, or null when the profile has no layout for it.</summary>
    public LayoutEngine? ForTemplate(string templateId) =>
        _layouts.TryGetValue(templateId, out var engine) ? engine : null;

    /// <summary>
    /// Maps a catalog action id to its game output (editor test mode, decisions §11). False when the active catalog has no such action.
    /// </summary>
    public bool TryGetOutput(string actionId, out GameOutput output)
    {
        ArgumentNullException.ThrowIfNull(actionId);
        return _outputs.TryGetValue(actionId, out output!);
    }

    /// <summary>Every switch back to position 0: a profile switch resets all commanded positions (decisions §3 "Publishing").</summary>
    public void ResetSwitchPositions()
    {
        foreach (var layout in _layouts.Values)
        {
            layout.ResetPositions();
        }
    }

    /// <summary>
    /// Keeps the commanded switch positions across a Save (decisions §3 "Publishing"): per template present in both engines, a switch
    /// keeps its position when its widget id exists in both and its number of positions is unchanged; everything else starts at 0.
    /// Call on the new engine, before it is published. The previous engine may still be handling gestures.
    /// </summary>
    public void CarryPositionsFrom(ProfileEngine previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        foreach (var (template, layout) in _layouts)
        {
            if (previous._layouts.TryGetValue(template, out var before))
            {
                layout.CarryPositionsFrom(before);
            }
        }
    }
}
