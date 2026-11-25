using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ButtonBox.Output.Scs;

/// <summary>
/// The final path of an existing file or folder, with every junction, symbolic link and <c>subst</c> drive resolved. Windows reports
/// a running process's image by its final path (a game started from a Steam library behind a junction reports the junction's
/// target), so every path that is compared with a process image must be resolved the same way (decisions §9 server check, §12).
/// </summary>
public static class FinalPath
{
    private const uint FileShareAll = 0x1 | 0x2 | 0x4; // read, write, delete
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000; // also opens folders
    private const uint VolumeNameDos = 0x0;

    /// <summary>The final path without a <c>\\?\</c> prefix, or null when the path does not exist or cannot be opened.</summary>
    public static string? Resolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        // Zero access: only the handle's name is read, so a file the game has open or loaded can still be resolved.
        using var handle = CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, VolumeNameDos);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, VolumeNameDos);
        }

        if (length == 0 || length >= buffer.Capacity)
        {
            return null;
        }

        var final = buffer.ToString(0, (int)length);
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + final[8..];
        }

        return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
