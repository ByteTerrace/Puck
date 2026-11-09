using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Assets;
using Puck.Assets.Documents;
using Puck.Confinement;
using Puck.Launcher.Release;
using Puck.Launcher.Stub;
using Puck.Testing;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Laws over pruning past versions from an install root: every removal acts through handles and never leaves
/// the root, a version a live process holds is never removed, registration waits for the update lease and confirms by
/// identity the directory it was loaded from, retention follows install order, a kept version's objects stay, and an
/// interrupted pass converges.</summary>
public sealed class InstallPruneLawTests : IDisposable {
    private readonly TemporaryDirectory m_directory = new(prefix: "puck-prune-");
    private readonly FileUpdateApplier m_applier = new();

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;
    private ContentAddressedStore Objects => new(root: Path.Combine(path1: Root, path2: "objects"));
    private string Outside => m_directory.PathOf(name: "outside");
    private string Root => m_directory.PathOf(name: "root");
    private string Trash => Path.Combine(path1: Root, path2: InstallPruner.TrashDirectoryName);

    private string Version(string version) => Path.Combine(path1: Root, path2: "versions", path3: version);
    private string Image(string version) => Path.Combine(path1: Version(version: version), path2: "app.dll");
    private string Fanout(string name) => Path.Combine(paths: [Root, "objects", "objects", "sha256", name]);
    private static byte[] Content(string version) => System.Text.Encoding.UTF8.GetBytes(s: $"binary-{version}");
    private static ReleaseManifest ManifestFor(string version) {
        var content = Content(version: version);

        return new ReleaseManifest(App: "puck.world", Channel: "stable", MinimumSupported: null, Notes: null,
            Payloads: [new ReleasePayload(Rid: "win-x64", Files: [new ReleasePayloadFile(Path: "app.dll", Hash: ContentPin.Compute(content: content).ToString(), Size: content.Length)])],
            Revoked: null, Rollout: new ReleaseRollout(Percent: 100), Schema: ReleaseManifest.CurrentSchema,
            Signature: null, StateGeneration: 0, Version: version);
    }
    // Stages a version as the stager does: its object in the cache, its files in versions/<version>/.
    private ContentPin Stage(string version) {
        Directory.CreateDirectory(path: Version(version: version));
        File.WriteAllBytes(path: Image(version: version), bytes: Content(version: version));
        return Objects.Put(content: Content(version: version));
    }
    private ContentPin Install(string version, bool healthy = false) {
        var pin = Stage(version: version);
        using var transaction = InstallTransaction.TryBegin(installRoot: Root)!;
        var applied = m_applier.Apply(manifest: ManifestFor(version: version), rid: "win-x64", transaction: transaction);

        Assert.True(condition: applied.Applied, userMessage: applied.RefusalReason);
        if (healthy) { Assert.True(condition: transaction.RecordHealthy(version: version)); }
        return pin;
    }
    private InstallPruneResult Prune(int keepVersions) {
        var pass = InstallPruner.Prune(installRoot: Root, keepVersions: keepVersions);

        Assert.Null(@object: pass.Skipped);
        return pass;
    }
    private string[] VersionNames() => [.. Directory.GetDirectories(path: Path.Combine(path1: Root, path2: "versions")).Select(selector: Path.GetFileName).Order(comparer: StringComparer.Ordinal)!];
    private string[] Tree() => [.. Directory.EnumerateFileSystemEntries(path: Root, searchPattern: "*", searchOption: SearchOption.AllDirectories)
        .Select(selector: path => Path.GetRelativePath(path: path, relativeTo: Root).Replace(newChar: '/', oldChar: '\\'))
        .Order(comparer: StringComparer.Ordinal)];
    private void WriteOutside() {
        Directory.CreateDirectory(path: Path.Combine(path1: Outside, path2: "nested"));
        File.WriteAllText(path: Path.Combine(path1: Outside, path2: "keep.txt"), contents: "keep");
        File.WriteAllText(path: Path.Combine(path1: Outside, path2: "nested", path3: "deep.txt"), contents: "keep");
    }
    private void AssertOutsideIntact() {
        Assert.True(condition: File.Exists(path: Path.Combine(path1: Outside, path2: "keep.txt")), userMessage: "a file outside the install root was deleted");
        Assert.True(condition: File.Exists(path: Path.Combine(path1: Outside, path2: "nested", path3: "deep.txt")), userMessage: "a file outside the install root was deleted");
    }

