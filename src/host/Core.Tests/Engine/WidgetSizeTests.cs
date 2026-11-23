using System.Text.Json;
using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;
using Xunit;

namespace ButtonBox.Core.Tests.Engine;

// Why: the editor stops a widget from shrinking below a fingertip and the host refuses such a profile on save. Both sides
// read the same cases (minSize.cases.json, also run by @bbs/widgets minSize.test.ts), so they can never disagree about what fits.
public class WidgetSizeTests
{
    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in LoadCases())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Shared_case(string name)
    {
        var c = LoadCases().Single(x => x.GetProperty("name").GetString() == name);
        var screen = c.GetProperty("screen").Deserialize<ScreenInfo>(ContractJson.Options)!;
        var grid = c.GetProperty("grid").Deserialize<Grid>(ContractJson.Options)!;
        var type = c.GetProperty("type").GetString() switch
        {
            "toggle" => WidgetType.Toggle,
            "encoder" => WidgetType.Encoder,
            "switch" => WidgetType.Switch,
            "dualAction" => WidgetType.DualAction,
            var other => throw new InvalidDataException("Unknown widget type " + other),
        };

        Assert.Equal(c.GetProperty("fits").GetBoolean(), WidgetSize.Fits(type, c.GetProperty("colSpan").GetInt32(), c.GetProperty("rowSpan").GetInt32(), grid, screen));
        var span = c.GetProperty("minimumSpan");
        Assert.Equal((span.GetProperty("colSpan").GetInt32(), span.GetProperty("rowSpan").GetInt32()), WidgetSize.MinimumSpan(type, grid, screen));
    }

    [Fact]
    public void Fixture_has_cases() => Assert.True(LoadCases().Count > 10);

    [Fact]
    public void Widget_overload_reads_type_and_span()
    {
        var screen = new ScreenInfo { Width = 852, Height = 393, SafeArea = new SafeArea { Top = 0, Right = 59, Bottom = 20, Left = 59 }, PixelRatio = 3 };
        var grid = new Grid { Columns = 24, Rows = 12 };
        var cell = new GridCell { Col = 0, Row = 0, ColSpan = 2, RowSpan = 2 };
        Assert.True(WidgetSize.Fits(new ToggleWidget { Id = "t", Cell = cell, Action = "a" }, grid, screen));
        Assert.False(WidgetSize.Fits(new EncoderWidget { Id = "e", Cell = cell, Minus = "a", Plus = "b" }, grid, screen));
    }

    private static List<JsonElement> LoadCases()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "web", "packages", "widgets", "src", "minSize.cases.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return [.. doc.RootElement.GetProperty("cases").EnumerateArray().Select(e => e.Clone())];
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("src/web/packages/widgets/src/minSize.cases.json not found above " + AppContext.BaseDirectory);
    }
}
