using System.Text.Json;
using Puck.Assets;
using Puck.Assets.Documents;
using Puck.Confinement;

namespace Puck.Launcher.Release;

/// <summary>What one <see cref="InstallPruner.Prune"/> pass did.</summary>
/// <param name="Skipped">Why nothing was pruned (a lease was busy, the platform is unsupported, or the tree could not be
/// read safely), or <see langword="null"/> when the pass ran.</param>
/// <param name="KeptVersions">The version directories left under <c>versions/</c>, most recently installed first.</param>
/// <param name="RunningVersions">The kept versions that a live process holds.</param>
/// <param name="DeletedVersions">The version directories removed, most recently installed first.</param>
/// <param name="DeletedObjects">The number of unreferenced content-addressed object files removed.</param>
/// <param name="Refused">Each entry left in place because removing it would act through a link, because it could not
/// be removed or read, or because the object sweep could not know every kept version's objects, with the reason.</param>
public sealed record InstallPruneResult(
    string? Skipped,
    IReadOnlyList<string> KeptVersions,
    IReadOnlyList<string> RunningVersions,
    IReadOnlyList<string> DeletedVersions,
    int DeletedObjects,
    IReadOnlyList<string> Refused
);
/// <summary>
/// Deletes past versions from an install root, keeping <c>current</c>, <c>last-good</c>, the version a pending
/// <c>install-intent.json</c> names, every version a live process holds (<see cref="InstallRegistration"/>), and the
/// <c>keepVersions</c> most recently installed others (by <see cref="InstallRecord.Sequence"/>; a version without a
/// record ranks below every recorded one), then the content-addressed objects nothing kept references.
/// <para>Every step acts through <see cref="ConfinedDirectory"/> handles opened from the install root: nothing is
/// looked up or deleted by a path checked earlier, and no link is followed. A pass holds
/// <see cref="InstallLeases.PublicationLockFileName"/> and then <see cref="InstallLeases.UpdateLockFileName"/>, and is
/// skipped when either is busy, so it never interleaves with an install, a stub launch, a registration or a local
/// publication. A version is opened for removal first (<see cref="ConfinedDirectory.TryOpenChildForRemoval"/>), which
/// fails while any process holds it; it then leaves <c>versions/</c> whole by one move into <c>trash/</c>, and is
/// deleted there through the same handle. An interrupted pass leaves complete versions and a partial <c>trash/</c>
/// entry, which the next pass removes first. A version that is or holds a link is refused and kept.</para>
/// <para>A kept version's objects are the pins its install record names, or, for a version without a record, the
/// content pins of the regular files it holds (a link is never a staged object). A pending intent's and a local
/// release source's channel manifests' objects are kept too. A malformed intent skips the pass, and a store whose
/// manifest does not parse or validate keeps all its objects; a version whose install record cannot be read is kept.
/// Platforms other than Windows and Linux x64 skip the pass.</para>
/// </summary>
public static class InstallPruner {
    private const int MaximumPointerBytes = 4096;

    private static readonly string[] StoreDirectoryNames = [ObjectCacheDirectoryName, LocalSourceDirectoryName];

    /// <summary>The stager's content-addressed object cache, directly under the install root.</summary>
    public const string ObjectCacheDirectoryName = "objects";
    /// <summary>A local release source's content-addressed root, directly under the install root (the operator
    /// adapter's development channel publishes here).</summary>
    public const string LocalSourceDirectoryName = "development-source";
    /// <summary>Where a version waits for deletion after leaving <c>versions/</c>, directly under the install root.</summary>
    public const string TrashDirectoryName = InstallLayout.TrashDirectoryName;
    /// <summary>The version directories' parent, directly under the install root.</summary>
    public const string VersionsDirectoryName = InstallLayout.VersionsDirectoryName;

