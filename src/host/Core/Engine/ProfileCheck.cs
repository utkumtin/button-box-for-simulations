using ButtonBox.Core.Contracts;

namespace ButtonBox.Core.Engine;

/// <summary>
/// Load-time validation of a profile against its catalog. Runtime gestures are only ever mapped
/// through a profile that passed this check, so the engine never meets a dangling action reference.
/// </summary>
public static class ProfileCheck
{
    /// <summary>Human-readable problems; empty when the profile can be used.</summary>
    public static IReadOnlyList<string> Problems(Profile profile, Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(catalog);

        var problems = new List<string>();

        if (catalog.Game != profile.Game)
        {
            problems.Add($"Catalog is for game '{catalog.Game}' but the profile is for '{profile.Game}'.");
        }

        var actions = new Dictionary<string, CatalogAction>();
        var expectsScs = profile.Game is Game.Ets2 or Game.Ats;
        foreach (var action in catalog.Actions)
        {
            if (!actions.TryAdd(action.Id, action))
            {
                problems.Add($"Catalog action id '{action.Id}' is defined more than once.");
            }

            if (expectsScs && action.Output is not ScsOutput)
            {
                problems.Add($"Catalog action '{action.Id}' must use an 'scs' output for game '{profile.Game}'.");
            }
            else if (!expectsScs && action.Output is not VjoyOutput)
            {
                problems.Add($"Catalog action '{action.Id}' must use a 'vjoy' output for game '{profile.Game}'.");
            }
        }

        var templates = new HashSet<string>();
        foreach (var layout in profile.Layouts)
        {
            if (!templates.Add(layout.TemplateId))
            {
                problems.Add($"Profile has more than one layout for template '{layout.TemplateId}'.");
            }

            var widgetIds = new HashSet<string>();
            foreach (var widget in layout.Widgets)
            {
                var where = $"Layout '{layout.TemplateId}', widget '{widget.Id}'";
                if (!widgetIds.Add(widget.Id))
                {
                    problems.Add($"{where}: widget id is used more than once in this layout.");
                }

                var cell = widget.Cell;
                if (cell.Col + cell.ColSpan > layout.Grid.Columns || cell.Row + cell.RowSpan > layout.Grid.Rows)
                {
                    problems.Add($"{where}: cell lies outside the {layout.Grid.Columns}x{layout.Grid.Rows} grid.");
                }

                void Need(string? actionId)
                {
                    if (actionId is not null && !actions.ContainsKey(actionId))
                    {
                        problems.Add($"{where}: action '{actionId}' is not in the catalog.");
                    }
                }

                switch (widget)
                {
                    case ToggleWidget toggle:
                        Need(toggle.Action);
                        break;
                    case EncoderWidget encoder:
                        Need(encoder.Minus);
                        Need(encoder.Plus);
                        break;
                    case SwitchWidget sw:
                        if (sw.Cycle is not null)
                        {
                            problems.Add($"{where}: 'cycle' (press-count switching) is not supported yet.");
                        }
                        for (var i = 0; i < sw.Positions.Count; i++)
                        {
                            var action = sw.Positions[i].Action;
                            if (action is null)
                            {
                                problems.Add($"{where}: position {i} has no action.");
                            }
                            Need(action);
                        }
                        break;
                    case DualActionWidget dual:
                        if (dual.Tap is null && dual.Hold is null)
                        {
                            problems.Add($"{where}: dual action has neither tap nor hold.");
                        }
                        if (dual.HoldMode == DualActionWidgetHoldMode.StarterTakeover)
                        {
                            problems.Add($"{where}: holdMode 'starterTakeover' is not supported yet.");
                        }
                        Need(dual.Tap);
                        Need(dual.Hold);
                        break;
                }
            }
        }

        return problems;
    }
}
