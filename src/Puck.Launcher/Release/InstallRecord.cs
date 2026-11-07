using System.Text.Json;
using Puck.Assets;
using Puck.Assets.Documents;
using Puck.Confinement;

namespace Puck.Launcher.Release;

/// <summary>
/// What the applier records about a version when it installs it, in <c>versions/&lt;version&gt;/install-record.json</c>
/// beside its <c>state-generation</c>: the install's place in the root's install order and the content pins of its
/// payload files. <see cref="InstallPruner"/> ranks versions by <paramref name="Sequence"/> and protects the objects
/// <paramref name="Files"/> names. The record lives inside the version directory, so a version and its record leave the
/// tree together; <see cref="ReleaseCanonicalizer"/> refuses a payload file of the same name, so no release can
/// overwrite it.
/// </summary>
/// <param name="Sequence">The install order: one more than the largest sequence recorded under the root when it was
/// installed. A higher sequence is a more recent install, whatever the version strings' precedence.</param>
/// <param name="Files">The payload files' content pins, as the manifest spells them.</param>
public sealed record InstallRecord(long Sequence, IReadOnlyList<string> Files) {
    private const int MaximumBytes = ((16 * 1024) * 1024);

    /// <summary>The record's file name inside a version directory.</summary>
    public const string FileName = "install-record.json";

    /// <summary>Reads a version directory's record through its handle.</summary>
    /// <param name="version">The held version directory.</param>
    /// <returns>The record, or <see langword="null"/> when it is absent, does not parse, or names no files, a sequence or a pin that is not well formed.</returns>
    /// <exception cref="IOException">The record is a link, oversized or cannot be read.</exception>
    public static InstallRecord? TryRead(ConfinedDirectory version) {
        ArgumentNullException.ThrowIfNull(argument: version);
        using var stream = version.OpenFile(name: FileName);

        if (stream is null) { return null; }
        if (stream.Length > MaximumBytes) { throw new IOException(message: $"'{FileName}' exceeds {MaximumBytes} bytes."); }
        try {
            // A record whose order, inventory or pins are not well formed names nothing reliable; its version then counts as
            // one without a record: kept, with the content pins of the files it holds.
            return (((JsonSerializer.Deserialize<InstallRecord>(options: DocumentJsonOptions.Shared, utf8Json: stream) is { Files: { Count: > 0 }, Sequence: > 0 } record) &&
                record.Files.All(predicate: static file => ContentPin.TryParse(pin: out _, text: file)))
                ? record
                : null);
        } catch (JsonException) {
            return null;
        }
    }
    /// <summary>Records <paramref name="version"/> as the root's most recent install. The caller holds
    /// <see cref="InstallLeases.UpdateLockFileName"/>, so no other install reads or writes the order meanwhile.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="version">The staged version directory's name.</param>
    /// <param name="files">The payload files' content pins.</param>
    /// <returns>The written record.</returns>
    /// <exception cref="IOException">The versions directory cannot be read or the record cannot be written.</exception>
    public static InstallRecord Write(string installRoot, string version, IEnumerable<string> files) {
        var latest = 0L;

        using (var root = ConfinedDirectory.OpenAnchor(create: false, path: installRoot))
        using (var versions = root?.Child(name: InstallPruner.VersionsDirectoryName)) {
            foreach (var entry in (versions?.Entries() ?? [])) {
                if (entry.Kind != ConfinedEntryKind.Directory) { continue; }
                using var directory = versions!.Child(name: entry.Name);

                if ((directory is not null) && (TryRead(version: directory) is { } existing)) {
                    latest = Math.Max(val1: latest, val2: existing.Sequence);
                }
            }
        }
        var record = new InstallRecord(Files: [.. files], Sequence: (latest + 1));

        AtomicFile.WriteAllText(
            contents: JsonSerializer.Serialize(options: DocumentJsonOptions.Shared, value: record),
            path: Path.Combine(path1: installRoot, path2: InstallPruner.VersionsDirectoryName, path3: version, path4: FileName)
        );
        return record;
    }
}
