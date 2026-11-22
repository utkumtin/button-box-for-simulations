using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ButtonBox.App.Lan;

/// <summary>Finds which process listens on an IPv4 TCP port, to name it in the "port in use" message.</summary>
public static class TcpPortOwner
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const int RowSize = 24; // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, owningPid
    private const uint ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

    /// <summary>The process name (e.g. "nginx", or "System" for kernel-mode listeners like http.sys), or null when unknown.</summary>
    public static string? FindListenerName(int port)
    {
        var pid = FindListenerPid(port);
        if (pid is null)
        {
            return null;
        }

        if (pid == 4)
        {
            return "System";
        }

        try
        {
            using var process = Process.GetProcessById((int)pid.Value);
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>PID of a TCP listener on <paramref name="port"/>, or null. Runs on error paths of the tray app, so it never throws.</summary>
    public static uint? FindListenerPid(int port)
    {
        try
        {
            return FindListenerPidCore(port);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static uint? FindListenerPidCore(int port)
    {
        var size = 0;
        var buffer = IntPtr.Zero;
        try
        {
            // The table can grow between the size query and the read, so retry a few times.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                if (result == 0 && buffer != IntPtr.Zero)
                {
                    return Find(buffer, port);
                }

                if (result != ErrorInsufficientBuffer)
                {
                    return null;
                }

                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }

                buffer = Marshal.AllocHGlobal(size);
            }

            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static uint? Find(IntPtr table, int port)
    {
        var count = Marshal.ReadInt32(table);
        for (var i = 0; i < count; i++)
        {
            var row = IntPtr.Add(table, 4 + i * RowSize);
            var rawPort = (uint)Marshal.ReadInt32(row, 8);
            // The port is in network byte order in the low 16 bits.
            var localPort = (int)(((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF));
            if (localPort == port)
            {
                return (uint)Marshal.ReadInt32(row, 20);
            }
        }

        return null;
    }
}
