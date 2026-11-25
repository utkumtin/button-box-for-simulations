using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ButtonBox.Output.Scs.Pipe;
using Xunit;

namespace ButtonBox.Output.Scs.Tests.Pipe;

// Why: the host sends key presses to whatever serves the pipe and believes its telemetry. Any local process could create a pipe
// with the well-known name first; only the game executable of a detected install, running as the current user, with a
// user-only pipe, may be trusted (decisions §9).
public class PipeServerCheckTests
{
    private static readonly string OwnImage = Environment.ProcessPath!;

    private static SecurityIdentifier Me => WindowsIdentity.GetCurrent().User!;

    [Fact]
    public async Task Accepts_the_game_image_with_a_user_only_pipe()
    {
        var result = await CheckAsync(FakePlugin.UserOnly(), [OwnImage]);
        Assert.Equal(ServerCheckFailure.None, result.Failure);
    }

    [Fact]
    public async Task Rejects_a_server_whose_image_is_not_an_allowed_game_executable()
    {
        var result = await CheckAsync(FakePlugin.UserOnly(), [@"C:\Games\Euro Truck Simulator 2\bin\win_x64\eurotrucks2.exe"]);
        Assert.Equal(ServerCheckFailure.ImageNotAllowed, result.Failure);
    }

    [Fact]
    public async Task Rejects_when_no_game_is_installed()
    {
        var result = await CheckAsync(FakePlugin.UserOnly(), []);
        Assert.Equal(ServerCheckFailure.ImageNotAllowed, result.Failure);
    }

    [Fact]
    public async Task Compares_the_image_path_after_normalization_and_ignoring_case()
    {
        var spellings = new[]
        {
            @"\\?\" + OwnImage.ToUpperInvariant(),
            OwnImage.Replace('\\', '/'),
            Path.Combine(Path.GetDirectoryName(OwnImage)!, "sub", "..", Path.GetFileName(OwnImage)),
        };
        foreach (var spelling in spellings)
        {
            var result = await CheckAsync(FakePlugin.UserOnly(), [spelling]);
            Assert.True(result.Trusted, spelling);
        }
    }

