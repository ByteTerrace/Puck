using System.Diagnostics;
using Puck.Confinement;
using Puck.Launcher.Stub;

// The stub's entire job: ask the install's release record what to launch, exec it, forward the exit code. Deliberately
// exempt from the self-update mechanism it drives — a bricked stub is unrecoverable in-band and needs a reinstall, the
// same way a bricked bootloader would, which is why this stays this small.
var installRoot = AppContext.BaseDirectory.TrimEnd(
    Path.DirectorySeparatorChar,
    Path.AltDirectorySeparatorChar
);
InstallTransaction transaction;
try {
    transaction = InstallTransaction.Begin(cancellationToken: CancellationToken.None, installRoot: installRoot, timeout: StubInstall.LeaseWait);
} catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)) {
    Console.Error.WriteLine(value: $"[stub: install is busy or unavailable — {exception.Message}]");
    return 2;
}
using var transactionLifetime = transaction;
StubConfiguration configuration;
try {
    configuration = StubConfigurationFile.Load(installRoot: installRoot);
} catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)) {
    Console.Error.WriteLine(value: $"[stub: could not read {StubInstall.StubConfigFileName} — {exception.Message}]");

    return 2;
}
LaunchDecision decision;
try {
    // Rejection, rollback, the state generation floor and this launch's attempt are one durable commit, made before the
    // child starts; the child's registration re-checks the record under the same lease before it runs.
    decision = transaction.SelectLaunch(maxAttempts: configuration.MaxAttempts);
} catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentOutOfRangeException)) {
    Console.Error.WriteLine(value: $"[stub: the install's release record is unavailable — {exception.Message}]");
    return 2;
}
foreach (var note in decision.Notes) {
    Console.Error.WriteLine(value: $"[stub: {note}]");
}
if (decision.Version is not { } launchVersion) {
    Console.Error.WriteLine(value: $"[stub: refusing to launch — {decision.Refusal}]");
    return 2;
}
ConfinedDirectory heldVersion;
try {
    heldVersion = StubInstall.HoldVersion(root: transaction.Root, version: launchVersion);
} catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)) {
    Console.Error.WriteLine(value: $"[stub: could not hold version {launchVersion} for its launch — {exception.Message}]");
    return 2;
}
// Held from before the child starts until it exits, so pruning never removes the version the child runs from.
using var heldVersionLifetime = heldVersion;
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
// Release before child startup: the child's registration waits for this lease, and the child owns update commands.
transaction.Dispose();
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
