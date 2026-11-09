using System.Text;
using Puck.Assets;
using Puck.Confinement;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Builds install-root states through the release record's own doors, so a law starts from a state the
/// launcher could have produced: an installed version is committed by <see cref="InstallTransaction.CommitApply"/> and
/// proven by <see cref="InstallTransaction.RecordHealthy"/>.</summary>
internal static class InstallSeed {
    /// <summary>A stand-in payload identity named by <paramref name="label"/>.</summary>
    public static string Pin(string label) => ContentPin.Compute(content: Encoding.UTF8.GetBytes(s: label)).ToString();
    /// <summary>Installs <paramref name="version"/> as current (it must follow the current version), creating its directory,
    /// and proves it healthy unless <paramref name="proven"/> is false.</summary>
    public static InstallRecord Install(string installRoot, string version, int stateGeneration = 1, string? payloadPin = null, bool proven = true) {
        Directory.CreateDirectory(path: Path.Combine(path1: installRoot, path2: InstallLayout.VersionsDirectoryName, path3: version));
        using var transaction = InstallTransaction.TryBegin(installRoot: installRoot)!;
        var refusal = transaction.CommitApply(files: [Pin(label: $"file:{version}")], payloadPin: (payloadPin ?? Pin(label: version)), precedence: ReleaseVersion.Compare,
            record: out var record, recovering: false, rid: UpdateService.CurrentRid(), stateGeneration: stateGeneration, version: version);

        Assert.Null(@object: refusal);
        if (proven) { Assert.True(condition: transaction.RecordHealthy(version: version)); }
        return record!;
    }
    /// <summary>The release record as it is on disk.</summary>
    public static ReleasePolicy Policy(string installRoot) => ReleasePolicy.ReadUnlocked(installRoot: installRoot);
    /// <summary>The release record's current version.</summary>
    public static string? Current(string installRoot) => Policy(installRoot: installRoot).Current;
    /// <summary>The version rollback would select now.</summary>
    public static string? RollbackTarget(string installRoot) {
        using var transaction = InstallTransaction.TryBegin(installRoot: installRoot)!;

        return transaction.RollbackTarget();
    }
    /// <summary>Runs the stub's selection once, as one stub launch does.</summary>
    public static LaunchDecision Launch(string installRoot, int maxAttempts) {
        using var transaction = InstallTransaction.TryBegin(installRoot: installRoot)!;

        return transaction.SelectLaunch(maxAttempts: maxAttempts);
    }
    /// <summary>Records <paramref name="version"/>'s startup health, as its started process does.</summary>
    public static bool Healthy(string installRoot, string version) {
        using var transaction = InstallTransaction.TryBegin(installRoot: installRoot)!;

        return transaction.RecordHealthy(version: version);
    }
}
