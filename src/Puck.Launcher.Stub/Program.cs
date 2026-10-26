using System.Diagnostics;
using Puck.Launcher.Stub;

// The stub's entire job: read one pointer, exec the target, forward the exit code. Deliberately exempt from the
// self-update mechanism it drives — a bricked stub is unrecoverable in-band and needs a reinstall, the same way a
// bricked bootloader would, which is why this stays this small.
var installRoot = AppContext.BaseDirectory.TrimEnd(
    Path.DirectorySeparatorChar,
    Path.AltDirectorySeparatorChar
);
FileStream updateLease;
try {
    updateLease = StubInstall.AcquireUpdateLease(installRoot: installRoot);
} catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
    Console.Error.WriteLine(value: $"[stub: install is busy or unavailable — {exception.Message}]");
    return 2;
}
using var leaseLifetime = updateLease;
StubConfiguration configuration;
try {
    configuration = StubConfigurationFile.Load(installRoot: installRoot);
} catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)) {
    Console.Error.WriteLine(value: $"[stub: could not read {StubInstall.StubConfigFileName} — {exception.Message}]");

    return 2;
}
var current = StubInstall.ReadPointer(path: Path.Combine(
    path1: installRoot,
    path2: StubInstall.CurrentFileName
));
if (current is null) {
    Console.Error.WriteLine(value: $"[stub: no '{StubInstall.CurrentFileName}' pointer under {installRoot} — install is empty]");

    return 2;
}
var lastGood = StubInstall.ReadPointer(path: Path.Combine(
    path1: installRoot,
    path2: StubInstall.LastGoodFileName
));
var attempts = StubHealth.AttemptsFor(
    record: StubHealth.Read(installRoot: installRoot),
    version: current
);
StubAction decision;
try {
    decision = StubDecisionTable.Decide(
        attempts: attempts,
        currentGeneration: StubInstall.ReadGeneration(
            installRoot: installRoot,
            version: current
        ),
        hasLastGood: (lastGood is not null),
        lastGoodGeneration: ((lastGood is null)
        ? 0
        : StubInstall.ReadGeneration(
                installRoot: installRoot,
                version: lastGood
            )),
        maxAttempts: configuration.MaxAttempts
    );
} catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
    Console.Error.WriteLine(value: $"[stub: state generation is unavailable — {exception.Message}]");
    return 2;
}
var launchVersion = current;
switch (decision) {
    case StubAction.RevertToLastGood:
        Console.Error.WriteLine(value: $"[stub: version {current} reached {attempts}/{configuration.MaxAttempts} attempts — reverting to last-good {lastGood}]");
        try {
            StubInstall.RevertToLastGood(failedVersion: current, installRoot: installRoot, lastGoodVersion: lastGood!);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)) {
            Console.Error.WriteLine(value: $"[stub: could not cancel pending install and roll back — {exception.Message}]");
            return 2;
        }
        launchVersion = lastGood!;

        break;
    case StubAction.LaunchCurrentAnyway:
        Console.Error.WriteLine(value: $"[stub: stuck on version {current} — {attempts}/{configuration.MaxAttempts} attempts exhausted and its state-generation exceeds last-good's; launching it anyway rather than risk an incompatible read]");

        break;
}
StubHealth.IncrementAndFlush(
    installRoot: installRoot,
    version: launchVersion
);
var executablePath = Path.Combine(
    path1: StubInstall.VersionDirectory(
        installRoot: installRoot,
        version: launchVersion
    ),
    path2: configuration.AppExecutableFileName
);
if (!File.Exists(path: executablePath)) {
    Console.Error.WriteLine(value: $"[stub: '{executablePath}' does not exist]");

    return 2;
}
// Selection and any rollback used the pointer read under this lease. Release before child startup: the child
// owns update commands, and retaining the lease until it exits would prevent every self-update.
updateLease.Dispose();
var startInfo = new ProcessStartInfo {
    FileName = executablePath,
    RedirectStandardInput = Console.IsInputRedirected,
    RedirectStandardOutput = Console.IsOutputRedirected,
    RedirectStandardError = Console.IsErrorRedirected,
    UseShellExecute = false,
    WorkingDirectory = Path.GetDirectoryName(path: executablePath),
};
foreach (var argument in args) {
    startInfo.ArgumentList.Add(item: argument);
}
using var process = (Process.Start(startInfo: startInfo) ?? throw new InvalidOperationException(message: $"failed to start '{executablePath}'"));
Console.Error.WriteLine(value: $"[stub: launched version {launchVersion} pid={process.Id} executable={executablePath.Replace(newChar: '/', oldChar: '\\')}]");
// Redirected handles are not reliably inherited through Process.Start on Windows. Forward bytes explicitly so
// piped consoles and stdio protocols reach the selected application just as an interactive terminal does.
using var inputLifetime = new CancellationTokenSource();
async Task ForwardInputAsync() {
    try {
        await Console.OpenStandardInput().CopyToAsync(process.StandardInput.BaseStream, inputLifetime.Token);
        process.StandardInput.Close();
    } catch (Exception exception) when ((exception is IOException or OperationCanceledException or ObjectDisposedException)) { }
}
var inputPump = (startInfo.RedirectStandardInput ? ForwardInputAsync() : Task.CompletedTask);
var outputPump = (startInfo.RedirectStandardOutput
    ? process.StandardOutput.BaseStream.CopyToAsync(destination: Console.OpenStandardOutput()) : Task.CompletedTask);
var errorPump = (startInfo.RedirectStandardError
    ? process.StandardError.BaseStream.CopyToAsync(destination: Console.OpenStandardError()) : Task.CompletedTask);
await process.WaitForExitAsync();
inputLifetime.Cancel();
await Task.WhenAll(outputPump, errorPump);
// Parent stdin may stay open for the life of an MCP client. It must not hold a finished child open.
_ = inputPump;
return process.ExitCode;
