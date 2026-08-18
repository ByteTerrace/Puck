using System.Diagnostics;
using Puck.Launcher.Stub;

// The stub's entire job: read one pointer, exec the target, forward the exit code. Deliberately exempt from the
// self-update mechanism it drives — a bricked stub is unrecoverable in-band and needs a reinstall, the same way a
// bricked bootloader would, which is why this stays this small.
var installRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
StubConfiguration configuration;
try {
    configuration = StubConfigurationFile.Load(installRoot: installRoot);
} catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)) {
    Console.Error.WriteLine(value: $"[stub: could not read {StubInstall.StubConfigFileName} — {exception.Message}]");

    return 2;
}
var current = StubInstall.ReadPointer(path: Path.Combine(path1: installRoot, path2: StubInstall.CurrentFileName));
if (current is null) {
    Console.Error.WriteLine(value: $"[stub: no '{StubInstall.CurrentFileName}' pointer under {installRoot} — install is empty]");

    return 2;
}
var lastGood = StubInstall.ReadPointer(path: Path.Combine(path1: installRoot, path2: StubInstall.LastGoodFileName));
var attempts = StubHealth.AttemptsFor(record: StubHealth.Read(installRoot: installRoot), version: current);
var decision = StubDecisionTable.Decide(
    attempts: attempts,
    currentGeneration: StubInstall.ReadGeneration(installRoot: installRoot, version: current),
    hasLastGood: (lastGood is not null),
    lastGoodGeneration: ((lastGood is null) ? 0 : StubInstall.ReadGeneration(installRoot: installRoot, version: lastGood)),
    maxAttempts: configuration.MaxAttempts
);
var launchVersion = current;
switch (decision) {
    case StubAction.RevertToLastGood:
        Console.Error.WriteLine(value: $"[stub: version {current} reached {attempts}/{configuration.MaxAttempts} attempts — reverting to last-good {lastGood}]");
        StubInstall.WritePointerAtomic(fileName: StubInstall.CurrentFileName, installRoot: installRoot, value: lastGood!);
        launchVersion = lastGood!;

        break;
    case StubAction.LaunchCurrentAnyway:
        Console.Error.WriteLine(value: $"[stub: stuck on version {current} — {attempts}/{configuration.MaxAttempts} attempts exhausted and its state-generation exceeds last-good's; launching it anyway rather than risk an incompatible read]");

        break;
}
StubHealth.IncrementAndFlush(installRoot: installRoot, version: launchVersion);
var executablePath = Path.Combine(path1: StubInstall.VersionDirectory(installRoot: installRoot, version: launchVersion), path2: configuration.AppExecutableFileName);
if (!File.Exists(path: executablePath)) {
    Console.Error.WriteLine(value: $"[stub: '{executablePath}' does not exist]");

    return 2;
}
var startInfo = new ProcessStartInfo {
    FileName = executablePath,
    UseShellExecute = false,
    WorkingDirectory = Path.GetDirectoryName(path: executablePath),
};
foreach (var argument in args) {
    startInfo.ArgumentList.Add(item: argument);
}
using var process = (Process.Start(startInfo: startInfo) ?? throw new InvalidOperationException(message: $"failed to start '{executablePath}'"));
process.WaitForExit();
return process.ExitCode;
