using System.Text.Json;
using Puck.Assets;
using Puck.Assets.Documents;
using Puck.Confinement;

namespace Puck.Launcher.Release;

/// <summary>What one <see cref="InstallPruner.Prune"/> pass did.</summary>
/// <param name="Skipped">Why nothing was pruned (a lease was busy, the platform is unsupported, or something the pass
/// depends on could not be read or did not validate), or <see langword="null"/> when the pass ran.</param>
/// <param name="KeptVersions">The version directories left under <c>versions/</c>, most recently installed first.</param>
/// <param name="RunningVersions">The kept versions that a live process holds.</param>
/// <param name="DeletedVersions">The version directories removed, most recently installed first.</param>
/// <param name="DeletedObjects">The number of unreferenced content-addressed object files removed.</param>
/// <param name="Refused">Each entry the deletion phase left in place, because removing it would act through a link or
/// because it could not be removed, with the reason.</param>
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
/// <c>install-intent.json</c> names, every version a live process holds (<see cref="InstallRegistration"/>), every
/// version whose install order cannot be read, and the <c>keepVersions</c> most recently installed others (by
/// <see cref="InstallRecord.Sequence"/>), then the content-addressed objects nothing kept references.
/// <para>A pass first reads and validates everything it depends on, deleting nothing: the pointers (each a plain name of
/// an existing version), the pending intent's manifest, every version's install record (valid, or absent and replaced by
/// the content pins of the files the version holds), and every store's layout and channel manifests. Any failure there
/// skips the whole pass with its reason; a pass never deletes on a partial reading, and never throws.</para>
/// <para>Every step acts through <see cref="ConfinedDirectory"/> handles opened from the install root: nothing is
/// looked up or deleted by a path checked earlier, and no link is followed. A pass holds
/// <see cref="InstallLeases.PublicationLockFileName"/> and then <see cref="InstallLeases.UpdateLockFileName"/>, and is
/// skipped when either is busy, so it never interleaves with an install, a stub launch, a registration or a local
/// publication. A version is opened for removal first (<see cref="ConfinedDirectory.TryOpenChildForRemoval"/>), which
/// fails while any process holds it; it then leaves <c>versions/</c> whole by one move into <c>trash/</c>, and is
/// deleted there through the same handle. An interrupted pass leaves complete versions and a partial <c>trash/</c>
/// entry, which the next pass removes first. A version that holds a link is refused and kept. Platforms other than
/// Windows and Linux x64 skip the pass.</para>
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

    private static StringComparer NameComparer => (OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Prunes <paramref name="installRoot"/> once, under both install leases. It never throws: whatever stops
    /// it is reported as <see cref="InstallPruneResult.Skipped"/>.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="keepVersions">The number of most recently installed versions kept beyond the protected ones; a
    /// negative count keeps none.</param>
    /// <returns>What the pass kept, deleted and refused, or why it was skipped.</returns>
    public static InstallPruneResult Prune(string installRoot, int keepVersions) {
        string skipped;

        try {
            using var root = ConfinedDirectory.OpenAnchor(create: false, path: installRoot);

            if (root is null) {
                skipped = "the install root does not exist";
            } else {
                using var publication = root.TryAcquireLock(name: InstallLeases.PublicationLockFileName);
                using var update = ((publication is null) ? null : root.TryAcquireLock(name: InstallLeases.UpdateLockFileName));

                if (update is not null) {
                    var plan = Read(root: root);

                    return ((plan.Failure is { } failure) ? Skip(reason: $"nothing was deleted, because {failure}") : Act(keepVersions: Math.Max(val1: 0, val2: keepVersions), plan: plan, root: root));
                }
                skipped = $"another process holds {((publication is null) ? InstallLeases.PublicationLockFileName : InstallLeases.UpdateLockFileName)}";
            }
        } catch (Exception error) when ((error is not OutOfMemoryException)) {
            // A maintenance pass never ends its caller's start or health check; a failure part way leaves the tree as
            // an interrupted pass would, which the next pass completes.
            skipped = $"the pass stopped: {error.GetType().Name}: {error.Message}";
        }
        return Skip(reason: skipped);
    }
    /// <summary>Removes one version directory, under the caller's <see cref="InstallLeases.UpdateLockFileName"/>, the
    /// way a prune pass does: refused while a process holds it or when it holds a link, moved whole into
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
    // One version as the validation phase read it: its install order when a valid record names one, and the objects it
    // references (its record's pins, or the content pins of the files it holds).
    private sealed record Version(string Name, long? Sequence, IReadOnlySet<string> Objects);
    // Everything the deletion phase acts on, read and validated before anything is deleted; Failure names why the pass
    // must not act at all.
    private sealed record Plan(string? Failure, IReadOnlyList<Version> Versions, IReadOnlySet<string> Protected, IReadOnlySet<string> IntentObjects,
        IReadOnlyDictionary<string, IReadOnlySet<string>> StoreObjects);

    private static InstallPruneResult Skip(string reason) =>
        new(DeletedObjects: 0, DeletedVersions: [], KeptVersions: [], Refused: [], RunningVersions: [], Skipped: reason);
    private static Plan Failed(string reason) =>
        new(Failure: reason, IntentObjects: new HashSet<string>(), Protected: new HashSet<string>(), StoreObjects: new Dictionary<string, IReadOnlySet<string>>(), Versions: []);
    // The validation phase: reads, never deletes. The first thing that cannot be read or does not validate fails it.
    private static Plan Read(ConfinedDirectory root) {
        try {
            using var versions = root.Child(name: VersionsDirectoryName);
            var found = new List<Version>();
            var names = new HashSet<string>(comparer: NameComparer);

            foreach (var entry in (versions?.Entries() ?? [])) {
                if (entry.Kind == ConfinedEntryKind.Link) { return Failed(reason: $"{VersionsDirectoryName}/{entry.Name} is a link"); }
                if (entry.Kind != ConfinedEntryKind.Directory) { continue; }
                using var version = (versions!.Child(name: entry.Name) ?? throw new IOException(message: $"{VersionsDirectoryName}/{entry.Name} vanished"));
                var record = InstallRecord.TryRead(version: version);

                _ = names.Add(item: entry.Name);
                found.Add(item: new Version(
                    Name: entry.Name,
                    Objects: new HashSet<string>(collection: ((record is not null) ? PinHexes(pins: record.Files) : ObjectsHeld(version: version)), comparer: StringComparer.OrdinalIgnoreCase),
                    Sequence: record?.Sequence
                ));
            }
            var protectedNames = new HashSet<string>(comparer: NameComparer);

            foreach (var (pointer, required) in new[] { (InstallLayout.CurrentFileName, true), ("last-good", false) }) {
                var named = ReadPointer(name: pointer, root: root);

                if (named is null) {
                    if (required) { return Failed(reason: $"'{pointer}' names no version"); }
                    continue;
                }
                if (!IsPlainName(name: named) || !names.Contains(item: named)) { return Failed(reason: $"'{pointer}' names '{named}', which is not a version directory"); }
                _ = protectedNames.Add(item: named);
            }
            var (intentVersion, intentObjects) = ReadIntent(root: root);

            if (intentVersion is not null) {
                if (!IsPlainName(name: intentVersion)) { return Failed(reason: $"the pending install intent names '{intentVersion}', which is not a version name"); }
                _ = protectedNames.Add(item: intentVersion);
            }
            var storeObjects = new Dictionary<string, IReadOnlySet<string>>(comparer: StringComparer.Ordinal);

            foreach (var store in StoreDirectoryNames) {
                storeObjects[store] = ChannelObjects(root: root, storeName: store);
            }
            return new Plan(Failure: null, IntentObjects: new HashSet<string>(collection: intentObjects, comparer: StringComparer.OrdinalIgnoreCase), Protected: protectedNames,
                StoreObjects: storeObjects, Versions: found);
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)) {
            return Failed(reason: error.Message);
        }
    }
    // The deletion phase: acts only on what the validation phase read.
    private static InstallPruneResult Act(ConfinedDirectory root, Plan plan, int keepVersions) {
        var refused = new List<string>();
        using var trash = root.Child(create: true, name: TrashDirectoryName)!;
        using var versions = root.Child(name: VersionsDirectoryName);

        foreach (var entry in trash.Entries()) {
            _ = Attempt(action: () => trash.DeleteChild(name: entry.Name), entry: $"{TrashDirectoryName}/{entry.Name}", refused: refused);
        }
        // Most recent install first; a version whose install order is unknown ranks nowhere and is kept.
        var ordered = plan.Versions.OrderByDescending(keySelector: static version => (version.Sequence ?? 0L)).ThenByDescending(keySelector: static version => version.Name, comparer: StringComparer.Ordinal).ToList();
        var kept = new List<Version>();
        var running = new List<string>();
        var deleted = new List<string>();
        var retained = 0;

        foreach (var version in ordered) {
            if (plan.Protected.Contains(item: version.Name) || (version.Sequence is null) || (versions is null)) {
                kept.Add(item: version);
                continue;
            }
            // A running version is kept for its process, never for the allowance: the allowance keeps the newest others.
            // No process registers meanwhile, because registration waits for the update lease this pass holds.
            switch (Probe(name: version.Name, refused: refused, versions: versions)) {
                case Removal.Running:
                    running.Add(item: version.Name);
                    kept.Add(item: version);
                    continue;
                case Removal.Refused:
                    kept.Add(item: version);
                    continue;
                case Removal.Gone:
                    continue;
            }
            if (retained++ < keepVersions) {
                kept.Add(item: version);
                continue;
            }
            switch (Remove(name: version.Name, refused: refused, trash: trash, versions: versions)) {
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
        var referenced = new HashSet<string>(collection: plan.IntentObjects, comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var version in kept) { referenced.UnionWith(other: version.Objects); }
        return new InstallPruneResult(
            DeletedObjects: StoreDirectoryNames.Sum(selector: store => SweepStore(referenced: referenced, refused: refused, root: root, storeName: store, storeObjects: plan.StoreObjects[store])),
            DeletedVersions: deleted,
            KeptVersions: [.. kept.Select(selector: static version => version.Name)],
            Refused: refused,
            RunningVersions: running,
            Skipped: null
        );
    }
    private static bool IsPlainName(string name) => ((name.Length > 0) && (name is not ("." or "..")) && (name.IndexOfAny(anyOf: ['/', '\\', ':', '\0']) < 0));
    // Runs one deletion, recording a failure against the entry instead of ending the pass.
    private static bool Attempt(Action action, string entry, List<string> refused) {
        try {
            action();
            return true;
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or InvalidOperationException)) {
            refused.Add(item: $"{entry}: {error.Message}");
            return false;
        }
    }
    // Whether a process holds the version: Running when one does, Removed when the version could be removed, Gone when
    // it no longer exists, and Refused when it cannot be opened.
    private static Removal Probe(ConfinedDirectory versions, string name, List<string> refused) {
        try {
            using var held = versions.TryOpenChildForRemoval(name: name);

            return ((held is null) ? Removal.Running : Removal.Removed);
        } catch (DirectoryNotFoundException) {
            return Removal.Gone;
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
            refused.Add(item: $"{VersionsDirectoryName}/{name}: {error.Message}");
            return Removal.Refused;
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

        // Only a missing file is absent: an empty or blank pointer is present and names no version, which the caller
        // refuses like any other name that is not a version.
        return reader.ReadToEnd().Trim();
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
        PinHexes(pins: manifest.Payloads.SelectMany(selector: static payload => payload.Files.Select(selector: static file => file.Hash)));
    // An intent whose manifest is absent or fails validation may name any version, so it fails the validation phase.
    private static (string? Version, IReadOnlyList<string> Objects) ReadIntent(ConfinedDirectory root) {
        if (ReadBounded(directory: root, name: "install-intent.json") is not { } bytes) { return (null, []); }
        using var intent = JsonDocument.Parse(utf8Json: bytes);
        var manifest = Valid(manifest: (((intent.RootElement.ValueKind == JsonValueKind.Object) && intent.RootElement.TryGetProperty(propertyName: "manifest", value: out var element))
            ? element.Deserialize<ReleaseManifest>(options: DocumentJsonOptions.Shared)
            : null), what: "the pending install intent");

        return (manifest.Version, ManifestObjects(manifest: manifest).ToArray());
    }
    // A manifest that is absent or fails structural validation names nothing reliable.
    private static ReleaseManifest Valid(ReleaseManifest? manifest, string what) {
        if (manifest is null) { throw new JsonException(message: $"{what} names no manifest"); }
        var errors = ReleaseCanonicalizer.Validate(document: manifest);

        return ((errors.Count == 0) ? manifest : throw new JsonException(message: $"{what} holds a manifest that fails validation: {errors[0]}"));
    }
    // The content pins of every regular file a version holds: what it was staged from.
    private static List<string> ObjectsHeld(ConfinedDirectory version) {
        var hexes = new List<string>();
        // Depth first without recursion: one open directory per level, and no stack, whatever the tree's depth.
        var levels = new Stack<(ConfinedDirectory? Directory, Queue<ConfinedEntry> Remaining)>();

        levels.Push(item: (null, new Queue<ConfinedEntry>(collection: version.Entries())));
        try {
            while (levels.TryPeek(result: out var level)) {
                if (!level.Remaining.TryDequeue(result: out var entry)) {
                    level.Directory?.Dispose();
                    _ = levels.Pop();
                    continue;
                }
                var directory = (level.Directory ?? version);

                if (entry.Kind == ConfinedEntryKind.Directory) {
                    var child = (directory.Child(name: entry.Name) ?? throw new IOException(message: $"'{entry.Name}' vanished"));

                    levels.Push(item: (child, new Queue<ConfinedEntry>()));
                    foreach (var grandchild in child.Entries()) { levels.Peek().Remaining.Enqueue(item: grandchild); }
                } else if (entry.Kind == ConfinedEntryKind.File) {
                    using var stream = (directory.OpenFile(name: entry.Name) ?? throw new IOException(message: $"'{entry.Name}' vanished"));

                    hexes.Add(item: ContentPin.Compute(content: stream).Hex);
                }
                // A link or a special file is never what staging wrote, so no cached object stands for it.
            }
            return hexes;
        } finally {
            foreach (var level in levels) { level.Directory?.Dispose(); }
        }
    }
    // Validates a store's layout and returns the objects every channel manifest directly under it names
    // (<store>/<channel>/manifest.json). A link anywhere on that layout, or a manifest that does not parse or validate,
    // throws.
    private static HashSet<string> ChannelObjects(ConfinedDirectory root, string storeName) {
        var hexes = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        using var store = root.Child(name: storeName);

        if (store is null) { return hexes; }
        using (var objects = store.Child(name: "objects")) {
            using var sha256 = objects?.Child(name: "sha256");
        }
        foreach (var channel in store.Entries()) {
            if (channel.Kind == ConfinedEntryKind.Link) { throw new IOException(message: $"{storeName}/{channel.Name} is a link"); }
            if ((channel.Kind != ConfinedEntryKind.Directory) || (channel.Name == "objects")) { continue; }
            using var directory = (store.Child(name: channel.Name) ?? throw new IOException(message: $"{storeName}/{channel.Name} vanished"));

            if (ReadBounded(directory: directory, name: "manifest.json") is not { } bytes) { continue; }
            hexes.UnionWith(other: ManifestObjects(manifest: Valid(manifest: JsonSerializer.Deserialize<ReleaseManifest>(options: DocumentJsonOptions.Shared, utf8Json: bytes),
                what: $"'{storeName}/{channel.Name}/manifest.json'")));
        }
        return hexes;
    }
    // Sweeps <store>/objects/sha256/<fanout>/<hex>, keeping what the kept versions, the intent and the store's own channel
    // manifests reference.
    private static int SweepStore(ConfinedDirectory root, string storeName, HashSet<string> referenced, IReadOnlySet<string> storeObjects, List<string> refused) {
        ConfinedDirectory? store = null, objects = null, sha256 = null;

        try {
            if (!Attempt(action: () => {
                store = root.Child(name: storeName);
                objects = store?.Child(name: "objects");
                sha256 = objects?.Child(name: "sha256");
            }, entry: storeName, refused: refused) || (sha256 is null)) {
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
                        if ((entry.Kind != ConfinedEntryKind.File) || referenced.Contains(item: entry.Name) || storeObjects.Contains(item: entry.Name) ||
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
}
