using System.Text.Json;

namespace Puck.Launcher.Stub;

/// <summary>One version's consecutive-unhealthy-attempt count, as <see cref="StubInstall.HealthFileName"/> holds it.
/// A single-slot record (not a per-version map): a record whose <see cref="Version"/> does not match the version
/// being considered carries no count for it — see <see cref="StubHealth.AttemptsFor"/>.</summary>
/// <param name="Version">The version the count belongs to.</param>
/// <param name="Attempts">Consecutive attempts recorded for <paramref name="Version"/> since it last cleared healthy.</param>
public sealed record StubHealthRecord(string Version, int Attempts);
/// <summary>Read/write access to the health attempt counter — flushed by the stub BEFORE it launches a candidate
/// (so a hang or an early kill still counts), and cleared by the running app once it reports healthy
/// (<c>Puck.Launcher.Release.HealthGateFile</c>, an independent writer of this same file — see
/// <see cref="StubInstall"/>'s own remarks on why the two sides share no code).</summary>
public static class StubHealth {
    /// <summary>Resolves the health file's full path.</summary>
    /// <param name="installRoot">The install root.</param>
    public static string FilePath(string installRoot) =>
        Path.Combine(path1: installRoot, path2: StubInstall.HealthDirectoryName, path3: StubInstall.HealthFileName);
    /// <summary>Reads the health record, tolerating an absent or corrupt file as an empty one.</summary>
    /// <param name="installRoot">The install root.</param>
    public static StubHealthRecord Read(string installRoot) {
        var path = FilePath(installRoot: installRoot);

        if (!File.Exists(path: path)) {
            return new StubHealthRecord(Attempts: 0, Version: string.Empty);
        }

        try {
            return (JsonSerializer.Deserialize<StubHealthRecord>(json: File.ReadAllText(path: path))
                ?? new StubHealthRecord(Attempts: 0, Version: string.Empty));
        } catch (JsonException) {
            return new StubHealthRecord(Attempts: 0, Version: string.Empty);
        }
    }
    /// <summary>Reads a record's attempt count for a specific version — a record naming a different version (or an
    /// absent/corrupt file) carries no history for the version asked about.</summary>
    /// <param name="record">The record read via <see cref="Read"/>.</param>
    /// <param name="version">The version to look up.</param>
    public static int AttemptsFor(StubHealthRecord record, string version) =>
        (string.Equals(a: record.Version, b: version, comparisonType: StringComparison.Ordinal) ? record.Attempts : 0);
    /// <summary>Increments the recorded attempt count for <paramref name="version"/> and flushes it atomically,
    /// BEFORE the stub launches that version.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="version">The version about to be launched.</param>
    public static void IncrementAndFlush(string installRoot, string version) {
        var attempts = (AttemptsFor(record: Read(installRoot: installRoot), version: version) + 1);

        Write(installRoot: installRoot, record: new StubHealthRecord(Attempts: attempts, Version: version));
    }
    /// <summary>Writes the health record atomically (write-to-temp then rename).</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="record">The record to write.</param>
    public static void Write(string installRoot, StubHealthRecord record) {
        var directory = Path.Combine(path1: installRoot, path2: StubInstall.HealthDirectoryName);
        var tmpPath = Path.Combine(path1: directory, path2: $"{Guid.NewGuid():n}.tmp");

        Directory.CreateDirectory(path: directory);
        File.WriteAllText(path: tmpPath, contents: JsonSerializer.Serialize(value: record));
        File.Move(sourceFileName: tmpPath, destFileName: FilePath(installRoot: installRoot), overwrite: true);
    }
}
