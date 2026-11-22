using ButtonBox.Core;

namespace ButtonBox.Security;

/// <summary>
/// PowerShell commands for the Windows Firewall rules (decisions §9): Private profile only, local subnet only.
/// Pure string building; nothing is executed here.
/// </summary>
public static class FirewallRules
{
    public const string Group = ProductInfo.Name;

    /// <summary>TCP 443 plus the pairing ports 80 and 8080.</summary>
    public static readonly IReadOnlyList<int> TcpPorts = [443, 80, 8080];

    /// <summary>mDNS.</summary>
    public const int UdpPort = 5353;

    /// <summary>The commands that add the TCP rule and the UDP rule. <paramref name="programPath"/> optionally restricts them to one executable.</summary>
    public static IReadOnlyList<string> BuildAddCommands(string? programPath = null)
    {
        var program = programPath is null ? string.Empty : $" -Program {Quote(programPath)}";
        var tcp = string.Join(',', TcpPorts);
        return
        [
            $"New-NetFirewallRule -DisplayName {Quote(Group + " (TCP)")} -Group {Quote(Group)} -Direction Inbound -Action Allow -Protocol TCP -LocalPort {tcp} -Profile Private -RemoteAddress LocalSubnet{program}",
            $"New-NetFirewallRule -DisplayName {Quote(Group + " (mDNS)")} -Group {Quote(Group)} -Direction Inbound -Action Allow -Protocol UDP -LocalPort {UdpPort} -Profile Private -RemoteAddress LocalSubnet{program}",
        ];
    }

    public static string BuildRemoveCommand() => $"Remove-NetFirewallRule -Group {Quote(Group)}";

    // PowerShell treats the typographic single quotes U+2018..U+201B like ', so all of them are doubled.
    private static string Quote(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                throw new ArgumentException("The value must not contain control characters.", nameof(value));
            }
        }

        var escaped = new System.Text.StringBuilder(value.Length + 2).Append('\'');
        foreach (var c in value)
        {
            escaped.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
            {
                escaped.Append(c);
            }
        }

        return escaped.Append('\'').ToString();
    }
}
