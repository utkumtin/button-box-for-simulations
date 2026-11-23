using System.Text.RegularExpressions;
using ButtonBox.Core.Contracts;

namespace ButtonBox.Security;

/// <summary>
/// Hand-written bounds, pattern and null checks of a deserialized <see cref="Profile"/> and its layouts (decisions §9).
/// The public constants mirror /schemas; a test fails when they drift.
/// Runs after <c>JsonSerializer.Deserialize&lt;Profile&gt;(…, ContractJson.Options)</c>. That does not reject <c>null</c>
/// for required reference properties, so every required string and object is null-checked here.
/// Not covered here: catalog references, minimum widget size, overlap and asset existence.
/// </summary>
public static class ProfileValidator
{
    public const int SchemaVersion = 1;
    public const int MaxLayouts = 16;
    public const int MaxWidgets = 256;
    public const int GridColumnsMin = 1;
    public const int GridColumnsMax = 24;
    public const int GridRowsMin = 1;
    public const int GridRowsMax = 16;
    public const int CellPositionMin = 0;
    public const int CellPositionMax = 64;
    public const int CellSpanMin = 1;
    public const int CellSpanMax = 64;
    public const int PositionsMin = 2;
    public const int PositionsMax = 8;
    public const double AngleMin = -180;
    public const double AngleMax = 180;
    public const double ScaleMin = 0.1;
    public const double ScaleMax = 2;
    public const double OffsetMin = -0.5;
    public const double OffsetMax = 0.5;
    public const int LabelMinLength = 1;
    public const int LabelMaxLength = 32;
    public const int ProfileNameMinLength = 1;
    public const int ProfileNameMaxLength = 64;
    public const int AssetIdLength = 64;
    public const int TelemetryFieldMaxLength = 64;

    /// <summary>At most this many problems are collected; the rest of a hostile profile is not described.</summary>
    public const int MaxProblems = 100;

    private const int MaxProblemLength = 300;

    // Patterns are anchored with \A…\z: in .NET "$" also matches before a trailing "\n".
    public const string AssetIdPattern = @"\A[0-9a-f]{64}\z";
    public const string TelemetryFieldPattern = @"\A[A-Za-z][A-Za-z0-9_.]{0,63}\z";
    public const string LabelPattern = @"\A[^\u0000-\u001f\u007f]+\z";
    public const string ProfileNamePattern = @"\A[^\u0000-\u001f\u007f]+\z";

    /// <summary>Wire names of <see cref="DualActionWidgetHoldMode"/> as in the schema.</summary>
    public static readonly IReadOnlyList<string> HoldModeNames = ["fire", "starterTakeover"];

    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
    private static readonly Regex AssetIdRegex = new(AssetIdPattern, Options);
    private static readonly Regex TelemetryFieldRegex = new(TelemetryFieldPattern, Options);
    private static readonly Regex LabelRegex = new(LabelPattern, Options);
    private static readonly Regex ProfileNameRegex = new(ProfileNamePattern, Options);

    public static bool IsValidAssetId(string? value) =>
        value is { Length: AssetIdLength } && AssetIdRegex.IsMatch(value);

    public static bool IsValidTelemetryField(string? value) =>
        value is { Length: <= TelemetryFieldMaxLength } && TelemetryFieldRegex.IsMatch(value);

    /// <summary>1..32 Unicode code points, no U+0000–U+001F or U+007F. Bidi controls such as U+202E are allowed by the schema.</summary>
    public static bool IsValidLabel(string? value) =>
        value is not null
        && ContractValidator.HasLengthInCodePoints(value, LabelMinLength, LabelMaxLength)
        && LabelRegex.IsMatch(value);

    /// <summary>1..64 Unicode code points, no U+0000–U+001F or U+007F.</summary>
    public static bool IsValidProfileName(string? value) =>
        value is not null
        && ContractValidator.HasLengthInCodePoints(value, ProfileNameMinLength, ProfileNameMaxLength)
        && ProfileNameRegex.IsMatch(value);

    /// <summary>Empty when the profile is valid. Each problem is one sentence of at most 300 characters; at most 100 are returned.</summary>
    public static IReadOnlyList<string> Problems(Profile? profile)
    {
        var sink = new Sink();
        if (profile is null)
        {
            sink.Add("The profile is missing.");
            return sink.Items;
        }

        if (profile.SchemaVersion != SchemaVersion)
        {
            sink.Add($"Profile: schemaVersion must be {SchemaVersion}.");
        }

        if (!ContractValidator.IsValidId(profile.Id))
        {
            sink.Add("Profile: the id is not valid.");
        }

        if (!IsValidProfileName(profile.Name))
        {
            sink.Add($"Profile: the name must be {ProfileNameMinLength} to {ProfileNameMaxLength} characters without control characters.");
        }

        if (!Enum.IsDefined(profile.Game))
        {
            sink.Add("Profile: the game is not known.");
        }

        if (profile.Layouts is null)
        {
            sink.Add("Profile: layouts are missing.");
            return sink.Items;
        }

        if (profile.Layouts.Count > MaxLayouts)
        {
            sink.Add($"Profile: at most {MaxLayouts} layouts are allowed.");
        }

        for (var i = 0; i < profile.Layouts.Count && i < MaxLayouts && !sink.Full; i++)
        {
            CheckLayout(sink, profile.Layouts[i], i);
        }

        return sink.Items;
    }

