using ButtonBox.Core.Contracts;

namespace ButtonBox.Core.Engine;

/// <summary>
/// One engine per profile. Holds one <see cref="LayoutEngine"/> per device template, so devices with the
/// same template share commanded switch positions.
/// </summary>
public sealed class ProfileEngine
{
    private readonly Dictionary<string, LayoutEngine> _layouts = new();

    /// <exception cref="InvalidOperationException">The profile and catalog do not pass <see cref="ProfileCheck"/>.</exception>
    public ProfileEngine(Profile profile, Catalog catalog)
    {
        var problems = ProfileCheck.Problems(profile, catalog);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Profile is not usable: " + string.Join(" ", problems));
        }

        var outputs = new Dictionary<string, GameOutput>();
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
            _layouts.Add(layout.TemplateId, new LayoutEngine(layout, outputs));
        }
    }

    /// <summary>The engine for a device template, or null when the profile has no layout for it.</summary>
    public LayoutEngine? ForTemplate(string templateId) =>
        _layouts.TryGetValue(templateId, out var engine) ? engine : null;
}
