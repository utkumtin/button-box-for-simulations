using ButtonBox.E2EHost;

// Test-only host for the Playwright session (decisions section 13). Never shipped.
// Usage: ButtonBox.E2EHost <client dist dir>
// Stops when stdin closes (the test runner exits), on Ctrl+C and on process exit. Everything it creates (data folder, CNG key) is
// deleted again. It never uses %AppData%, TCP 443/80 or the production key name.
if (args.Length != 1 || !Directory.Exists(args[0]))
{
    await Console.Error.WriteLineAsync("Usage: ButtonBox.E2EHost <client dist dir>");
    return 2;
}

var clientDir = Path.GetFullPath(args[0]);
using var rig = new Rig();
var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => rig.Cleanup();
_ = Task.Run(async () =>
{
    // EOF on stdin = the parent is gone or closed the pipe on purpose.
    while (await Console.In.ReadLineAsync() is not null)
    {
    }

    stop.TrySetResult();
});

try
{
    await rig.StartAsync(clientDir);
    Console.Out.WriteLine(rig.ReadyLine());
    await Console.Out.FlushAsync();
    await stop.Task;
}
catch (Exception e)
{
    // Type only: a message may name a path or key material.
    await Console.Error.WriteLineAsync("E2E host failed: " + e.GetType().Name);
    return 1;
}
finally
{
    await rig.StopAsync();
}

return 0;
