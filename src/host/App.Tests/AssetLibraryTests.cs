using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ButtonBox.App.Lan;
using ButtonBox.App.Profiles;
using ButtonBox.Core.Contracts;
using ButtonBox.Security;
using Xunit;

namespace ButtonBox.App.Tests;

// Why: the library is what the LAN route serves to every phone without a token. Only bytes whose name, hash and header all agree
// may ever leave it, and a file the user put there by hand is never touched.
public sealed class AssetLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bb-assets-" + Guid.NewGuid().ToString("N"));

    public AssetLibraryTests()
    {
        Directory.CreateDirectory(Dir);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir => Path.Combine(_root, "assets");

    private static byte[] Png => DemoContent.ReadSeed().Images[0];

    private static byte[] Svg => Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"600\" height=\"600\"></svg>");

    // Headers plus dummy payload: the host only reads headers (same approach as the ImageCheck tests).
    internal static byte[] Webp(int size = 512)
    {
        var bits = (uint)(size - 1) | ((uint)(size - 1) << 14) | (1u << 28);
        var payload = new byte[9];
        payload[0] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), bits);
        var chunk = new byte[8 + 10];
        "VP8L"u8.CopyTo(chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(chunk, 8); // 9 bytes, padded to 10
        var riff = new byte[12 + chunk.Length];
        "RIFF"u8.CopyTo(riff);
        BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), (uint)(riff.Length - 8));
        "WEBP"u8.CopyTo(riff.AsSpan(8));
        chunk.CopyTo(riff, 12);
        return riff;
    }

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private AssetLibrary Open() => new(Dir);

    [Fact]
    public void A_png_is_stored_unchanged_under_its_own_hash()
    {
        var png = Png;
        var library = Open();

        var result = library.Import(png);

        Assert.Null(result.Problem);
        var asset = result.Asset!;
        Assert.Equal(Hash(png), asset.Id);
        Assert.Equal(AssetType.Png, asset.Type);
        Assert.Equal(1254, asset.Size);
        Assert.Equal(png.Length, asset.Bytes);
        // "The file is stored unchanged" (decisions §7): the hash on the wire is the hash of the bytes the user chose.
        Assert.Equal(png, File.ReadAllBytes(Path.Combine(Dir, asset.Id + ".png")));
        Assert.True(library.Contains(asset.Id));
        Assert.Equal([asset], library.List());
    }

    [Fact]
    public void A_webp_is_stored_with_the_webp_extension_and_served_as_webp()
    {
        var webp = Webp();
        var library = Open();

        var asset = library.Import(webp).Asset!;

        Assert.Equal(AssetType.Webp, asset.Type);
        Assert.True(File.Exists(Path.Combine(Dir, asset.Id + ".webp")));
        Assert.True(library.TryGet(asset.Id, out var type, out var bytes));
        Assert.Equal(AssetType.Webp, type);
        Assert.Equal(webp, bytes);
    }

    [Fact]
    public void Importing_the_same_bytes_again_changes_nothing()
    {
        var library = Open();
        var first = library.Import(Png).Asset!;
        var written = File.GetLastWriteTimeUtc(Path.Combine(Dir, first.Id + ".png"));

        var second = library.Import(Png);

        Assert.Equal(first, second.Asset);
        Assert.Single(library.List());
        Assert.Single(Directory.GetFiles(Dir));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(Dir, first.Id + ".png")));
    }

    [Fact]
    public void A_new_library_on_the_same_folder_finds_what_was_imported()
    {
        var id = Open().Import(Png).Asset!.Id;

        var reopened = Open();

        Assert.True(reopened.Contains(id));
        Assert.True(reopened.TryGet(id, out _, out _));
    }

    [Fact]
    public void An_image_that_fails_the_check_is_refused_with_a_sentence_and_nothing_is_written()
    {
        var library = Open();

        var svg = library.Import(Svg);
        var empty = library.Import([]);
        var tooBig = library.Import(new byte[ImageCheck.MaxBytes + 1]);
        var text = library.Import("not an image"u8);

        Assert.All(new[] { svg, empty, tooBig, text }, r =>
        {
            Assert.Null(r.Asset);
            Assert.False(string.IsNullOrWhiteSpace(r.Problem));
        });
        Assert.Contains("SVG", svg.Problem, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Dir));
        Assert.Empty(library.List());
    }

    [Fact]
    public void Files_whose_name_hash_or_header_do_not_check_out_are_ignored_and_never_deleted()
    {
        // Each of these is a way for a wrong file to end up under /assets: a renamed file, an SVG that is named by its own
        // correct hash, a name that is not lowercase hex, a type that differs from the extension, a file that is not an image.
        var png = Png;
        var other = Webp();
        var files = new Dictionary<string, byte[]>
        {
            [new string('a', 64) + ".png"] = png, // the name is not the hash
            [Hash(Svg) + ".png"] = Svg, // the hash is right, the header is not an image
            [Hash(png).ToUpperInvariant() + ".png"] = png, // not lowercase
            [Hash(other) + ".png"] = other, // WebP content with a .png name
            [Hash(png) + ".webp"] = png, // PNG content with a .webp name
            ["notes.txt"] = "hello"u8.ToArray(),
            [Hash(png)[..63] + ".png"] = png, // 63 characters
        };
        foreach (var (name, data) in files)
        {
            File.WriteAllBytes(Path.Combine(Dir, name), data);
        }

        var library = Open();

        Assert.Empty(library.List());
        foreach (var name in files.Keys)
        {
            Assert.False(library.TryGet(Path.GetFileNameWithoutExtension(name), out _, out _));
            Assert.True(File.Exists(Path.Combine(Dir, name)), name + " must still exist");
        }
    }

    [Fact]
    public void A_file_replaced_after_the_scan_is_not_served()
    {
        // The index is built once; the bytes are verified again on every read, so swapping the file under a running app
        // cannot make the route serve something that the id does not describe.
        var library = Open();
        var id = library.Import(Png).Asset!.Id;
        File.WriteAllBytes(Path.Combine(Dir, id + ".png"), Svg);

        Assert.False(library.TryGet(id, out _, out var bytes));
        Assert.Empty(bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../assets/x")]
    [InlineData("A")]
    public void Anything_but_a_held_lowercase_hash_is_not_found(string id)
    {
        var library = Open();
        library.Import(Png);

        Assert.False(library.TryGet(id, out _, out _));
        Assert.False(library.TryGet(null, out _, out _));
        Assert.False(library.Contains(id));
        Assert.False(library.Delete(id));
    }

    [Fact]
    public void Delete_removes_the_file_and_the_entry_and_leaves_ignored_files_alone()
    {
        var library = Open();
        var id = library.Import(Png).Asset!.Id;
        var stray = Path.Combine(Dir, "notes.txt");
        File.WriteAllText(stray, "keep");

        Assert.True(library.Delete(id));

        Assert.False(library.Contains(id));
        Assert.False(File.Exists(Path.Combine(Dir, id + ".png")));
        Assert.True(File.Exists(stray));
        Assert.False(library.Delete(id));
    }

    [Fact]
    public void A_damaged_file_that_already_has_the_name_of_an_image_is_never_replaced_by_an_import()
    {
        // The name is the hash of the bytes being imported, so a file under it that does not verify is somebody's file or
        // a damaged one: the app does not overwrite it. It reports the conflict instead.
        var png = Png;
        var path = Path.Combine(Dir, Hash(png) + ".png");
        File.WriteAllBytes(path, [1, 2, 3]);
        var library = Open();

        var result = library.Import(png);

        Assert.Null(result.Asset);
        Assert.NotNull(result.Problem);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
    }

    [Fact]
    public void Many_threads_importing_the_same_image_leave_one_file()
    {
        var library = Open();
        var png = Png;

        Parallel.For(0, 16, _ => Assert.Null(library.Import(png).Problem));

        Assert.Single(library.List());
        Assert.Single(Directory.GetFiles(Dir));
    }

    [Fact]
    public void An_unchanged_file_is_served_without_hashing_it_again()
    {
        // /assets is unauthenticated: a request must not cost a SHA-256 over up to 4 MiB each time (CPU amplification).
        var library = Open();
        var id = library.Import(Png).Asset!.Id;
        var hashed = library.HashCount;

        for (var i = 0; i < 5; i++)
        {
            Assert.True(library.TryGet(id, out _, out var bytes));
            Assert.Equal(Png, bytes);
        }

        Assert.Equal(hashed, library.HashCount);
    }

    [Fact]
    public void A_served_file_whose_bytes_change_on_disk_is_dropped_and_no_longer_served()
    {
        var library = Open();
        var id = library.Import(Png).Asset!.Id;
        var path = Path.Combine(Dir, id + ".png");
        var altered = Png;
        altered[^20] ^= 0xFF; // same length, other bytes: only the time stamp tells
        File.WriteAllBytes(path, altered);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        Assert.False(library.TryGet(id, out _, out _));
        Assert.False(library.Contains(id));
        Assert.True(File.Exists(path)); // never deleted
    }

    [Fact]
    public void A_file_that_was_only_touched_is_verified_once_and_served_again()
    {
        var library = Open();
        var id = library.Import(Png).Asset!.Id;
        File.SetLastWriteTimeUtc(Path.Combine(Dir, id + ".png"), DateTime.UtcNow.AddMinutes(5));
        var hashed = library.HashCount;

        Assert.True(library.TryGet(id, out _, out _));
        Assert.True(library.TryGet(id, out _, out _));

        Assert.Equal(hashed + 1, library.HashCount);
    }
}
