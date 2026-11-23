using ButtonBox.Core.Contracts;

namespace ButtonBox.Core.Engine;

/// <summary>
/// The minimum physical widget size (decisions §6). The usable area is the template's landscape screen minus its safe-area
/// insets (CSS px = pt); a widget's touch area is its whole cell span. Toggle and dual-action need 44 × 44 pt; encoder and switch
/// need 88 × 44 pt because they split into left/right halves of at least 44 pt each.
/// The TypeScript twin is <c>@bbs/widgets</c> <c>minSize.ts</c>; both are tested against <c>minSize.cases.json</c>.
/// </summary>
public static class WidgetSize
{
    public const double MinTouchPt = 44;

    // Cell sizes are divisions, so a span that is exactly 44 pt can come out a hair below it.
    private const double Epsilon = 1e-9;

    public static (double Width, double Height) Minimum(WidgetType type) => type switch
    {
        WidgetType.Encoder or WidgetType.Switch => (2 * MinTouchPt, MinTouchPt),
        _ => (MinTouchPt, MinTouchPt),
    };

    public static WidgetType TypeOf(Widget widget) => widget switch
    {
        ToggleWidget => WidgetType.Toggle,
        EncoderWidget => WidgetType.Encoder,
        SwitchWidget => WidgetType.Switch,
        DualActionWidget => WidgetType.DualAction,
        _ => throw new ArgumentException("Unknown widget type.", nameof(widget)),
    };

    public static (double Width, double Height) UsableArea(ScreenInfo screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        return (screen.Width - screen.SafeArea.Left - screen.SafeArea.Right, screen.Height - screen.SafeArea.Top - screen.SafeArea.Bottom);
    }

    public static (double Width, double Height) CellSize(Grid grid, ScreenInfo screen)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var (width, height) = UsableArea(screen);
        return (width / grid.Columns, height / grid.Rows);
    }

    /// <summary>True when a widget of this type spanning these cells is at least the minimum size.</summary>
    public static bool Fits(WidgetType type, int colSpan, int rowSpan, Grid grid, ScreenInfo screen)
    {
        var (cellWidth, cellHeight) = CellSize(grid, screen);
        var (minWidth, minHeight) = Minimum(type);
        return colSpan * cellWidth >= minWidth - Epsilon && rowSpan * cellHeight >= minHeight - Epsilon;
    }

    public static bool Fits(Widget widget, Grid grid, ScreenInfo screen)
    {
        ArgumentNullException.ThrowIfNull(widget);
        return Fits(TypeOf(widget), widget.Cell.ColSpan, widget.Cell.RowSpan, grid, screen);
    }

    /// <summary>The smallest span that fits. It can exceed the grid when the usable area is very small; then nothing of this type fits.</summary>
    public static (int ColSpan, int RowSpan) MinimumSpan(WidgetType type, Grid grid, ScreenInfo screen)
    {
        var (cellWidth, cellHeight) = CellSize(grid, screen);
        var (minWidth, minHeight) = Minimum(type);
        return (Span(minWidth, cellWidth), Span(minHeight, cellHeight));
    }

    private static int Span(double minimum, double cell) => Math.Max(1, (int)Math.Ceiling(minimum / cell - Epsilon));
}

/// <summary>Widget types as named in the layout schema's discriminator.</summary>
public enum WidgetType
{
    Toggle,
    Encoder,
    Switch,
    DualAction,
}