    // A held directory is enumerated and emptied through its handle, so replacing its path with a link between the
    // check and the deletion never reaches the link's target: on Windows the replacement is refused while the directory
    // is held, and on Linux it succeeds but the removal still acts on the held directory.
    [Fact]
    public void ReplacingAHeldDirectoryWithALinkNeverRedirectsWhatIsRemovedThroughIt() {
        WriteOutside();
        Directory.CreateDirectory(path: Path.Combine(path1: Trash, path2: "moved", path3: "runtimes"));
        File.WriteAllText(path: Path.Combine(path1: Trash, path2: "moved", path3: "runtimes", path4: "native.dll"), contents: "partial");
        File.WriteAllText(path: Path.Combine(path1: Trash, path2: "loose.txt"), contents: "partial");
        using var root = ConfinedDirectory.OpenAnchor(create: false, path: Root)!;
        using var trash = root.Child(name: InstallPruner.TrashDirectoryName)!;
        var displaced = Path.Combine(path1: Root, path2: "displaced");
        var replaced = false;

        try {
            Directory.Move(destDirName: displaced, sourceDirName: Trash);
            DirectoryLinks.Create(link: Trash, target: Outside);
            replaced = true;
        } catch (IOException) when (OperatingSystem.IsWindows()) {
            // The held handle denies the rename: nobody can replace trash/ while the pass holds it.
        }
        foreach (var entry in trash.Entries()) {
            _ = Record.Exception(testCode: () => trash.DeleteChild(name: entry.Name));
        }
        AssertOutsideIntact();
        Assert.Equal(expected: !OperatingSystem.IsWindows(), actual: replaced);
        Assert.Empty(collection: Directory.EnumerateFileSystemEntries(path: (replaced ? displaced : Trash)));
        if (replaced) { DirectoryLinks.Remove(link: Trash); }
    }
    // A process launched directly from versions/<v>/ waits for update.lock however long a removal holds it, then
    // confirms that versions/<v> is still the directory it was loaded from. On Windows
    // the runtime's open image file already denies moving its directory, so a running version cannot be removed or
    // replaced at all and the registration completes; on Linux the move succeeds and the registration fails the launch.
    [Fact]
    public async Task RegistrationWaitsForTheUpdateLeaseAndRefusesAVersionRemovedOrReplacedMeanwhile() {
        _ = Install(version: "1.0.0");
        _ = Install(version: "1.0.1");
        using var removed = new MappedImage(path: Image(version: "1.0.0"));
        using var replaced = new MappedImage(path: Image(version: "1.0.1"));
        Task<InstallRegistration> removedRegistration, replacedRegistration;
        var moved = false;

        using (var lease = InstallLeases.TryAcquire(fileName: InstallLeases.UpdateLockFileName, installRoot: Root)) {
            Assert.NotNull(@object: lease);
            removedRegistration = Task.Factory.StartNew(cancellationToken: Cancel, creationOptions: TaskCreationOptions.LongRunning,
                function: () => removed.Register(installRoot: Root), scheduler: TaskScheduler.Default);
            replacedRegistration = Task.Factory.StartNew(cancellationToken: Cancel, creationOptions: TaskCreationOptions.LongRunning,
                function: () => replaced.Register(installRoot: Root), scheduler: TaskScheduler.Default);
            await Task.Delay(cancellationToken: Cancel, delay: TimeSpan.FromMilliseconds(milliseconds: 1500));
            Assert.False(condition: removedRegistration.IsCompleted, userMessage: "registration proceeded while a removal held update.lock");
            Assert.False(condition: replacedRegistration.IsCompleted, userMessage: "registration proceeded while a removal held update.lock");
            // What a pass does under the lease: one version leaves versions/, another is replaced by a new directory.
            try {
                Assert.True(condition: InstallPruner.RemoveVersion(installRoot: Root, version: "1.0.0"));
                Directory.Move(destDirName: Path.Combine(path1: Root, path2: "elsewhere"), sourceDirName: Version(version: "1.0.1"));
                _ = Stage(version: "1.0.1");
                moved = true;
            } catch (Exception error) when (((error is IOException or UnauthorizedAccessException) && OperatingSystem.IsWindows())) {
                // The open image pins its directory.
            }
        }
        Assert.Equal(expected: !OperatingSystem.IsWindows(), actual: moved);
        if (!moved) {
            using var first = await removedRegistration;
            using var second = await replacedRegistration;

            Assert.Equal(expected: ["1.0.0", "1.0.1"], actual: new[] { first.Version, second.Version });
            return;
        }
        var removedError = await Assert.ThrowsAsync<InstallRegistrationException>(testCode: () => removedRegistration);
        var replacedError = await Assert.ThrowsAsync<InstallRegistrationException>(testCode: () => replacedRegistration);

        Assert.Contains(expectedSubstring: "versions/1.0.0 was removed", actualString: removedError.Message);
        Assert.Contains(expectedSubstring: "versions/1.0.1 is no longer the directory", actualString: replacedError.Message);
    }
    // Registration recognizes versions/<v> by the identity of the image's parent directory, so any equivalent spelling
    // (an extended-length or differently cased path on Windows, a linked ancestor on Linux) registers and protects it.
    [Fact]
    public void RegistrationRecognizesItsVersionDirectoryWhateverPathSpellsIt() {
        _ = Install(healthy: true, version: "1.0.0");
        _ = Install(healthy: true, version: "1.0.1");
        _ = Install(healthy: true, version: "1.0.2");
        string spelled;

        if (OperatingSystem.IsWindows()) {
            spelled = (@"\\?\" + Path.Combine(path1: Path.GetFullPath(path: Root), path2: "VERSIONS", path3: "1.0.0", path4: "app.dll"));
        } else {
            DirectoryLinks.Create(link: m_directory.PathOf(name: "alias"), target: Root);
            spelled = Path.Combine(paths: [m_directory.PathOf(name: "alias"), "versions", "1.0.0", "app.dll"]);
        }
        using (var image = new MappedImage(path: Image(version: "1.0.0")))
        using (var registration = image.Register(imagePath: spelled, installRoot: Root)) {
            Assert.Equal(expected: "1.0.0", actual: registration.Version);
            var pass = Prune(keepVersions: 0);

            Assert.Equal(expected: ["1.0.0"], actual: pass.RunningVersions);
            Assert.Equal(expected: ["1.0.0", "1.0.1", "1.0.2"], actual: VersionNames());
        }
        Assert.Equal(expected: ["1.0.0"], actual: Prune(keepVersions: 0).DeletedVersions);
        if (!OperatingSystem.IsWindows()) { DirectoryLinks.Remove(link: m_directory.PathOf(name: "alias")); }
    }
    // A kept version without an install record keeps the content pins of the regular files it holds, a link among them
    // included, and the sweep of everything else still runs.
    [Fact]
    public void AKeptVersionWithoutARecordKeepsTheObjectsItHoldsAndTheSweepStillRuns() {
        var unrecorded = Install(healthy: true, version: "1.0.0");
        var current = Install(version: "1.0.1");
        var orphan = Objects.Put(content: "nothing references this"u8);

        WriteOutside();
        DirectoryLinks.Create(link: Path.Combine(path1: Version(version: "1.0.0"), path2: "escape"), target: Outside);
        File.Delete(path: Path.Combine(path1: Version(version: "1.0.0"), path2: InstallRecord.FileName));
        // Its proof of health no longer matches an install record, so it is no rollback target either.
        Assert.Null(@object: InstallSeed.RollbackTarget(installRoot: Root));

        var pass = Prune(keepVersions: 0);

        Assert.Equal(expected: ["1.0.1", "1.0.0"], actual: pass.KeptVersions);
        Assert.Empty(collection: pass.Refused);
        Assert.True(condition: Objects.Contains(pin: unrecorded), userMessage: "the unrecorded kept version lost its object");
        Assert.True(condition: Objects.Contains(pin: current));
        Assert.False(condition: Objects.Contains(pin: orphan), userMessage: "the sweep did not run");
        Assert.Equal(expected: 1, actual: pass.DeletedObjects);
        AssertOutsideIntact();
        DirectoryLinks.Remove(link: Path.Combine(path1: Version(version: "1.0.0"), path2: "escape"));
    }
    [Fact]
    public void LinksAreRefusedAndWhatTheyReachSurvives() {
        WriteOutside();
        File.WriteAllText(path: Path.Combine(path1: Outside, path2: new string(c: 'e', count: 64)), contents: "named like an object");
        foreach (var version in new[] { "1.0.0", "1.0.1", "1.0.2", "1.0.3" }) {
            _ = Install(version: version);
        }
        // A version that is a link, a version that holds one deep inside, an object fan-out and a trash entry that are.
        DirectoryLinks.Create(link: Version(version: "1.0.9"), target: Outside);
        Directory.CreateDirectory(path: Path.Combine(path1: Version(version: "1.0.1"), path2: "runtimes"));
        DirectoryLinks.Create(link: Path.Combine(path1: Version(version: "1.0.1"), path2: "runtimes", path3: "assets"), target: Outside);
        DirectoryLinks.Create(link: Fanout(name: "ee"), target: Outside);
        Directory.CreateDirectory(path: Trash);
        DirectoryLinks.Create(link: Path.Combine(path1: Trash, path2: "escape"), target: Outside);

        // A link among the versions fails the validation phase, so nothing at all is deleted.
        Assert.Contains(expectedSubstring: "is a link", actualString: InstallPruner.Prune(installRoot: Root, keepVersions: 0).Skipped);
        Assert.Equal(expected: ["1.0.0", "1.0.1", "1.0.2", "1.0.3", "1.0.9"], actual: VersionNames());
        Assert.True(condition: Directory.Exists(path: Path.Combine(path1: Trash, path2: "escape")));
        DirectoryLinks.Remove(link: Version(version: "1.0.9"));

        var pass = Prune(keepVersions: 0);

        AssertOutsideIntact();
        Assert.Equal(expected: ["1.0.2", "1.0.0"], actual: pass.DeletedVersions);
        Assert.Equal(expected: ["1.0.1", "1.0.3"], actual: VersionNames());
        Assert.Equal(expected: 3, actual: pass.Refused.Count);
        Assert.True(condition: Directory.Exists(path: Fanout(name: "ee")));
        // A linked trash/ refuses the whole pass.
        foreach (var link in new[] { Fanout(name: "ee"), Path.Combine(path1: Trash, path2: "escape") }) {
            DirectoryLinks.Remove(link: link);
        }
        Directory.Delete(path: Trash);
        DirectoryLinks.Create(link: Trash, target: Outside);
        Assert.Contains(expectedSubstring: "link", actualString: InstallPruner.Prune(installRoot: Root, keepVersions: 0).Skipped);
        AssertOutsideIntact();
        DirectoryLinks.Remove(link: Trash);
        DirectoryLinks.Remove(link: Path.Combine(path1: Version(version: "1.0.1"), path2: "runtimes", path3: "assets"));
    }
    [Fact]
    public void AVersionAProcessHoldsIsKeptUntilItsHolderReleasesIt() {
        foreach (var version in new[] { "1.0.0", "1.0.1", "1.0.2" }) {
            _ = Install(version: version);
        }
        // The stub's hold and a process's own registration. Neither opens the version's files, as on a file system
        // that lets open files be deleted; only the held directory protects it.
        using (var anchor = ConfinedDirectory.OpenAnchor(create: false, path: Root)!)
        using (var stub = StubInstall.HoldVersion(root: anchor, version: "1.0.0"))
        using (var image = new MappedImage(path: Image(version: "1.0.1")))
        using (var application = image.Register(installRoot: Root)) {
            Assert.Equal(expected: "1.0.1", actual: application.Version);
            var pass = Prune(keepVersions: 0);

            Assert.Equal(expected: ["1.0.1", "1.0.0"], actual: pass.RunningVersions);
            Assert.Equal(expected: ["1.0.2", "1.0.1", "1.0.0"], actual: pass.KeptVersions);
            // Staging over a held version is refused, as a pass refuses it.
            Assert.Throws<IOException>(testCode: () => InstallPruner.RemoveVersion(installRoot: Root, version: "1.0.0"));
        }
        Assert.Equal(expected: ["1.0.1", "1.0.0"], actual: Prune(keepVersions: 0).DeletedVersions);
        Assert.Equal(expected: ["1.0.2"], actual: VersionNames());
        // A process outside the install registers as nothing and holds nothing.
        Directory.CreateDirectory(path: Outside);
        File.WriteAllText(path: Path.Combine(path1: Outside, path2: "app.dll"), contents: "elsewhere");
        using var outside = new MappedImage(path: Path.Combine(path1: Outside, path2: "app.dll"));
        using var unregistered = outside.Register(installRoot: Root);

        Assert.Null(@object: unregistered.Version);
    }
    [Fact]
    public void APendingInstallKeepsItsVersionAndObjects_AndAnUnreadableIntentSkipsThePass() {
        _ = Install(healthy: true, version: "1.0.0");
        _ = Install(healthy: true, version: "1.0.1");
        var pending = Stage(version: "1.0.2");
        var intent = new { Manifest = ManifestFor(version: "1.0.2"), VerifiedAt = DateTimeOffset.UnixEpoch, InstalledVersion = "1.0.1", Rid = "win-x64" };

        File.WriteAllText(path: Path.Combine(path1: Root, path2: "install-intent.json"), contents: JsonSerializer.Serialize(value: intent, options: DocumentJsonOptions.Shared));
        _ = Prune(keepVersions: 0);
        Assert.Equal(expected: ["1.0.0", "1.0.1", "1.0.2"], actual: VersionNames());
        Assert.True(condition: Objects.Contains(pin: pending));
        // An intent that cannot be read may name any version, so nothing is pruned.
        _ = Stage(version: "1.0.5");
        File.WriteAllText(path: Path.Combine(path1: Root, path2: "install-intent.json"), contents: "{}");
        Assert.Contains(expectedSubstring: "names no manifest", actualString: InstallPruner.Prune(installRoot: Root, keepVersions: 0).Skipped);
        Assert.Equal(expected: ["1.0.0", "1.0.1", "1.0.2", "1.0.5"], actual: VersionNames());
    }
    [Fact]
    public void RetentionRanksByInstallOrderNotVersionPrecedence() {
        _ = Install(healthy: true, version: "0.0.0-local.1");
        _ = Install(version: "0.0.0-local.9");
        // local.9 never proves healthy: the stub rejects its payload and rolls back to local.1, and later builds install
        // over it. The rejected install takes none of the allowance's places.
        Assert.Equal(expected: "0.0.0-local.9", actual: InstallSeed.Launch(installRoot: Root, maxAttempts: 1).Version);
        Assert.Equal(expected: "0.0.0-local.1", actual: InstallSeed.Launch(installRoot: Root, maxAttempts: 1).Version);
        var newest = Install(version: "0.0.0-local.10");

        _ = Install(version: "0.0.0-local.2");
        _ = Prune(keepVersions: 1);

        Assert.Equal(expected: ["0.0.0-local.1", "0.0.0-local.10", "0.0.0-local.2"], actual: VersionNames());
        Assert.True(condition: Objects.Contains(pin: newest));
        Assert.False(condition: Objects.Contains(pin: ContentPin.Compute(content: Content(version: "0.0.0-local.9"))));
    }
    [Fact]
    public async Task PruningNeverInterleavesWithAPublicationOrAnUpdate() {
        _ = Install(version: "1.0.0");
        _ = Install(version: "1.0.1");
        // A local publication has written its objects and manifest; its install has not happened yet.
        var sourceRoot = Path.Combine(path1: Root, path2: InstallPruner.LocalSourceDirectoryName);
        var build = m_directory.PathOf(name: "build");

        Directory.CreateDirectory(path: build);
        File.WriteAllText(path: Path.Combine(path1: build, path2: "app.dll"), contents: "published");
        _ = new DirectoryReleaseSource(root: sourceRoot).Publish(input: build, rid: "win-x64", manifest: ManifestFor(version: "1.0.2"));
        var published = ContentPin.Compute(content: "published"u8);
        var stale = new ContentAddressedStore(root: sourceRoot).Put(content: "superseded"u8);
        Task<FileStream> nextPublication;

        using (await InstallLeases.AcquireAsync(cancellationToken: Cancel, fileName: InstallLeases.PublicationLockFileName, installRoot: Root)) {
            Assert.Contains(expectedSubstring: InstallLeases.PublicationLockFileName, actualString: InstallPruner.Prune(installRoot: Root, keepVersions: 0).Skipped);
            // A second publisher waits for the first.
            nextPublication = InstallLeases.AcquireAsync(cancellationToken: Cancel, fileName: InstallLeases.PublicationLockFileName, installRoot: Root);
            await Task.Delay(cancellationToken: Cancel, millisecondsDelay: 300);
            Assert.False(condition: nextPublication.IsCompleted);
        }
        (await nextPublication).Dispose();
        using (InstallLeases.TryAcquire(fileName: InstallLeases.UpdateLockFileName, installRoot: Root)) {
            Assert.Contains(expectedSubstring: InstallLeases.UpdateLockFileName, actualString: InstallPruner.Prune(installRoot: Root, keepVersions: 0).Skipped);
        }
        Assert.Equal(expected: ["1.0.0", "1.0.1"], actual: VersionNames());
        _ = Prune(keepVersions: 0);
        Assert.Equal(expected: ["1.0.1"], actual: VersionNames());
        Assert.True(condition: File.Exists(path: ContentAddressedStore.ObjectPath(pin: published, root: sourceRoot)));
        Assert.False(condition: File.Exists(path: ContentAddressedStore.ObjectPath(pin: stale, root: sourceRoot)));
    }
    [Fact]
    public void AnInterruptedPassIsCompletedByTheNextAndAPrunedTreeIsAFixedPoint() {
        foreach (var version in new[] { "1.0.0", "1.0.1", "1.0.2", "1.0.3" }) {
            _ = Install(version: version);
        }
        // What an interrupted pass leaves: one version already moved out whole, another half deleted, and an object
        // fan-out emptied before its directory was removed.
        Directory.CreateDirectory(path: Trash);
        Directory.Move(sourceDirName: Version(version: "1.0.0"), destDirName: Path.Combine(path1: Trash, path2: "moved"));
        Directory.CreateDirectory(path: Path.Combine(path1: Trash, path2: "half", path3: "runtimes", path4: "win-x64"));
        File.WriteAllText(path: Path.Combine(paths: [Trash, "half", "runtimes", "win-x64", "native.dll"]), contents: "partial");
        var readOnly = Path.Combine(path1: Version(version: "1.0.1"), path2: "readonly.dat");

        File.WriteAllText(contents: "read only", path: readOnly);
        File.SetAttributes(fileAttributes: FileAttributes.ReadOnly, path: readOnly);
        Directory.CreateDirectory(path: Fanout(name: "00"));

        _ = Prune(keepVersions: 1);

        Assert.Equal(expected: ["1.0.2", "1.0.3"], actual: VersionNames());
        Assert.Empty(collection: Directory.EnumerateFileSystemEntries(path: Trash));
        Assert.False(condition: Directory.Exists(path: Fanout(name: "00")));
        var pruned = Tree();
        var again = Prune(keepVersions: 1);

        Assert.Empty(collection: again.DeletedVersions);
        Assert.Equal(expected: 0, actual: again.DeletedObjects);
        Assert.Equal(expected: pruned, actual: Tree());
    }
    // Metadata that cannot be read or does not validate keeps what it might protect: an install record that cannot be
    // read, a channel manifest that fails validation, and an intent whose manifest fails validation each skip the whole
    // pass, and a record with an invalid pin counts as no record, so its version is kept with the content pins of its
    // files.
    [Fact]
    public void MetadataThatCannotBeReadOrValidatedKeepsWhatItMightProtect() {
        var older = Install(healthy: true, version: "0.9.0");
        var lastGood = Install(healthy: true, version: "1.0.0");
        var current = Install(version: "1.0.1");
        var orphan = Objects.Put(content: "nothing references this"u8);
        var sourceRoot = Path.Combine(path1: Root, path2: InstallPruner.LocalSourceDirectoryName);
        var stale = new ContentAddressedStore(root: sourceRoot).Put(content: "named by no valid manifest"u8);
        var olderRecord = Path.Combine(path1: Version(version: "0.9.0"), path2: InstallRecord.FileName);

        File.WriteAllText(path: Path.Combine(path1: Version(version: "1.0.0"), path2: InstallRecord.FileName), contents: """{"sequence":2,"files":["invalid"]}""");
        Directory.CreateDirectory(path: Path.Combine(path1: sourceRoot, path2: "development"));
        File.WriteAllText(path: Path.Combine(path1: sourceRoot, path2: "development", path3: "manifest.json"), contents: "{}");
        string? unreadable;

        if (OperatingSystem.IsWindows()) {
            using (new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: olderRecord, share: FileShare.None)) {
                unreadable = InstallPruner.Prune(installRoot: Root, keepVersions: 0).Skipped;
            }
        } else {
            File.SetUnixFileMode(mode: UnixFileMode.None, path: olderRecord);
            unreadable = InstallPruner.Prune(installRoot: Root, keepVersions: 0).Skipped;
            File.SetUnixFileMode(mode: UnixFileMode.UserRead | UnixFileMode.UserWrite, path: olderRecord);
        }
        Assert.Contains(actualString: unreadable, expectedSubstring: "nothing was deleted");
        Assert.Contains(expectedSubstring: "fails validation", actualString: InstallPruner.Prune(installRoot: Root, keepVersions: 0).Skipped);
        Assert.Equal(expected: ["0.9.0", "1.0.0", "1.0.1"], actual: VersionNames());
        Assert.True(condition: (Objects.Contains(pin: older) && Objects.Contains(pin: orphan)), userMessage: "a skipped pass deleted an object");
        Assert.True(condition: File.Exists(path: ContentAddressedStore.ObjectPath(pin: stale, root: sourceRoot)));
        File.Delete(path: Path.Combine(path1: sourceRoot, path2: "development", path3: "manifest.json"));

        var readable = Prune(keepVersions: 0);

        // 1.0.0's proof no longer matches a valid record, so proven 0.9.0 is the rollback target and stays.
        Assert.Empty(collection: readable.DeletedVersions);
        Assert.True(condition: Objects.Contains(pin: older));
        Assert.True(condition: Objects.Contains(pin: lastGood), userMessage: "a record with an invalid pin lost its version's object");
        Assert.True(condition: Objects.Contains(pin: current));
        Assert.False(condition: Objects.Contains(pin: orphan));
        File.WriteAllText(path: Path.Combine(path1: Root, path2: "install-intent.json"), contents: """{"manifest":{"version":"1.0.1"}}""");
        Assert.Contains(expectedSubstring: "fails validation", actualString: InstallPruner.Prune(installRoot: Root, keepVersions: 0).Skipped);
    }
    // A running version is kept for its process and never takes one of the allowance's places, so the allowance still
    // keeps the newest others.
    [Fact]
    public void ARunningVersionDoesNotTakeAPlaceTheAllowanceKeeps() {
        _ = Install(healthy: true, version: "1.0.0");
        _ = Install(healthy: true, version: "1.0.1");
        _ = Install(healthy: true, version: "1.0.2");
        _ = Install(version: "1.0.3");
        Assert.Equal(expected: "1.0.2", actual: InstallSeed.RollbackTarget(installRoot: Root));
        using (var anchor = ConfinedDirectory.OpenAnchor(create: false, path: Root)!)
        using (StubInstall.HoldVersion(root: anchor, version: "1.0.1")) {
            var pass = Prune(keepVersions: 1);

            Assert.Equal(expected: ["1.0.1"], actual: pass.RunningVersions);
            Assert.Empty(collection: pass.DeletedVersions);
        }
        Assert.Equal(expected: ["1.0.0"], actual: Prune(keepVersions: 1).DeletedVersions);
    }
    // A tree of any depth is scanned, hashed and removed without recursion: on a thread whose stack holds few frames,
    // versions and a trash/ leftover 900 levels deep are still pruned. The depth stays under Linux's default limit of
    // 1024 descriptors (one is open per level) and within what the run directory's own recursive cleanup can remove if
    // the law fails part way.
    [Fact]
    public void TreesOfAnyDepthArePrunedWithoutRecursion() {
        const int Depth = 900;

        _ = Install(version: "0.9.0");
        var unrecorded = Install(healthy: true, version: "1.0.0");

        _ = Install(version: "1.0.1");
        File.Delete(path: Path.Combine(path1: Version(version: "1.0.0"), path2: InstallRecord.FileName));
        Directory.CreateDirectory(path: Path.Combine(path1: Trash, path2: "leftover"));
        using (var root = ConfinedDirectory.OpenAnchor(create: false, path: Root)!) {
            foreach (var start in new[] { "versions/0.9.0", "versions/1.0.0", "trash/leftover" }) {
                var current = root;

                foreach (var segment in start.Split(separator: '/')) { current = Owned(current: current, next: current.Child(name: segment)!, root: root); }
                for (var level = 0; (level < Depth); level++) { current = Owned(current: current, next: current.Child(create: true, name: "d")!, root: root); }
                using (var leaf = current.OpenFile(create: true, name: "leaf.dat")!) { leaf.Write(buffer: "deep"u8); }
                current.Dispose();
            }
        }
        InstallPruneResult? pass = null;
        Exception? error = null;
        var pruning = new Thread(maxStackSize: (64 * 1024), start: () => {
            try {
                pass = InstallPruner.Prune(installRoot: Root, keepVersions: 0);
            } catch (Exception thrown) {
                error = thrown;
            }
        });

        pruning.Start();
        pruning.Join();
        Assert.Null(@object: error);
        Assert.Null(@object: pass!.Skipped);
        Assert.Empty(collection: pass.Refused);
        Assert.Equal(expected: ["0.9.0"], actual: pass.DeletedVersions);
        Assert.Empty(collection: Directory.EnumerateFileSystemEntries(path: Trash));
        Assert.True(condition: Objects.Contains(pin: unrecorded));
        // The kept version's deep tree goes the same way, so the run directory's own cleanup never walks it.
        using var cleanup = ConfinedDirectory.OpenAnchor(create: false, path: Version(version: "1.0.0"))!;

        Assert.True(condition: cleanup.DeleteChild(name: "d"));
    }
    // A release record that does not validate, names no current version, or names one that is not a version directory
    // skips the whole pass before anything, a trash/ leftover included, is deleted.
    [Fact]
    public void AReleaseRecordThatCannotBeTrustedSkipsTheWholePass() {
        _ = Install(version: "1.0.0");
        _ = Install(version: "1.0.1");
        Directory.CreateDirectory(path: Path.Combine(path1: Trash, path2: "leftover"));
        var policy = Path.Combine(path1: Root, path2: InstallLayout.StateDirectoryName, path3: InstallLayout.ReleasePolicyFileName);
        var valid = File.ReadAllText(path: policy);

        File.WriteAllText(path: policy, contents: valid.Replace(oldValue: "\"current\": \"1.0.1\"", newValue: "\"current\": \"../1.0.1\"", comparisonType: StringComparison.Ordinal));
        Assert.Contains(expectedSubstring: "nothing was deleted", actualString: InstallPruner.Prune(installRoot: Root, keepVersions: 0).Skipped);
        Assert.Equal(expected: ["1.0.0", "1.0.1"], actual: VersionNames());
        Assert.True(condition: Directory.Exists(path: Path.Combine(path1: Trash, path2: "leftover")), userMessage: "a skipped pass deleted a trash/ entry");
        File.WriteAllText(path: policy, contents: valid.Replace(oldValue: "\"current\": \"1.0.1\"", newValue: "\"current\": \"1.0.9\"", comparisonType: StringComparison.Ordinal));
        Assert.Contains(expectedSubstring: "is not a version directory", actualString: InstallPruner.Prune(installRoot: Root, keepVersions: 0).Skipped);
        File.Delete(path: policy);
        Assert.Contains(expectedSubstring: "names no current version", actualString: InstallPruner.Prune(installRoot: Root, keepVersions: 0).Skipped);
        Assert.Equal(expected: ["1.0.0", "1.0.1"], actual: VersionNames());
    }
    // A record that does not validate gives its version no rank, so the version is kept rather than ranked below older
    // recorded ones.
    [Fact]
    public void ACorruptRecordKeepsItsVersion() {
        _ = Install(version: "1.0.0");
        var corrupted = Install(version: "1.0.1");

        _ = Install(version: "1.0.2");
        File.WriteAllText(path: Path.Combine(path1: Version(version: "1.0.1"), path2: InstallRecord.FileName), contents: "{ not json");

        var pass = Prune(keepVersions: 1);

        Assert.Empty(collection: pass.DeletedVersions);
        Assert.Equal(expected: ["1.0.0", "1.0.1", "1.0.2"], actual: VersionNames());
        Assert.True(condition: Objects.Contains(pin: corrupted));
    }
    // A record that names no files is no inventory: its version keeps the content pins of the files it holds.
    [Fact]
    public void AnEmptyInventoryCountsAsNoRecord() {
        var current = Install(version: "1.0.0");
        var orphan = Objects.Put(content: "nothing references this"u8);

        File.WriteAllText(path: Path.Combine(path1: Version(version: "1.0.0"), path2: InstallRecord.FileName), contents: """{"sequence":1,"files":[]}""");

        var pass = Prune(keepVersions: 0);

        Assert.True(condition: Objects.Contains(pin: current), userMessage: "an empty inventory let the sweep take the version's object");
        Assert.False(condition: Objects.Contains(pin: orphan));
        Assert.Equal(expected: 1, actual: pass.DeletedObjects);
    }
    // A channel directory that is a link fails the validation phase instead of being skipped, so the objects a manifest
    // behind it names are never swept.
    [Fact]
    public void ALinkedChannelDirectorySkipsTheWholePass() {
        _ = Install(version: "1.0.0");
        var sourceRoot = Path.Combine(path1: Root, path2: InstallPruner.LocalSourceDirectoryName);
        var build = m_directory.PathOf(name: "build");

        Directory.CreateDirectory(path: build);
        File.WriteAllText(path: Path.Combine(path1: build, path2: "app.dll"), contents: "published");
        _ = new DirectoryReleaseSource(root: Outside).Publish(input: build, manifest: ManifestFor(version: "1.0.1"), rid: "win-x64");
        var published = new ContentAddressedStore(root: sourceRoot).Put(content: "published"u8);

        DirectoryLinks.Create(link: Path.Combine(path1: sourceRoot, path2: "stable"), target: Path.Combine(path1: Outside, path2: "stable"));
        Assert.Contains(expectedSubstring: "is a link", actualString: InstallPruner.Prune(installRoot: Root, keepVersions: 0).Skipped);
        Assert.True(condition: File.Exists(path: ContentAddressedStore.ObjectPath(pin: published, root: sourceRoot)));
        DirectoryLinks.Remove(link: Path.Combine(path1: sourceRoot, path2: "stable"));
    }
    // A channel manifest whose file has no path fails validation, and the pass reports it instead of throwing into its
    // caller's start or health check.
    [Fact]
    public void AManifestFileWithoutAPathSkipsThePassWithoutThrowing() {
        _ = Install(version: "1.0.0");
        var manifest = JsonNode.Parse(json: JsonSerializer.Serialize(options: DocumentJsonOptions.Shared, value: ManifestFor(version: "1.0.1")))!;
        var channel = Path.Combine(path1: Root, path2: InstallPruner.LocalSourceDirectoryName, path3: "stable");

        _ = manifest["payloads"]![0]!["files"]![0]!.AsObject().Remove(propertyName: "path");
        Directory.CreateDirectory(path: channel);
        File.WriteAllText(path: Path.Combine(path1: channel, path2: "manifest.json"), contents: manifest.ToJsonString());

        Assert.Contains(expectedSubstring: "nothing was deleted", actualString: InstallPruner.Prune(installRoot: Root, keepVersions: 0).Skipped);
    }

    // Hands the walk to the next directory, closing the one it leaves unless that is the root.
    private static ConfinedDirectory Owned(ConfinedDirectory current, ConfinedDirectory next, ConfinedDirectory root) {
        if (!ReferenceEquals(objA: current, objB: root)) { current.Dispose(); }
        return next;
    }

    public void Dispose() => m_directory.Dispose();
}
