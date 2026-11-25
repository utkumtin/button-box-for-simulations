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
                            problems.Add(sw.Behavior is { } switchBehavior
                                ? $"{where}: behavior '{BehaviorName(switchBehavior)}' does not allow 'cycle'."
                                : $"{where}: 'cycle' (press-count switching) is not supported yet.");
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
                        if (sw.Behavior is { } behavior)
                        {
                            SwitchBehaviorProblems(sw, behavior, expectsScs, actions, problems, where);
                        }
                        break;
                    case DualActionWidget dual:
                        if (dual.Tap is null && dual.Hold is null)
                        {
                            problems.Add($"{where}: dual action has neither tap nor hold.");
                        }
                        if (dual.HoldMode == DualActionWidgetHoldMode.StarterTakeover && dual.Behavior is null)
                        {
                            problems.Add($"{where}: holdMode 'starterTakeover' is not supported yet.");
                        }
                        Need(dual.Tap);
                        Need(dual.Hold);
                        if (dual.Behavior is not null)
                        {
                            DualBehaviorProblems(dual, expectsScs, actions, problems, where);
                        }
                        break;
                }
            }
        }

        return problems;
    }

    private static string BehaviorName(SwitchBehavior behavior) => behavior == SwitchBehavior.Headlights ? "headlights" : "wipers";

    // The layout lists the real actions and the host checks they match the behaviour (decisions §5 "Widget behaviours").
    private static void SwitchBehaviorProblems(SwitchWidget sw, SwitchBehavior behavior, bool expectsScs, Dictionary<string, CatalogAction> actions, List<string> problems, string where)
    {
        var name = BehaviorName(behavior);
        if (!expectsScs)
        {
            problems.Add($"{where}: behavior '{name}' is only available for ETS2 and ATS profiles.");
            return;
        }

        string[] expected = behavior == SwitchBehavior.Headlights
            ? [ScsActions.LightOff, ScsActions.LightPark, ScsActions.LightOn, ScsActions.LightOn]
            : [ScsActions.WipersOff, ScsActions.WipersAuto, ScsActions.WipersLow, ScsActions.WipersFast];
        if (sw.Positions.Count != expected.Length)
        {
            problems.Add($"{where}: behavior '{name}' needs exactly {expected.Length} positions.");
        }
        else
        {
            for (var i = 0; i < expected.Length; i++)
            {
                var action = sw.Positions[i].Action;
                if (action is not null && action != expected[i])
                {
                    problems.Add($"{where}: behavior '{name}' needs action '{expected[i]}' at position {i}.");
                }
            }
        }

        if (behavior == SwitchBehavior.Headlights && !actions.ContainsKey(ScsActions.HighBeam))
        {
            problems.Add($"{where}: behavior 'headlights' needs the catalog action '{ScsActions.HighBeam}'.");
        }
    }

    private static void DualBehaviorProblems(DualActionWidget dual, bool expectsScs, Dictionary<string, CatalogAction> actions, List<string> problems, string where)
    {
        if (!expectsScs)
        {
            problems.Add($"{where}: behavior 'engineStartStop' is only available for ETS2 and ATS profiles.");
            return;
        }

        if (dual.Tap != ScsActions.IgnitionOn)
        {
            problems.Add($"{where}: behavior 'engineStartStop' needs tap action '{ScsActions.IgnitionOn}'.");
        }
        if (dual.Hold != ScsActions.Starter)
        {
            problems.Add($"{where}: behavior 'engineStartStop' needs hold action '{ScsActions.Starter}'.");
        }
        if (dual.HoldMode != DualActionWidgetHoldMode.Fire)
        {
            problems.Add($"{where}: behavior 'engineStartStop' needs holdMode 'fire'.");
        }
        if (!actions.ContainsKey(ScsActions.IgnitionOff))
        {
            problems.Add($"{where}: behavior 'engineStartStop' needs the catalog action '{ScsActions.IgnitionOff}'.");
        }
        if (dual.State is not null)
        {
            problems.Add($"{where}: behavior 'engineStartStop' shows its own lit and LED, so a 'state' binding is not allowed.");
        }
    }
}