    private static StringComparer NameComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Prunes <paramref name="installRoot"/> once, under both install leases.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="keepVersions">The number of most recently installed versions kept beyond the protected ones; a
    /// negative count keeps none.</param>
    /// <returns>What the pass kept, deleted and refused, or why it was skipped.</returns>
    public static InstallPruneResult Prune(string installRoot, int keepVersions) {
        var refused = new List<string>();
        string skipped;

        try {
            using var root = ConfinedDirectory.OpenAnchor(create: false, path: installRoot);

            if (root is null) {
                skipped = "the install root does not exist";
            } else {
                using var publication = root.TryAcquireLock(name: InstallLeases.PublicationLockFileName);
                using var update = ((publication is null) ? null : root.TryAcquireLock(name: InstallLeases.UpdateLockFileName));

                if (update is not null) {
                    return PruneUnderLeases(keepVersions: Math.Max(val1: 0, val2: keepVersions), refused: refused, root: root);
                }
                skipped = $"another process holds {((publication is null) ? InstallLeases.PublicationLockFileName : InstallLeases.UpdateLockFileName)}";
            }
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or JsonException or PlatformNotSupportedException)) {
            skipped = $"the install could not be pruned safely: {error.Message}";
        }
        return new InstallPruneResult(DeletedObjects: 0, DeletedVersions: [], KeptVersions: [], Refused: refused, RunningVersions: [], Skipped: skipped);
    }
    /// <summary>Removes one version directory, under the caller's <see cref="InstallLeases.UpdateLockFileName"/>, the
    /// way a prune pass does: refused while a process holds it or when it is or holds a link, moved whole into
    /// <c>trash/</c>, then deleted there through handles. A deletion that stops part way leaves its <c>trash/</c> entry
    /// for the next pass; the version has left <c>versions/</c> either way.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="version">The version directory's name.</param>
    /// <returns><see langword="true"/> when the version was removed; <see langword="false"/> when it, its
    /// <c>versions/</c> or the install root did not exist.</returns>
    /// <exception cref="IOException">The version is held by a live process, is or holds a link, or cannot be
    /// moved.</exception>
    public static bool RemoveVersion(string installRoot, string version) {
        using var root = ConfinedDirectory.OpenAnchor(create: false, path: installRoot);
        using var versions = root?.Child(name: VersionsDirectoryName);

        if ((versions is null) || !versions.Entries().Any(predicate: entry => NameComparer.Equals(x: entry.Name, y: version))) { return false; }
        using var trash = root!.Child(create: true, name: TrashDirectoryName)!;
        var refused = new List<string>();

        return (Remove(name: version, refused: refused, trash: trash, versions: versions) switch {
            Removal.Removed => true,
            Removal.Gone => false,
            Removal.Running => throw new IOException(message: $"versions/{version} is held by a running process."),
            _ => throw new IOException(message: string.Join(separator: "; ", values: refused)),
        });
    }

    private enum Removal { Removed, Gone, Running, Refused }

    private static InstallPruneResult PruneUnderLeases(ConfinedDirectory root, int keepVersions, List<string> refused) {
        using var trash = root.Child(create: true, name: TrashDirectoryName)!;

        foreach (var entry in trash.Entries()) {
            _ = Attempt(action: () => trash.DeleteChild(name: entry.Name), entry: $"{TrashDirectoryName}/{entry.Name}", refused: refused);
        }
        var (intentVersion, intentObjects) = ReadIntent(root: root);
        var protectedNames = new HashSet<string>(comparer: NameComparer);

        foreach (var pointer in new[] { "current", "last-good" }) {
            if (ReadPointer(name: pointer, root: root) is { } named) { _ = protectedNames.Add(item: named); }
        }
        if (intentVersion is not null) { _ = protectedNames.Add(item: intentVersion); }
        using var versions = root.Child(name: VersionsDirectoryName);
        var installed = new List<(string Name, InstallRecord? Record)>();

        foreach (var entry in (versions?.Entries() ?? [])) {
            if (entry.Kind == ConfinedEntryKind.Link) {
                refused.Add(item: $"{VersionsDirectoryName}/{entry.Name}: a link");
            } else if (entry.Kind == ConfinedEntryKind.Directory) {
                InstallRecord? record = null;

                // A record that cannot be read might rank the version among the kept ones, so the version is kept and
                // its objects come from the files it holds.
                if (!Attempt(action: () => { using var version = versions!.Child(name: entry.Name); record = ((version is null) ? null : InstallRecord.TryRead(version: version)); },
                    entry: $"{VersionsDirectoryName}/{entry.Name}: its install record cannot be read, so it is kept", refused: refused)) {
                    _ = protectedNames.Add(item: entry.Name);
                }
                installed.Add(item: (entry.Name, record));
            }
        }
        // Most recent install first; unrecorded versions last, in a fixed order.
        installed.Sort(comparison: static (left, right) => ((((right.Record?.Sequence ?? 0L).CompareTo(value: (left.Record?.Sequence ?? 0L))) is var order and not 0)
            ? order
            : string.CompareOrdinal(strA: right.Name, strB: left.Name)));
        var kept = new List<(string Name, InstallRecord? Record)>();
        var running = new List<string>();
        var deleted = new List<string>();
        var retained = 0;

        foreach (var version in installed) {
            if (protectedNames.Contains(item: version.Name) || (retained++ < keepVersions)) {
                kept.Add(item: version);
                continue;
            }
            switch (Remove(name: version.Name, refused: refused, trash: trash, versions: versions!)) {
                case Removal.Removed:
                    deleted.Add(item: version.Name);
                    break;
                case Removal.Running:
                    running.Add(item: version.Name);
                    kept.Add(item: version);
                    break;
                case Removal.Refused:
                    kept.Add(item: version);
                    break;
            }
        }
        var referenced = new HashSet<string>(collection: intentObjects, comparer: StringComparer.OrdinalIgnoreCase);
        var known = true;

        foreach (var version in kept) {
            known &= Attempt(action: () => referenced.UnionWith(other: ((version.Record is { } record)
                ? PinHexes(pins: record.Files)
                : ObjectsHeld(name: version.Name, versions: versions!))), entry: $"{VersionsDirectoryName}/{version.Name}: its objects are unknown, so no object was swept", refused: refused);
        }
        return new InstallPruneResult(
            DeletedObjects: (known ? StoreDirectoryNames.Sum(selector: store => SweepStore(referenced: referenced, refused: refused, root: root, storeName: store)) : 0),
            DeletedVersions: deleted,
            KeptVersions: [.. kept.Select(selector: static version => version.Name)],
            Refused: refused,
            RunningVersions: running,
            Skipped: null
        );
    }
    // Runs one deletion or read, recording a failure against the entry instead of ending the pass.
    private static bool Attempt(Action action, string entry, List<string> refused) {
        try {
            action();
            return true;
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)) {
            refused.Add(item: $"{entry}: {error.Message}");
            return false;
        }
    }
    private static Removal Remove(ConfinedDirectory versions, ConfinedDirectory trash, string name, List<string> refused) {
        ConfinedDirectory? held;

        try {
            held = versions.TryOpenChildForRemoval(name: name);
        } catch (DirectoryNotFoundException) {
            return Removal.Gone;
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
            refused.Add(item: $"{VersionsDirectoryName}/{name}: {error.Message}");
            return Removal.Refused;
        }
        if (held is null) { return Removal.Running; }
        using (held) {
            string? link = null;
            var trashName = Guid.NewGuid().ToString(format: "N");

            if (!Attempt(action: () => link = held.FindLink(), entry: $"{VersionsDirectoryName}/{name}", refused: refused)) { return Removal.Refused; }
            if (link is not null) {
                refused.Add(item: $"{VersionsDirectoryName}/{name}: holds the link '{link}'");
                return Removal.Refused;
            }
            if (!Attempt(action: () => held.MoveTo(destination: trash, name: name, newName: trashName, parent: versions), entry: $"{VersionsDirectoryName}/{name}", refused: refused)) {
                return Removal.Refused;
            }
            _ = Attempt(action: () => held.DeleteTree(name: trashName, parent: trash), entry: $"{TrashDirectoryName}/{trashName}", refused: refused);
            return Removal.Removed;
        }
    }
    private static string? ReadPointer(ConfinedDirectory root, string name) {
        using var stream = root.OpenFile(name: name);

        if (stream is null) { return null; }
        if (stream.Length > MaximumPointerBytes) { throw new IOException(message: $"'{name}' exceeds {MaximumPointerBytes} bytes."); }
        using var reader = new StreamReader(stream: stream);
        var text = reader.ReadToEnd().Trim();

        return ((text.Length == 0) ? null : text);
    }
    private static byte[]? ReadBounded(ConfinedDirectory directory, string name) {
        using var stream = directory.OpenFile(name: name);

        if (stream is null) { return null; }
        if (stream.Length > ReleaseSourceReader.MaximumManifestBytes) { throw new IOException(message: $"'{name}' exceeds {ReleaseSourceReader.MaximumManifestBytes} bytes."); }
        var bytes = new byte[checked((int)stream.Length)];

        stream.ReadExactly(buffer: bytes);
        return bytes;
    }
    private static IEnumerable<string> PinHexes(IEnumerable<string> pins) {
        foreach (var text in pins) {
            if (ContentPin.TryParse(pin: out var pin, text: text)) { yield return pin.Hex; }
        }
    }
    private static IEnumerable<string> ManifestObjects(ReleaseManifest manifest) =>
        PinHexes(pins: (manifest.Payloads ?? []).SelectMany(selector: static payload => (payload.Files ?? []).Select(selector: static file => file.Hash)));
    // A malformed intent throws, which skips the pass: an unreadable intent may name any version.
    private static (string? Version, IReadOnlyList<string> Objects) ReadIntent(ConfinedDirectory root) {
        if (ReadBounded(directory: root, name: "install-intent.json") is not { } bytes) { return (null, []); }
        using var intent = JsonDocument.Parse(utf8Json: bytes);
        var manifest = Valid(manifest: (((intent.RootElement.ValueKind == JsonValueKind.Object) && intent.RootElement.TryGetProperty(propertyName: "manifest", value: out var element))
            ? element.Deserialize<ReleaseManifest>(options: DocumentJsonOptions.Shared)
            : null), what: "the pending install intent");

        return (manifest.Version, ManifestObjects(manifest: manifest).ToArray());
    }
    // A manifest that is absent or fails structural validation names nothing reliable, so the caller keeps everything it
    // might protect: a malformed intent skips the pass, and a malformed channel manifest keeps its store's objects.
    private static ReleaseManifest Valid(ReleaseManifest? manifest, string what) {
        if (manifest is null) { throw new JsonException(message: $"{what} names no manifest"); }
        var errors = ReleaseCanonicalizer.Validate(document: manifest);

        return ((errors.Count == 0) ? manifest : throw new JsonException(message: $"{what} holds a manifest that fails validation: {errors[0]}"));
    }
    // The content pins of every file a version without an install record holds: what it was staged from.
    private static List<string> ObjectsHeld(ConfinedDirectory versions, string name) {
        using var version = (versions.Child(name: name) ?? throw new IOException(message: "it no longer exists"));
        var hexes = new List<string>();

        Hash(directory: version, hexes: hexes);
        return hexes;
    }
    private static void Hash(ConfinedDirectory directory, List<string> hexes) {
        foreach (var entry in directory.Entries()) {
            switch (entry.Kind) {
                case ConfinedEntryKind.Directory: {
                        using var child = (directory.Child(name: entry.Name) ?? throw new IOException(message: $"'{entry.Name}' vanished"));

                        Hash(directory: child, hexes: hexes);
                        break;
                    }
                case ConfinedEntryKind.File: {
                        using var stream = (directory.OpenFile(name: entry.Name) ?? throw new IOException(message: $"'{entry.Name}' vanished"));

                        hexes.Add(item: ContentPin.Compute(content: stream).Hex);
                        break;
                    }
                default:
                    // A link or a special file is never what staging wrote, so no cached object stands for it.
                    break;
            }
        }
    }
    // Sweeps <store>/objects/sha256/<fanout>/<hex>. A store that is also a release source protects what its channel
    // manifests (<store>/<channel>/manifest.json) name.
    private static int SweepStore(ConfinedDirectory root, string storeName, HashSet<string> referenced, List<string> refused) {
        ConfinedDirectory? store = null, objects = null, sha256 = null;

        try {
            if (!Attempt(action: () => {
                store = root.Child(name: storeName);
                objects = store?.Child(name: "objects");
                sha256 = objects?.Child(name: "sha256");
            }, entry: storeName, refused: refused) || (sha256 is null)) {
                return 0;
            }
            var protectedHexes = new HashSet<string>(collection: referenced, comparer: StringComparer.OrdinalIgnoreCase);

            if (!Attempt(action: () => protectedHexes.UnionWith(other: ChannelObjects(store: store!)), entry: $"{storeName}: a channel manifest", refused: refused)) {
                return 0;
            }
            var deleted = 0;

            foreach (var fanout in sha256.Entries()) {
                if (fanout.Kind == ConfinedEntryKind.Link) {
                    refused.Add(item: $"{storeName}/objects/sha256/{fanout.Name}: a link");
                    continue;
                }
                if (fanout.Kind != ConfinedEntryKind.Directory) { continue; }
                var remaining = false;

                using (var directory = sha256.Child(name: fanout.Name)) {
                    foreach (var entry in (directory?.Entries() ?? [])) {
                        var path = $"{storeName}/objects/sha256/{fanout.Name}/{entry.Name}";

                        if (entry.Kind == ConfinedEntryKind.Link) { refused.Add(item: $"{path}: a link"); }
                        if ((entry.Kind != ConfinedEntryKind.File) || protectedHexes.Contains(item: entry.Name) ||
                            !Attempt(action: () => directory!.DeleteChild(name: entry.Name), entry: path, refused: refused)) {
                            remaining = true;
                            continue;
                        }
                        deleted++;
                    }
                }
                if (!remaining) {
                    _ = Attempt(action: () => sha256.DeleteChild(name: fanout.Name), entry: $"{storeName}/objects/sha256/{fanout.Name}", refused: refused);
                }
            }
            return deleted;
        } finally {
            sha256?.Dispose();
            objects?.Dispose();
            store?.Dispose();
        }
    }
    // The objects every channel manifest directly under a store names; a manifest that does not parse or validate throws.
    private static List<string> ChannelObjects(ConfinedDirectory store) {
        var hexes = new List<string>();

        foreach (var channel in store.Entries()) {
            if ((channel.Kind != ConfinedEntryKind.Directory) || (channel.Name == "objects")) { continue; }
            using var directory = store.Child(name: channel.Name);

            if ((directory is null) || (ReadBounded(directory: directory, name: "manifest.json") is not { } bytes)) { continue; }
            hexes.AddRange(collection: ManifestObjects(manifest: Valid(manifest: JsonSerializer.Deserialize<ReleaseManifest>(options: DocumentJsonOptions.Shared, utf8Json: bytes),
                what: $"'{channel.Name}/manifest.json'")));
        }
        return hexes;
    }
}
