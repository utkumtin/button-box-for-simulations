using Xunit;
using ButtonBox.Security;
using Microsoft.Extensions.Time.Testing;

namespace ButtonBox.Security.Tests;

public class PairingCodeTests
{
    private static string Wrong(PairingCode code) => code.Code == "000000" ? "000001" : "000000";

    // Why: a code that can be replayed lets anyone who saw the screen pair a second phone.
    [Fact]
    public void Success_once_then_AlreadyUsed()
    {
        var code = PairingCode.Create(new FakeTimeProvider());
        Assert.Equal(PairingCodeResult.Success, code.TryUse(code.Code));
        Assert.Equal(PairingCodeResult.AlreadyUsed, code.TryUse(code.Code));
    }

    // Why: the two-minute window limits how long a shoulder-surfed code stays useful; the boundary must be exact.
    [Fact]
    public void Expires_exactly_at_two_minutes_and_does_not_consume_an_attempt()
    {
        var time = new FakeTimeProvider();
        var code = PairingCode.Create(time);
        time.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(PairingCodeResult.Wrong, code.TryUse(Wrong(code)));
        Assert.Equal(4, code.AttemptsLeft);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PairingCodeResult.Expired, code.TryUse(code.Code));
        Assert.Equal(PairingCodeResult.Expired, code.TryUse(Wrong(code)));
        Assert.Equal(4, code.AttemptsLeft);
    }

    // Why: lockout is the only defence against brute-forcing 10^6 codes; after it even the right code must fail.
    [Fact]
    public void Five_wrong_attempts_burn_the_code_even_for_the_right_one()
    {
        var code = PairingCode.Create(new FakeTimeProvider());
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(PairingCodeResult.Wrong, code.TryUse(Wrong(code)));
        }

        Assert.Equal(0, code.AttemptsLeft);
        Assert.Equal(PairingCodeResult.Burned, code.TryUse(code.Code));
    }

    // Why: a prefix, padded or empty guess must not succeed or skip the attempt counter.
    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData(null)]
    public void Wrong_length_counts_as_wrong(string? candidate)
    {
        var code = PairingCode.Create(new FakeTimeProvider());
        Assert.Equal(PairingCodeResult.Wrong, code.TryUse(candidate));
        Assert.Equal(4, code.AttemptsLeft);
    }

    // Why: the right code with extra characters is still not the code.
    [Fact]
    public void Right_code_with_suffix_is_wrong()
    {
        var code = PairingCode.Create(new FakeTimeProvider());
        Assert.Equal(PairingCodeResult.Wrong, code.TryUse(code.Code + "0"));
        Assert.Equal(PairingCodeResult.Success, code.TryUse(code.Code));
    }

    // Why: a code shown as "4821" instead of "004821" could never be typed correctly.
    [Fact]
    public void Codes_are_always_six_digits_including_leading_zeros()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 3000; i++)
        {
            var code = PairingCode.Create(new FakeTimeProvider()).Code;
            Assert.Matches(@"\A[0-9]{6}\z", code);
            seen.Add(code);
        }

        Assert.True(seen.Count > 2500, "codes must be random, not constant");
        Assert.Contains(seen, c => c[0] == '0');
    }

    // Why: the phone endpoint is hit concurrently; the code must be used by exactly one caller.
    [Fact]
    public void Concurrent_use_succeeds_exactly_once()
    {
        var code = PairingCode.Create(new FakeTimeProvider());
        var results = new PairingCodeResult[32];
        Parallel.For(0, results.Length, i => results[i] = code.TryUse(code.Code));
        Assert.Equal(1, results.Count(r => r == PairingCodeResult.Success));
    }
}

public class EditorSecretTests
{
    // Why: the secret guards the editor API; it must be 256-bit, URL-safe and unique per launch.
    [Fact]
    public void Value_is_43_chars_base64url_and_unique()
    {
        var a = EditorSecret.Create();
        var b = EditorSecret.Create();
        Assert.True(ContractValidator.IsValidToken(a.Value));
        Assert.NotEqual(a.Value, b.Value);
    }

    // Why: only the exact secret may open the editor API.
    [Fact]
    public void Matches_only_the_exact_value()
    {
        var secret = EditorSecret.Create();
        Assert.True(secret.Matches(secret.Value));
        Assert.False(secret.Matches(null));
        Assert.False(secret.Matches(""));
        Assert.False(secret.Matches(secret.Value + "a"));
        Assert.False(secret.Matches(secret.Value[..^1]));
        Assert.False(secret.Matches(secret.Value[..^1] + (secret.Value[^1] == 'A' ? 'B' : 'A')));
        Assert.False(secret.Matches(EditorSecret.Create().Value));
    }
}

public class FirewallRulesTests
{
    private const string Tcp = "New-NetFirewallRule -DisplayName 'button-box-for-simulations (TCP)' -Group 'button-box-for-simulations' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 443,80,8080 -Profile Private -RemoteAddress LocalSubnet";
    private const string Udp = "New-NetFirewallRule -DisplayName 'button-box-for-simulations (mDNS)' -Group 'button-box-for-simulations' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 5353 -Profile Private -RemoteAddress LocalSubnet";

    // Why: the scope (Private profile, local subnet) is a security decision; any change must be deliberate.
    [Fact]
    public void Add_commands_are_exact_without_program()
    {
        Assert.Equal([Tcp, Udp], FirewallRules.BuildAddCommands());
    }

    // Why: a program path with a quote must not break out of the PowerShell string (command injection).
    [Fact]
    public void Program_path_is_single_quoted_and_quotes_are_doubled()
    {
        var commands = FirewallRules.BuildAddCommands(@"C:\Users\O'Neil\app.exe");
        Assert.Equal([Tcp + @" -Program 'C:\Users\O''Neil\app.exe'", Udp + @" -Program 'C:\Users\O''Neil\app.exe'"], commands);
    }

    // Why: PowerShell treats typographic single quotes as quotes too.
    [Fact]
    public void Typographic_quotes_are_doubled()
    {
        var commands = FirewallRules.BuildAddCommands("a\u2019b");
        Assert.EndsWith(" -Program 'a\u2019\u2019b'", commands[0]);
    }

    // Why: a newline would start a second command.
    [Fact]
    public void Control_characters_in_the_path_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => FirewallRules.BuildAddCommands("a\nb"));
    }

    // Why: uninstall must remove exactly our rules and nothing else.
    [Fact]
    public void Remove_command_targets_only_our_group()
    {
        Assert.Equal("Remove-NetFirewallRule -Group 'button-box-for-simulations'", FirewallRules.BuildRemoveCommand());
    }
}
