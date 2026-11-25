using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ButtonBox.Output.Scs.Install;

/// <summary>Finds out whether a game executable is running, by process image (same comparison as the pipe server check).</summary>
internal static class GameProcess
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>True when a process runs the image <paramref name="exePath"/>; both sides are compared as final paths.</summary>
    public static bool IsRunning(string exePath)
    {
        var target = Canonical(exePath);
        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath));
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        try
        {
            foreach (var process in candidates)
            {
                var image = ImagePath(process.Id);
                if (image is not null && string.Equals(Canonical(image), target, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            foreach (var process in candidates)
            {
                process.Dispose();
            }
        }
    }

    private static string Canonical(string path) => Path.GetFullPath(FinalPath.Resolve(path) ?? path).TrimEnd('\\');

    private static string? ImagePath(int pid)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle.IsInvalid)
        {
            return null;
        }

        var name = new StringBuilder(32768);
        var size = (uint)name.Capacity;
        return QueryFullProcessImageNameW(handle, 0, name, ref size) ? name.ToString(0, (int)size) : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
}
