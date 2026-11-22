using Xunit;

namespace ButtonBox.Core.Tests;

// The device list and the CA key are written with AtomicFile. A partial write would lose every paired device
// or the CA (forcing all devices to re-pair), so the old content must survive until the new one is complete.
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bbs-atomic-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Creates_missing_directory_and_file()
    {
        var path = Path.Combine(_dir, "sub", "devices.json");
        AtomicFile.WriteAllBytes(path, "first"u8);
        Assert.Equal("first", File.ReadAllText(path));
    }

    [Fact]
    public void Replaces_existing_content_and_leaves_no_temp_files()
    {
        var path = Path.Combine(_dir, "devices.json");
        AtomicFile.WriteAllBytes(path, "a much longer first version"u8);
        AtomicFile.WriteAllBytes(path, "second"u8);
        Assert.Equal("second", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    // If the replace step fails, the existing file is untouched and the temp file is cleaned up.
    [Fact]
    public void Failed_replace_keeps_old_content()
    {
        var path = Path.Combine(_dir, "devices.json");
        AtomicFile.WriteAllBytes(path, "old"u8);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Windows reports the locked target as access denied.
            var error = Record.Exception(() => AtomicFile.WriteAllBytes(path, "new"u8));
            Assert.True(error is IOException or UnauthorizedAccessException, $"unexpected: {error}");
        }
        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }
}
