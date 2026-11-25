using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ButtonBox.Output.Scs.Pipe;

/// <summary>Why the host refused to trust a pipe server (decisions §9). Shown in the Settings panel and logs.</summary>
public enum ServerCheckFailure
{
    /// <summary>The server passed every check.</summary>
    None,

    /// <summary>The pipe's DACL does not let the current user connect.</summary>
    PipeAccessDenied,

    /// <summary>The serving process or its image path could not be determined.</summary>
    ServerProcessUnknown,

    /// <summary>The serving process is not the game executable of a detected install.</summary>
    ImageNotAllowed,

    /// <summary>The pipe's owner is not the current user.</summary>
    OwnerNotCurrentUser,

    /// <summary>The pipe has no DACL, which grants access to everyone.</summary>
    DaclMissing,

    /// <summary>The DACL grants access to an account other than the current user.</summary>
    DaclGrantsOthers,

    /// <summary>The pipe's security descriptor could not be read.</summary>
    SecurityUnreadable,
}

/// <summary>Outcome of <see cref="PipeServerCheck.Check"/>. <paramref name="Detail"/> holds paths and SIDs only, never secrets.</summary>
public sealed record ServerCheckResult(ServerCheckFailure Failure, string? Detail = null)
{
    public bool Trusted => Failure == ServerCheckFailure.None;
}

/// <summary>
/// The host's check of a connected pipe server (decisions §9): the serving process is the game executable of a detected
/// install, the pipe owner is the current user and the DACL grants access to no other account. It runs on every connect,
/// before the first read or write.
/// </summary>
public static class PipeServerCheck
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>Checks the server end of a connected client pipe.</summary>
    /// <param name="pipe">The connected client stream; nothing is read from or written to it.</param>
    /// <param name="allowedExePaths">Full paths of the game executables that may serve the pipe.</param>
    public static ServerCheckResult Check(NamedPipeClientStream pipe, IEnumerable<string> allowedExePaths)
    {
        var image = ServerImagePath(pipe.SafePipeHandle, out var detail);
        if (image is null)
        {
            return new ServerCheckResult(ServerCheckFailure.ServerProcessUnknown, detail);
        }

        // Both sides as final paths: Windows reports the image with junctions resolved (a Steam library behind a junction), so
        // comparing the text of the detected path alone would reject the real game.
        var normalized = Canonical(image);
        if (!allowedExePaths.Any(allowed => string.Equals(Canonical(allowed), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return new ServerCheckResult(ServerCheckFailure.ImageNotAllowed, $"server image '{image}' is not a detected game executable");
        }

        RawSecurityDescriptor descriptor;
        try
        {
            descriptor = new RawSecurityDescriptor(pipe.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException)
        {
            return new ServerCheckResult(ServerCheckFailure.SecurityUnreadable, e.GetType().Name);
        }

        return EvaluateSecurity(descriptor, WindowsIdentity.GetCurrent().User!);
    }

    /// <summary>Owner and DACL rules for a pipe's security descriptor. A NULL DACL, a foreign allow ACE or a foreign owner fails.</summary>
    internal static ServerCheckResult EvaluateSecurity(RawSecurityDescriptor descriptor, SecurityIdentifier currentUser)
    {
        if (descriptor.Owner != currentUser)
        {
            return new ServerCheckResult(ServerCheckFailure.OwnerNotCurrentUser, $"pipe owner is {descriptor.Owner?.Value ?? "(none)"}");
        }

        if (descriptor.DiscretionaryAcl is null)
        {
            return new ServerCheckResult(ServerCheckFailure.DaclMissing, "pipe has no DACL");
        }

        var foreign = new List<string>();
        foreach (var ace in descriptor.DiscretionaryAcl)
        {
            // Deny ACEs only restrict; every other ACE type (allow, callback, object) must be for the current user.
            if (ace is QualifiedAce { AceQualifier: AceQualifier.AccessDenied })
            {
                continue;
            }

            if (ace is QualifiedAce qualified && qualified.SecurityIdentifier == currentUser)
            {
                continue;
            }

            foreign.Add(ace is QualifiedAce other ? other.SecurityIdentifier?.Value ?? "?" : $"ace-type:{ace.AceType}");
        }

        return foreign.Count > 0
            ? new ServerCheckResult(ServerCheckFailure.DaclGrantsOthers, $"pipe DACL grants access to {string.Join(", ", foreign)}")
            : new ServerCheckResult(ServerCheckFailure.None);
    }

    /// <summary>Full path, with a <c>\\?\</c> prefix removed, so two spellings of one file compare equal.</summary>
    internal static string NormalizePath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[8..];
        }
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        return Path.GetFullPath(path).TrimEnd('\\');
    }

    // A path that exists is compared by its final path; one that does not exist (no such install) by its normalized text.
    private static string Canonical(string path) => NormalizePath(FinalPath.Resolve(path) ?? path);

    private static string? ServerImagePath(SafePipeHandle pipe, out string? detail)
    {
        detail = null;
        if (!GetNamedPipeServerProcessId(pipe, out var pid))
        {
            detail = $"GetNamedPipeServerProcessId failed (win32 {Marshal.GetLastWin32Error()})";
            return null;
        }

        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid)
        {
            detail = $"cannot open server process {pid} (win32 {Marshal.GetLastWin32Error()})";
            return null;
        }

        var name = new StringBuilder(32768);
        var size = (uint)name.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, name, ref size))
        {
            detail = $"cannot read the image of server process {pid} (win32 {Marshal.GetLastWin32Error()})";
            return null;
        }

        return name.ToString(0, (int)size);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
}