    // Why: a Steam library moved behind a junction is a normal setup. Windows reports the running image by its final path (checked on
    // the user's PC, 2026-11-25), so the detected path must be resolved too, or the real game would be rejected on every connect.
    [Fact]
    public async Task Accepts_the_game_when_its_detected_path_goes_through_a_junction()
    {
        var root = Directory.CreateTempSubdirectory("bbfs-junction-").FullName;
        var link = Path.Combine(root, "library");
        try
        {
            using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, Path.GetDirectoryName(OwnImage)! },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            })!)
            {
                await mklink.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, mklink.ExitCode);
            }

            var throughLink = Path.Combine(link, Path.GetFileName(OwnImage));
            Assert.True(File.Exists(throughLink));
            var result = await CheckAsync(FakePlugin.UserOnly(), [throughLink]);
            Assert.True(result.Trusted, result.Detail);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link); // removes the junction only, never its target
            }

            Directory.Delete(root);
        }
    }

    [Fact]
    public async Task Does_not_accept_another_file_in_the_same_folder()
    {
        var sibling = Path.Combine(Path.GetDirectoryName(OwnImage)!, "other_" + Path.GetFileName(OwnImage));
        var result = await CheckAsync(FakePlugin.UserOnly(), [sibling]);
        Assert.Equal(ServerCheckFailure.ImageNotAllowed, result.Failure);
    }

    [Theory]
    [InlineData(WellKnownSidType.WorldSid)]
    [InlineData(WellKnownSidType.BuiltinUsersSid)]
    [InlineData(WellKnownSidType.LocalSystemSid)]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid)]
    public async Task Rejects_a_pipe_that_also_grants_access_to_another_account(WellKnownSidType other)
    {
        var security = FakePlugin.UserOnly();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(other, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var result = await CheckAsync(security, [OwnImage]);
        Assert.Equal(ServerCheckFailure.DaclGrantsOthers, result.Failure);
    }

    [Fact]
    public async Task A_deny_ace_for_another_account_is_fine()
    {
        var security = FakePlugin.UserOnly();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        var result = await CheckAsync(security, [OwnImage]);
        Assert.True(result.Trusted, result.Detail);
    }

    [Fact]
    public async Task Rejects_a_pipe_created_without_an_explicit_user_only_dacl()
    {
        var descriptor = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative, Me, null, null, null);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        var security = new PipeSecurity();
        security.SetSecurityDescriptorBinaryForm(bytes);

        // Windows does not allow a real NULL DACL on a pipe: it substitutes the default DACL (SYSTEM, Administrators, ...).
        // Either way the pipe must be rejected; the true NULL DACL case is covered by the Evaluate test below.
        var result = await CheckAsync(security, [OwnImage]);
        Assert.False(result.Trusted);
        Assert.Equal(ServerCheckFailure.DaclGrantsOthers, result.Failure);
    }

    [Fact]
    public void Evaluate_rejects_a_null_dacl_a_foreign_owner_and_foreign_allow_aces_of_every_kind()
    {
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

        var nullDacl = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent, Me, null, null, null);
        Assert.Equal(ServerCheckFailure.DaclMissing, PipeServerCheck.EvaluateSecurity(nullDacl, Me).Failure);

        var foreignOwner = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent, everyone, null, null, UserOnlyAcl());
        Assert.Equal(ServerCheckFailure.OwnerNotCurrentUser, PipeServerCheck.EvaluateSecurity(foreignOwner, Me).Failure);

        var noOwner = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent, null, null, null, UserOnlyAcl());
        Assert.Equal(ServerCheckFailure.OwnerNotCurrentUser, PipeServerCheck.EvaluateSecurity(noOwner, Me).Failure);

        var withEveryone = UserOnlyAcl();
        withEveryone.InsertAce(1, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x1F01FF, everyone, false, null));
        var extra = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent, Me, null, null, withEveryone);
        Assert.Equal(ServerCheckFailure.DaclGrantsOthers, PipeServerCheck.EvaluateSecurity(extra, Me).Failure);

        var userOnly = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent, Me, null, null, UserOnlyAcl());
        Assert.True(PipeServerCheck.EvaluateSecurity(userOnly, Me).Trusted);
    }

    [Fact]
    public async Task Reads_and_writes_nothing()
    {
        // The check only asks the OS about the handle. A server that records every byte must see none.
        using var plugin = new FakePlugin();
        using var client = new NamedPipeClientStream(".", plugin.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000, TestContext.Current.CancellationToken);
        await plugin.WaitForClientAsync();
        await plugin.SendAsync("hello 1 ets2 1.0.0");

        PipeServerCheck.Check(client, [@"C:\nope\game.exe"]);
        PipeServerCheck.Check(client, [OwnImage]);

        // The hello is still unread in the client's buffer: the check consumed nothing.
        var buffer = new byte[64];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = await client.ReadAsync(buffer, timeout.Token);
        Assert.Equal(2 + "hello 1 ets2 1.0.0".Length, read);

        // And the server received nothing from the client.
        client.Dispose();
        Assert.Null(await plugin.ReadPayloadAsync());
    }

    private static RawAcl UserOnlyAcl()
    {
        var acl = new RawAcl(GenericAcl.AclRevision, 1);
        acl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x1F01FF, Me, false, null));
        return acl;
    }

    private static async Task<ServerCheckResult> CheckAsync(PipeSecurity security, string[] allowed)
    {
        using var plugin = new FakePlugin(security: security);
        using var client = new NamedPipeClientStream(".", plugin.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000, TestContext.Current.CancellationToken);
        await plugin.WaitForClientAsync();
        return PipeServerCheck.Check(client, allowed);
    }
}