    private static void CheckLayout(Sink sink, Layout? layout, int index)
    {
        if (layout is null)
        {
            sink.Add($"Layout {index + 1} is missing.");
            return;
        }

        var where = Describe("Layout", layout.TemplateId, index);
        if (layout.SchemaVersion != SchemaVersion)
        {
            sink.Add($"{where}: schemaVersion must be {SchemaVersion}.");
        }

        if (!ContractValidator.IsValidId(layout.TemplateId))
        {
            sink.Add($"{where}: the templateId is not valid.");
        }

        if (layout.Grid is null)
        {
            sink.Add($"{where}: the grid is missing.");
        }
        else
        {
            if (layout.Grid.Columns is < GridColumnsMin or > GridColumnsMax)
            {
                sink.Add($"{where}: the grid needs {GridColumnsMin} to {GridColumnsMax} columns.");
            }

            if (layout.Grid.Rows is < GridRowsMin or > GridRowsMax)
            {
                sink.Add($"{where}: the grid needs {GridRowsMin} to {GridRowsMax} rows.");
            }
        }

        if (layout.Widgets is null)
        {
            sink.Add($"{where}: widgets are missing.");
            return;
        }

        if (layout.Widgets.Count > MaxWidgets)
        {
            sink.Add($"{where}: at most {MaxWidgets} widgets are allowed.");
        }

        for (var i = 0; i < layout.Widgets.Count && i < MaxWidgets && !sink.Full; i++)
        {
            CheckWidget(sink, layout.Widgets[i], where, i);
        }
    }

    private static void CheckWidget(Sink sink, Widget? widget, string layoutWhere, int index)
    {
        if (widget is null)
        {
            sink.Add($"{layoutWhere}, widget {index + 1} is missing.");
            return;
        }

        var where = $"{layoutWhere}, {Describe("widget", widget.Id, index, capitalize: false)}";
        if (!ContractValidator.IsValidId(widget.Id))
        {
            sink.Add($"{where}: the id is not valid.");
        }

        CheckCell(sink, widget.Cell, where);
        if (widget.Label is not null && !IsValidLabel(widget.Label))
        {
            sink.Add($"{where}: the label must be {LabelMinLength} to {LabelMaxLength} characters without control characters.");
        }

        if (widget.State is { } state)
        {
            if (state.Lit is not null && !IsValidTelemetryField(state.Lit))
            {
                sink.Add($"{where}: state.lit is not a valid telemetry field name.");
            }

            if (state.Led is not null && !IsValidTelemetryField(state.Led))
            {
                sink.Add($"{where}: state.led is not a valid telemetry field name.");
            }
        }

        switch (widget)
        {
            case ToggleWidget toggle:
                RequireId(sink, toggle.Action, where, "action");
                CheckLook(sink, toggle.Look, where);
                break;
            case EncoderWidget encoder:
                RequireId(sink, encoder.Minus, where, "minus");
                RequireId(sink, encoder.Plus, where, "plus");
                break;
            case SwitchWidget sw:
                CheckSwitch(sink, sw, where);
                break;
            case DualActionWidget dual:
                OptionalId(sink, dual.Tap, where, "tap");
                OptionalId(sink, dual.Hold, where, "hold");
                if (!Enum.IsDefined(dual.HoldMode))
                {
                    sink.Add($"{where}: the holdMode is not known.");
                }

                CheckLook(sink, dual.Look, where);
                break;
            default:
                sink.Add($"{where}: the widget type is not known.");
                break;
        }
    }

    private static void CheckCell(Sink sink, GridCell? cell, string where)
    {
        if (cell is null)
        {
            sink.Add($"{where}: the cell is missing.");
            return;
        }

        if (cell.Col is < CellPositionMin or > CellPositionMax || cell.Row is < CellPositionMin or > CellPositionMax)
        {
            sink.Add($"{where}: the cell position must be {CellPositionMin} to {CellPositionMax}.");
        }

        if (cell.ColSpan is < CellSpanMin or > CellSpanMax || cell.RowSpan is < CellSpanMin or > CellSpanMax)
        {
            sink.Add($"{where}: the cell span must be {CellSpanMin} to {CellSpanMax}.");
        }
    }

