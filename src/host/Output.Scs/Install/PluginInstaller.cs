using System.Security.Cryptography;
using ButtonBox.Core.Contracts;

namespace ButtonBox.Output.Scs.Install;

/// <summary>Result of <see cref="PluginInstaller.Install"/>.</summary>
public enum PluginInstallOutcome
{
    Installed,
    GameNotFound,

    /// <summary>This build ships no plug-in DLL.</summary>
    NotIncluded,

    /// <summary>The game runs (or holds the DLL open): the user must close it first.</summary>
    GameRunning,

    /// <summary>Anything else; <see cref="PluginInstallResult.Reason"/> says briefly why.</summary>
    Failed,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Reason">A short, fixed text for <see cref="PluginInstallOutcome.Failed"/>; no stack, no file contents.</param>
public sealed record PluginInstallResult(PluginInstallOutcome Outcome, string? Reason = null);

/// <param name="State">Per-game state shown in Settings.</param>
/// <param name="InstallDir">The game folder, or null when the game is not found.</param>
public sealed record PluginStatus(ScsPluginState State, string? InstallDir);

/// <summary>
/// Installs or updates the plug-in DLL in a game's <c>bin\win_x64\plugins</c> folder (decisions §12). It only ever creates, replaces
/// or deletes <c>button_box_scs.dll</c> and its own temporary file there, and creates the <c>plugins</c> folder when missing.
/// </summary>
public sealed class PluginInstaller
{
    public const string DllName = "button_box_scs.dll";

    private const long MaxDllBytes = 32 * 1024 * 1024;

    private readonly IScsGameLocator _locator;
    private readonly string? _shippedDllPath;
    private readonly Func<string, bool> _isRunning;
    private readonly object _gate = new();

    public PluginInstaller(IScsGameLocator locator, string? shippedDllPath)
        : this(locator, shippedDllPath, GameProcess.IsRunning)
    {
    }

    internal PluginInstaller(IScsGameLocator locator, string? shippedDllPath, Func<string, bool> isRunning)
    {
        ArgumentNullException.ThrowIfNull(locator);
        _locator = locator;
        _shippedDllPath = shippedDllPath;
        _isRunning = isRunning;
    }

    /// <summary>True when the shipped DLL exists; otherwise Settings says "plugin not included in this build".</summary>
    public bool Included => _shippedDllPath is not null && File.Exists(_shippedDllPath);

    /// <summary>Compares the installed DLL's SHA-256 with the shipped one. Reads only.</summary>
    public PluginStatus Status(ScsGame game)
    {
        var install = _locator.Find().FirstOrDefault(g => g.Game == game);
        if (install is null)
        {
            return new PluginStatus(ScsPluginState.GameNotFound, null);
        }

        var target = Path.Combine(install.PluginDir, DllName);
        if (!File.Exists(target))
        {
            return new PluginStatus(ScsPluginState.NotInstalled, install.InstallDir);
        }

        var installed = HashFile(target);
        var shipped = ReadShipped(out _);
        var state = installed is not null && shipped is not null && installed.AsSpan().SequenceEqual(SHA256.HashData(shipped))
            ? ScsPluginState.UpToDate
            : ScsPluginState.UpdateAvailable;
        return new PluginStatus(state, install.InstallDir);
    }

    /// <summary>Copies the shipped DLL into the game's plug-in folder. Called only when the user clicks Install or Update.</summary>
    public PluginInstallResult Install(ScsGame game)
    {
        lock (_gate)
        {
            try
            {
                return InstallLocked(game);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                return Failed("unexpected file system error");
            }
        }
    }

    private PluginInstallResult InstallLocked(ScsGame game)
    {
        if (!Included)
        {
            return new PluginInstallResult(PluginInstallOutcome.NotIncluded);
        }

        var shipped = ReadShipped(out var shippedError);
        if (shipped is null)
        {
            return Failed(shippedError!);
        }

        var shippedHash = SHA256.HashData(shipped);
        var install = _locator.Find().FirstOrDefault(g => g.Game == game);
        if (install is null)
        {
            return new PluginInstallResult(PluginInstallOutcome.GameNotFound);
        }

        if (_isRunning(install.ExePath))
        {
            return new PluginInstallResult(PluginInstallOutcome.GameRunning);
        }

        var pluginDir = install.PluginDir;
        var target = Path.Combine(pluginDir, DllName);

        // Only the plugins folder itself is created: its parent (bin\win_x64, where the game executable is) must exist already.
        if (!Directory.Exists(pluginDir))
        {
            if (File.Exists(pluginDir) || !Directory.Exists(Path.GetDirectoryName(pluginDir)))
            {
                return Failed("the plug-in folder cannot be created");
            }

            Directory.CreateDirectory(pluginDir);
        }

        // A link here would let the write land outside the game folder. Links higher up (a Steam library behind a junction) are fine.
        if (IsReparsePoint(pluginDir) || !Directory.Exists(pluginDir))
        {
            return Failed("the plug-in folder is a link");
        }

        var targetAttributes = AttributesOrNull(target);
        if (targetAttributes is { } attributes)
        {
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return Failed("the plug-in file is a link");
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                return Failed("the plug-in file name is taken by a folder");
            }
        }

        var temp = Path.Combine(pluginDir, $"{DllName}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(8))}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(shipped);
                stream.Flush(flushToDisk: true);
            }

            if (HashFile(temp) is not { } written || !written.AsSpan().SequenceEqual(shippedHash))
            {
                return Failed("the copy did not match the original");
            }

            try
            {
                File.Move(temp, target, overwrite: true);
            }
            catch (Exception e) when (IsLockedByGame(e))
            {
                return new PluginInstallResult(PluginInstallOutcome.GameRunning);
            }

            return HashFile(target) is { } final && final.AsSpan().SequenceEqual(shippedHash)
                ? new PluginInstallResult(PluginInstallOutcome.Installed)
                : Failed("the installed file does not match the original");
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    // A loaded DLL cannot be replaced: Windows answers access denied (a mapped image) or a sharing violation (an open handle).
    private static bool IsLockedByGame(Exception e) =>
        e is UnauthorizedAccessException || (e is IOException && ((e.HResult & 0xFFFF) is 32 or 33));

    private static PluginInstallResult Failed(string reason) => new(PluginInstallOutcome.Failed, reason);

    private static bool IsReparsePoint(string path) => AttributesOrNull(path) is { } a && a.HasFlag(FileAttributes.ReparsePoint);

    private static FileAttributes? AttributesOrNull(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private byte[]? ReadShipped(out string? error)
    {
        error = null;
        try
        {
            var info = new FileInfo(_shippedDllPath ?? string.Empty);
            if (!info.Exists)
            {
                error = "the plug-in is not included in this build";
                return null;
            }

            if (info.Length > MaxDllBytes)
            {
                error = "the shipped plug-in is unexpectedly large";
                return null;
            }

            return File.ReadAllBytes(info.FullName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = "the shipped plug-in cannot be read";
            return null;
        }
    }

    private static byte[]? HashFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return SHA256.HashData(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done; the leftover is our own uniquely named temporary file.
        }
    }
}
