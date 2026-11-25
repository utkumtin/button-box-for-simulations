using System.Text;
using ButtonBox.Output.Scs.Install;
using Xunit;

namespace ButtonBox.Output.Scs.Tests.Install;

// Why: libraryfolders.vdf and appmanifest_*.acf are user-writable and decide which executable the pipe server check trusts and
// where the installer writes (decisions §9, §12). The reader must read what Steam really writes, and must treat every other
// input as "not parsed" without throwing, hanging or allocating without bound.
public class VdfTests
{
    // The shapes seen on the user's PC (2026-11-25): tabs, escaped backslashes, nested apps, a hash-like value.
    private const string RealLibraryFolders = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"contentid"		"123456789"
        		"apps"
        		{
        			"227300"		"41490707721"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"apps"
        		{
        			"270880"		"123"
        		}
        	}
        }
        """;

    private const string RealManifest = """
        "AppState"
        {
        	"appid"		"227300"
        	"Universe"		"1"
        	"name"		"Euro Truck Simulator 2"
        	"installdir"		"Euro Truck Simulator 2"
        	"UserConfig"
        	{
        		"language"		"english"
        	}
        }
        """;

    [Fact]
    public void Reads_the_library_folders_file_steam_writes()
    {
        var root = Vdf.TryParse(RealLibraryFolders);
        var folders = root?.GetObject("libraryfolders");
        Assert.NotNull(folders);
        Assert.Equal(@"C:\Program Files (x86)\Steam", folders.GetObject("0")?.GetString("path"));
        Assert.Equal(@"D:\SteamLibrary", folders.GetObject("1")?.GetString("path"));
        Assert.Equal("123", folders.GetObject("1")?.GetObject("apps")?.GetString("270880"));
    }

    [Fact]
    public void Reads_the_app_manifest_steam_writes()
    {
        var state = Vdf.TryParse(RealManifest)?.GetObject("AppState");
        Assert.Equal("Euro Truck Simulator 2", state?.GetString("installdir"));
        Assert.Equal("227300", state?.GetString("appid"));
    }

    // Why: Steam looks keys up without regard to case; a manifest with "InstallDir" must still name the game folder.
    [Fact]
    public void Key_lookup_ignores_case()
    {
        var state = Vdf.TryParse("\"appstate\" { \"InstallDir\" \"X\" }")?.GetObject("AppState");
        Assert.Equal("X", state?.GetString("installdir"));
    }

    [Fact]
    public void Understands_the_escapes_in_quoted_strings()
    {
        var root = Vdf.TryParse("\"k\" \"a\\\\b\\\"c\\nd\\te\"");
        Assert.Equal("a\\b\"c\nd\te", root?.GetString("k"));
    }

    // Why: the escape set is small on purpose; an unknown escape must not swallow the next character or fail a real manifest.
    [Fact]
    public void Keeps_an_unknown_escape_as_written()
    {
        Assert.Equal("a\\qb", Vdf.TryParse("\"k\" \"a\\qb\"")?.GetString("k"));
    }

    [Fact]
    public void Skips_comments_and_blank_space()
    {
        var root = Vdf.TryParse("// header\n\"a\" // trailing\n{\n  // inside\n  \"b\" \"1\"\n}\n// end");
        Assert.Equal("1", root?.GetObject("a")?.GetString("b"));
    }

    [Fact]
    public void A_duplicate_key_keeps_the_first_value()
    {
        // Why: a later entry must not silently replace what was read first (deterministic result for a hand-edited file).
        Assert.Equal("first", Vdf.TryParse("\"k\" \"first\" \"k\" \"second\"")?.GetString("k"));
    }

    [Fact]
    public void An_empty_file_is_an_empty_object()
    {
        Assert.NotNull(Vdf.TryParse(string.Empty));
    }

    [Fact]
    public void Nesting_up_to_the_limit_parses_and_one_more_does_not()
    {
        Assert.NotNull(Vdf.TryParse(Nested(Vdf.MaxDepth)));
        Assert.Null(Vdf.TryParse(Nested(Vdf.MaxDepth + 1)));
    }

    [Theory]
    [InlineData("\"k\" \"unterminated")]
    [InlineData("\"k\" {")]
    [InlineData("\"k\" { \"a\" \"b\"")]
    [InlineData("}")]
    [InlineData("\"k\" { } }")]
    [InlineData("\"k\"")]
    [InlineData("{ \"a\" \"b\" }")]
    [InlineData("\"k\" \"v\" {")]
    [InlineData("k v")]
    [InlineData("\"k\" / \"v\"")]
    [InlineData("\"k\" \"line\nbreak\"")]
    [InlineData("\"k\" \"back\\")]
    public void Malformed_input_is_not_parsed(string text)
    {
        Assert.Null(Vdf.TryParse(text));
    }

    [Fact]
    public async Task A_deeply_nested_file_is_refused_without_a_stack_overflow()
    {
        var bomb = string.Concat(Enumerable.Repeat("\"a\" {", 200_000));
        Assert.Null(await Bounded(bomb));
    }

    [Fact]
    public async Task A_huge_key_or_value_is_refused()
    {
        Assert.Null(await Bounded($"\"{new string('k', Vdf.MaxKeyLength + 1)}\" \"v\""));
        Assert.Null(await Bounded($"\"k\" \"{new string('v', Vdf.MaxValueLength + 1)}\""));
        Assert.NotNull(await Bounded($"\"{new string('k', Vdf.MaxKeyLength)}\" \"{new string('v', Vdf.MaxValueLength)}\""));
    }

    [Fact]
    public void Too_many_entries_are_refused()
    {
        var many = string.Concat(Enumerable.Range(0, Vdf.MaxNodes + 1).Select(i => $"\"{i}\" \"x\"\n"));
        Assert.Null(Vdf.TryParse(many));
    }

    [Fact]
    public async Task Binary_garbage_is_not_parsed_and_does_not_throw()
    {
        var random = new Random(1234);
        for (var round = 0; round < 200; round++)
        {
            var chars = new char[random.Next(1, 512)];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)random.Next(0, 256);
            }

            var text = new string(chars);
            var ex = await Record.ExceptionAsync(() => Bounded(text));
            Assert.Null(ex);
        }
    }

    // Why: a file over the cap must be ignored as a whole. Cut at the cap it would still be valid KeyValues text (every line is
    // complete), so a reader that merely truncated would return partial data as if it were the file.
    [Fact]
    public void A_file_over_the_size_cap_is_ignored_not_truncated()
    {
        using var temp = new TempRoot();
        var line = "\"a\" \"b\"\n";
        var file = temp.WriteFile("big.vdf", string.Concat(Enumerable.Repeat(line, 100)));
        Assert.Null(Vdf.TryReadFile(file, maxBytes: line.Length * 50));
        Assert.NotNull(Vdf.TryReadFile(file, maxBytes: line.Length * 100));
        Assert.Null(Vdf.TryReadFile(file, maxBytes: line.Length * 100 - 1));
    }

    [Fact]
    public void A_utf8_bom_is_ignored()
    {
        using var temp = new TempRoot();
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(RealManifest)).ToArray();
        var file = temp.WriteBytes("bom.acf", bytes);
        Assert.Equal("227300", Vdf.TryReadFile(file, 1 << 16)?.GetObject("AppState")?.GetString("appid"));
    }

    [Fact]
    public void Bytes_that_are_not_utf8_are_not_parsed()
    {
        using var temp = new TempRoot();
        var file = temp.WriteBytes("garbage.acf", [0xFF, 0xFE, 0x00, 0x80, 0xC0, 0xAF, 0x22, 0x61, 0x22]);
        Assert.Null(Vdf.TryReadFile(file, 1 << 16));
    }

    [Fact]
    public void A_missing_file_or_a_folder_is_not_parsed()
    {
        using var temp = new TempRoot();
        Assert.Null(Vdf.TryReadFile(temp.Combine("nope.vdf"), 1 << 16));
        Assert.Null(Vdf.TryReadFile(temp.MakeDir("folder"), 1 << 16));
    }

    [Fact]
    public void A_file_another_process_has_open_for_writing_can_still_be_read()
    {
        // Why: Steam rewrites these files while it runs; a share-mode clash would hide the game.
        using var temp = new TempRoot();
        var file = temp.WriteFile("open.vdf", "\"a\" \"b\"");
        using var writer = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.Equal("b", Vdf.TryReadFile(file, 1 << 16)?.GetString("a"));
    }

    // A hang would fail here instead of stalling the run: parsing must finish in bounded time.
    private static Task<VdfObject?> Bounded(string text) =>
        Task.Run(() => Vdf.TryParse(text), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

    private static string Nested(int depth) => string.Concat(Enumerable.Repeat("\"a\" { ", depth)) + string.Concat(Enumerable.Repeat("} ", depth));
}