    private static void CheckSwitch(Sink sink, SwitchWidget widget, string where)
    {
        OptionalId(sink, widget.Cycle, where, "cycle");
        if (widget.Positions is null)
        {
            sink.Add($"{where}: positions are missing.");
        }
        else
        {
            if (widget.Positions.Count is < PositionsMin or > PositionsMax)
            {
                sink.Add($"{where}: a switch needs {PositionsMin} to {PositionsMax} positions.");
            }

            // Bounded: more than PositionsMax positions is already reported, the rest is not walked.
            for (var i = 0; i < widget.Positions.Count && i < PositionsMax && !sink.Full; i++)
            {
                CheckPosition(sink, widget.Positions[i], $"{where}, position {i + 1}");
            }
        }

        if (widget.Rotary is { } rotary)
        {
            CheckLayer(sink, rotary.Base, where, "rotary.base");
            CheckLayer(sink, rotary.Knob, where, "rotary.knob");
            CheckLayer(sink, rotary.Highlight, where, "rotary.highlight");
        }
    }

    private static void CheckPosition(Sink sink, SwitchPosition? position, string where)
    {
        if (position is null)
        {
            sink.Add($"{where} is missing.");
            return;
        }

        if (!InRange(position.Angle, AngleMin, AngleMax))
        {
            sink.Add($"{where}: the angle must be {AngleMin} to {AngleMax} degrees.");
        }

        if (position.Label is not null && !IsValidLabel(position.Label))
        {
            sink.Add($"{where}: the label must be {LabelMinLength} to {LabelMaxLength} characters without control characters.");
        }

        OptionalId(sink, position.Action, where, "action");
        OptionalId(sink, position.Icon, where, "icon");
    }

    private static void CheckLayer(Sink sink, RotaryLayer? layer, string where, string name)
    {
        if (layer is null)
        {
            return;
        }

        if (!IsValidAssetId(layer.Asset))
        {
            sink.Add($"{where}: {name}.asset is not a valid asset id.");
        }

        if (layer.Scale is { } scale && !InRange(scale, ScaleMin, ScaleMax))
        {
            sink.Add($"{where}: {name}.scale must be {ScaleMin} to {ScaleMax}.");
        }

        if (layer.OffsetX is { } x && !InRange(x, OffsetMin, OffsetMax))
        {
            sink.Add($"{where}: {name}.offsetX must be {OffsetMin} to {OffsetMax}.");
        }

        if (layer.OffsetY is { } y && !InRange(y, OffsetMin, OffsetMax))
        {
            sink.Add($"{where}: {name}.offsetY must be {OffsetMin} to {OffsetMax}.");
        }
    }

    private static void CheckLook(Sink sink, Look? look, string where)
    {
        switch (look)
        {
            case null:
                break;
            case IconLook icon:
                RequireId(sink, icon.Icon, where, "look.icon");
                break;
            case ImagesLook images:
                if (!IsValidAssetId(images.Image))
                {
                    sink.Add($"{where}: look.image is not a valid asset id.");
                }

                if (images.On is not null && !IsValidAssetId(images.On))
                {
                    sink.Add($"{where}: look.on is not a valid asset id.");
                }

                break;
            default:
                sink.Add($"{where}: the look mode is not known.");
                break;
        }
    }

    private static void RequireId(Sink sink, string? value, string where, string name)
    {
        if (!ContractValidator.IsValidId(value))
        {
            sink.Add($"{where}: {name} is missing or not a valid id.");
        }
    }

    private static void OptionalId(Sink sink, string? value, string where, string name)
    {
        if (value is not null && !ContractValidator.IsValidId(value))
        {
            sink.Add($"{where}: {name} is not a valid id.");
        }
    }

    // Comparisons with NaN are false, so NaN and infinities fail the range check.
    private static bool InRange(double value, double min, double max) => value >= min && value <= max;

    // Only an id that matches the id pattern is quoted; anything else is named by position, so no hostile text is echoed.
    private static string Describe(string kind, string? id, int index, bool capitalize = true)
    {
        var name = capitalize ? kind : kind.ToLowerInvariant();
        return ContractValidator.IsValidId(id) ? $"{name} '{id}'" : $"{name} {index + 1}";
    }

    private sealed class Sink
    {
        public List<string> Items { get; } = [];

        public bool Full => Items.Count >= MaxProblems;

        public void Add(string problem)
        {
            if (Full)
            {
                return;
            }

            Items.Add(problem.Length <= MaxProblemLength ? problem : problem[..MaxProblemLength]);
        }
    }
}
