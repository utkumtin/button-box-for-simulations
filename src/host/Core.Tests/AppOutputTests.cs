using Xunit;
namespace ButtonBox.Core.Tests;

// The schema generator and its libraries are build tools. If one of them reaches the App output it would ship.
public class AppOutputTests
{
    // .../Core.Tests/bin/<config>/net10.0/
    private static string Config => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\', '/')).Parent!.Name;

    private static string FindHostDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ButtonBox.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException($"ButtonBox.slnx not found above {AppContext.BaseDirectory}.");
    }

    [Fact]
    public void App_output_contains_no_code_generation_libraries()
    {
        var appOutput = Path.Combine(FindHostDirectory(), "App", "bin", Config, "net10.0-windows");
        Assert.True(Directory.Exists(appOutput), $"App build output is missing: {appOutput}. Build the solution first.");
        Assert.NotEmpty(Directory.GetFiles(appOutput, "ButtonBox.Core.dll"));

        string[] forbidden = ["NJsonSchema*.dll", "Newtonsoft.Json.dll", "Fluid*.dll", "Parlot*.dll", "SchemaGen*"];
        var found = forbidden.SelectMany(p => Directory.GetFiles(appOutput, p, SearchOption.AllDirectories)).ToList();
        Assert.True(found.Count == 0, $"Generator files in the App output: {string.Join(", ", found)}");
    }
}
