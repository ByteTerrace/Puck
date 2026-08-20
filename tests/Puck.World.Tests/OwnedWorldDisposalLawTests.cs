using System.Text.Json.Nodes;

using Xunit;

using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Proves <see cref="WorldOwnedWorlds"/>'s handling of a document it cannot admit: a document-shape refusal
/// leaves the catalog's own glob ONCE and is named once with its own reason, a refusal that can answer differently on
/// the next boot leaves its bytes exactly where they are, and a document that still parses is never touched by the
/// sweep.</summary>
public sealed class OwnedWorldDisposalLawTests {
    private static string[] CatalogFiles(string directory) => [.. Directory
        .GetFiles(
            path: directory,
            searchOption: SearchOption.TopDirectoryOnly,
            searchPattern: $"*{WorldOwnedWorldFileName.Suffix}"
        )
        .Order(comparer: StringComparer.Ordinal)];
    // A second and third admissible owned world, so a sweep across several files is exercised rather than inferred
    // from the single identity the fixture document authors.
    private static void Clone(TempWorldDirectory dir, string source, string id) {
        var node = JsonNode.Parse(json: File.ReadAllText(path: source))!.AsObject();
        var identity = node["identity"]!.AsObject();

        identity["id"] = id;
        identity["name"] = id;
        node["documentId"] = id;

        _ = dir.WriteText(
            name: $"{id}{WorldOwnedWorldFileName.Suffix}",
            text: node.ToJsonString()
        );
    }
    private static WorldOwnedWorlds Open(TempWorldDirectory dir) => new(
        directory: dir.RootPath,
        machineId: Guid.NewGuid(),
        template: Fixtures.BuildDocument()
    );
    private static string[] Populate(TempWorldDirectory dir) {
        var seeded = Open(dir: dir);

        Assert.NotEmpty(collection: seeded.All);
        Assert.Empty(collection: seeded.Discarded);

        var first = CatalogFiles(directory: dir.RootPath)[0];

        Clone(
            dir: dir,
            id: "cobalt",
            source: first
        );
        Clone(
            dir: dir,
            id: "moss",
            source: first
        );

        var files = CatalogFiles(directory: dir.RootPath);

        Assert.Equal(expected: 3, actual: files.Length);
        Assert.Equal(expected: 3, actual: Open(dir: dir).All.Count);

        return files;
    }
    private static string QuarantineDirectory(TempWorldDirectory dir) => Path.Combine(
        path1: dir.RootPath,
        path2: WorldOwnedWorlds.QuarantineDirectoryName
    );
    // A share-exclusive handle is the one way to make File.ReadAllBytes answer "cannot read" without touching the
    // bytes — the environmental refusal class, which a sweep must never act destructively on.
    private static FileStream Lock(string path) => new(
        access: FileAccess.Read,
        mode: FileMode.Open,
        path: path,
        share: FileShare.None
    );
    // Readable but not movable: File.ReadAllBytes shares this handle, while File.Move's own DELETE access does not —
    // the one way to reach the disposal's move arm with a refusal the loader has already JUDGED on the bytes.
    private static FileStream Pin(string path) => new(
        access: FileAccess.Read,
        mode: FileMode.Open,
        path: path,
        share: FileShare.Read
    );
    // The owner-observed shape: an unmapped member inside a WorldCameraProgram, which the strict parse refuses by
    // name before the document ever reaches a validator. Reaching the seat rig through the model's own member names
    // keeps this fixture from degrading into "a document with an extra root key" if views ever loses that member.
    private static void RetireCameraProgram(string path) {
        var node = JsonNode.Parse(json: File.ReadAllText(path: path))!.AsObject();
        var rig = node["views"]!.AsObject()["seatRig"]!.AsObject();

        rig["motion"] = new JsonObject { ["kind"] = "chase" };

        File.WriteAllText(
            contents: node.ToJsonString(),
            path: path
        );
    }

    /// <summary>DENIAL: every document carrying the retired member is disposed of — moved out of the catalog
    /// directory, named once with its own reason, and never parsed into an identity — and the sweep is ONE-TIME: a
    /// later construction over the same directory finds nothing left to discard.</summary>
    [Fact]
    public void RetiredDocumentShape_IsDiscardedOnce_AndNamedWithItsOwnReason() {
        using var dir = new TempWorldDirectory();
        var files = Populate(dir: dir);

        foreach (var path in files) {
            RetireCameraProgram(path: path);
        }

        var swept = Open(dir: dir);

        Assert.Equal(expected: files.Length, actual: swept.Discarded.Count);

        foreach (var entry in swept.Discarded) {
            Assert.True(condition: entry.Moved, userMessage: entry.Reason);
            Assert.Contains(
                actualString: entry.Reason,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "'motion' could not be mapped"
            );
            Assert.True(condition: File.Exists(path: entry.QuarantinePath), userMessage: entry.QuarantinePath);
        }

        // The retired shape never reaches a parsed definition: the catalog that came back is the fresh seed the
        // emptied directory triggers, and every file now in the directory parses clean.
        Assert.NotEmpty(collection: swept.All);

        foreach (var path in CatalogFiles(directory: dir.RootPath)) {
            Assert.True(condition: WorldDefinitionFileSource.TryLoad(
                contentHash: out _,
                definition: out _,
                path: path,
                reason: out var reason
            ), userMessage: reason);
        }

        Assert.Empty(collection: Open(dir: dir).Discarded);
    }
    /// <summary>DISTINCTION: a document that cannot be admitted is discarded on its own terms — the documents that
    /// still parse stay in the catalog, byte-for-byte, and no re-seed runs behind them.</summary>
    [Fact]
    public void OneUnreadableDocument_IsDiscardedWithoutDisturbingTheDocumentsThatParse() {
        using var dir = new TempWorldDirectory();
        var files = Populate(dir: dir);
        var corrupt = files[0];
        var survivors = files[1..].ToDictionary(
            elementSelector: File.ReadAllBytes,
            keySelector: path => path
        );

        File.WriteAllText(
            contents: "{ this is not a document",
            path: corrupt
        );

        var swept = Open(dir: dir);
        var discarded = Assert.Single(collection: swept.Discarded);

        Assert.Equal(expected: Path.GetFileName(path: corrupt), actual: discarded.FileName);
        Assert.True(condition: discarded.Moved, userMessage: discarded.Reason);
        // No re-seed: the catalog was not left empty, so the discarded id is simply absent from it.
        Assert.Equal(expected: survivors.Count, actual: swept.All.Count);
        Assert.False(condition: File.Exists(path: corrupt));

        foreach (var (path, bytes) in survivors) {
            Assert.True(condition: File.Exists(path: path), userMessage: path);
            Assert.Equal(expected: bytes, actual: File.ReadAllBytes(path: path));
        }
    }
    /// <summary>CONTROL: a directory of documents this catalog CAN admit is swept by nothing — no disposal, no
    /// quarantine directory, and every file's bytes survive the construction untouched.</summary>
    [Fact]
    public void ValidOwnedWorlds_LoadUnchanged_AndAreNeverDiscarded() {
        using var dir = new TempWorldDirectory();
        var before = Populate(dir: dir).ToDictionary(
            elementSelector: File.ReadAllBytes,
            keySelector: path => path
        );
        var reopened = Open(dir: dir);

        Assert.Empty(collection: reopened.Discarded);
        Assert.Equal(expected: before.Count, actual: reopened.All.Count);
        Assert.False(condition: Directory.Exists(path: QuarantineDirectory(dir: dir)));

        foreach (var (path, bytes) in before) {
            Assert.Equal(expected: bytes, actual: File.ReadAllBytes(path: path));
        }
        // The catalog that reloaded is the one on disk, not a fresh seed standing in for it: the fixture document
        // authors neither of the hand-placed ids, so a re-seed could not produce them.
        Assert.Contains(collection: reopened.All, filter: identity => (identity.Id == "cobalt"));
        Assert.Contains(collection: reopened.All, filter: identity => (identity.Id == "moss"));
    }
    /// <summary>A discarded document is disposed of, not destroyed: its bytes survive verbatim under the quarantine
    /// directory the entry names, so an operator can still read what was refused.</summary>
    [Fact]
    public void DiscardedDocument_SurvivesInQuarantine_ByteForByte() {
        using var dir = new TempWorldDirectory();
        var target = Populate(dir: dir)[0];

        RetireCameraProgram(path: target);

        var staleBytes = File.ReadAllBytes(path: target);
        var swept = Open(dir: dir);
        var discarded = Assert.Single(collection: swept.Discarded);

        Assert.Equal(expected: staleBytes, actual: File.ReadAllBytes(path: discarded.QuarantinePath));
        Assert.Equal(
            actual: Path.GetDirectoryName(path: discarded.QuarantinePath),
            expected: QuarantineDirectory(dir: dir)
        );
    }
    /// <summary>DENIAL: a document the loader could not READ is not a document the loader has JUDGED — the file
    /// keeps its bytes and its place in the catalog, no quarantine directory is even created, and the documents
    /// beside it load exactly as they would have.</summary>
    [Fact]
    public void UnreadableDocument_IsRefusedWithoutBeingDisposedOf() {
        using var dir = new TempWorldDirectory();
        var files = Populate(dir: dir);
        var target = files[0];
        var before = File.ReadAllBytes(path: target);
        WorldOwnedWorlds swept;

        using (var handle = Lock(path: target)) {
            swept = Open(dir: dir);
        }

        Assert.Empty(collection: swept.Discarded);
        Assert.False(condition: Directory.Exists(path: QuarantineDirectory(dir: dir)));
        Assert.True(condition: File.Exists(path: target), userMessage: target);
        Assert.Equal(expected: before, actual: File.ReadAllBytes(path: target));
        Assert.Equal(expected: (files.Length - 1), actual: swept.All.Count);
    }
    /// <summary>CONTROL for the denial above: the SAME file, refused for a reason that IS a verdict on its bytes, is
    /// disposed of — so the retention proved above rests on the refusal's class, not on the sweep being inert.</summary>
    [Fact]
    public void UnparseableDocument_AtTheSamePath_IsDisposedOf() {
        using var dir = new TempWorldDirectory();
        var target = Populate(dir: dir)[0];

        File.WriteAllText(
            contents: "{ this is not a document",
            path: target
        );

        var swept = Open(dir: dir);
        var discarded = Assert.Single(collection: swept.Discarded);

        Assert.True(condition: discarded.Moved, userMessage: discarded.Reason);
        Assert.False(condition: File.Exists(path: target));
    }
    /// <summary>RETENTION: when every document is unreadable the catalog comes back EMPTY rather than re-seeded —
    /// the seeding pass never writes a fresh default over a catalog path a refused document still occupies, so the
    /// authored bytes survive to the next boot.</summary>
    [Fact]
    public void UnreadableDocuments_AreNeverReSeededOver() {
        using var dir = new TempWorldDirectory();
        var files = Populate(dir: dir);
        var before = files.ToDictionary(
            elementSelector: File.ReadAllBytes,
            keySelector: path => path
        );
        var locks = new List<FileStream>();
        WorldOwnedWorlds swept;

        try {
            foreach (var path in files) {
                locks.Add(item: Lock(path: path));
            }

            swept = Open(dir: dir);
        } finally {
            foreach (var handle in locks) {
                handle.Dispose();
            }
        }

        Assert.Empty(collection: swept.All);
        Assert.Empty(collection: swept.Discarded);
        Assert.Equal(expected: files.Length, actual: CatalogFiles(directory: dir.RootPath).Length);

        foreach (var (path, bytes) in before) {
            Assert.Equal(expected: bytes, actual: File.ReadAllBytes(path: path));
        }
    }
    /// <summary>A directory can occupy a deterministic seed path without appearing in the catalog's file glob. The
    /// seed pass preserves that entry and returns an empty catalog instead of throwing while trying to save through
    /// it; <see cref="WorldOwnedWorlds.BootProfile"/>'s own contract then refuses the empty catalog by name rather
    /// than handing back a null identity.</summary>
    [Fact]
    public void DirectoryAtSeedPath_IsPreservedAndDoesNotCrashConstruction() {
        using var dir = new TempWorldDirectory();
        var template = Fixtures.BuildDocument();
        var seed = Assert.Single(collection: template.PlayerDefaults.Identities);
        var occupied = Path.Combine(
            path1: dir.RootPath,
            path2: WorldOwnedWorldFileName.For(id: seed.Id)
        );

        _ = Directory.CreateDirectory(path: occupied);

        var catalog = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: template
        );

        Assert.Empty(collection: catalog.All);
        Assert.True(condition: Directory.Exists(path: occupied));
        _ = Assert.Throws<InvalidOperationException>(testCode: () => catalog.BootProfile);
    }
    /// <summary>A quarantine name is derived from the catalog name, and the catalog re-seeds a freed name, so the
    /// same name arrives twice carrying different bytes: BOTH copies survive, at distinct paths.</summary>
    [Fact]
    public void QuarantineCollision_PreservesBothCopies() {
        using var dir = new TempWorldDirectory();
        var target = Populate(dir: dir)[0];

        RetireCameraProgram(path: target);

        var firstBytes = File.ReadAllBytes(path: target);
        var first = Assert.Single(collection: Open(dir: dir).Discarded);

        Assert.True(condition: first.Moved, userMessage: first.Reason);

        File.WriteAllText(
            contents: "{ this is not a document either",
            path: target
        );

        var secondBytes = File.ReadAllBytes(path: target);
        var second = Assert.Single(collection: Open(dir: dir).Discarded);

        Assert.True(condition: second.Moved, userMessage: second.Reason);
        Assert.NotEqual(expected: first.QuarantinePath, actual: second.QuarantinePath);
        Assert.Equal(expected: firstBytes, actual: File.ReadAllBytes(path: first.QuarantinePath));
        Assert.Equal(expected: secondBytes, actual: File.ReadAllBytes(path: second.QuarantinePath));
    }
    /// <summary>A directory can occupy a path just as completely as a file. It is skipped by the same suffix walk
    /// rather than turning a recoverable name collision into a failed disposal.</summary>
    [Fact]
    public void QuarantineDirectoryEntryCollision_UsesTheNextSuffix() {
        using var dir = new TempWorldDirectory();
        var target = Populate(dir: dir)[0];

        RetireCameraProgram(path: target);

        var occupied = Path.Combine(
            path1: QuarantineDirectory(dir: dir),
            path2: Path.GetFileName(path: target)
        );

        _ = Directory.CreateDirectory(path: occupied);

        var disposal = Assert.Single(collection: Open(dir: dir).Discarded);

        Assert.True(condition: disposal.Moved, userMessage: disposal.Reason);
        Assert.True(condition: Directory.Exists(path: occupied));
        Assert.EndsWith(
            actualString: disposal.QuarantinePath,
            expectedEndString: $"{Path.GetFileName(path: target)}.2",
            comparisonType: StringComparison.Ordinal
        );
    }
    /// <summary>A disposal whose MOVE fails leaves the document exactly as it was — and the seeding pass that runs
    /// behind an emptied catalog skips the ids whose paths those documents still occupy, so nothing overwrites the
    /// bytes the failed move promised would be named again next boot.</summary>
    [Fact]
    public void FailedMove_LeavesTheBytes_AndNoSeedOverwritesThem() {
        using var dir = new TempWorldDirectory();
        var files = Populate(dir: dir);

        foreach (var path in files) {
            RetireCameraProgram(path: path);
        }

        var before = files.ToDictionary(
            elementSelector: File.ReadAllBytes,
            keySelector: path => path
        );

        // A file standing where the quarantine DIRECTORY must be makes Directory.CreateDirectory and every move
        // fail without changing the source documents.
        File.WriteAllText(
            contents: "occupied",
            path: QuarantineDirectory(dir: dir)
        );

        var swept = Open(dir: dir);

        Assert.Equal(expected: files.Length, actual: swept.Discarded.Count);
        Assert.All(
            action: entry => Assert.False(condition: entry.Moved, userMessage: entry.Reason),
            collection: swept.Discarded
        );
        Assert.Empty(collection: swept.All);
        Assert.Equal(expected: files.Length, actual: CatalogFiles(directory: dir.RootPath).Length);

        foreach (var (path, bytes) in before) {
            Assert.Equal(expected: bytes, actual: File.ReadAllBytes(path: path));
        }
    }
    /// <summary>DENIAL: when <c>File.Move</c> itself fails, BOTH copies survive — the source keeps its bytes in the
    /// catalog directory, and the earlier quarantined copy of the same catalog name keeps its own. The move is
    /// attempted with <c>overwrite: false</c> onto a destination the suffix walk chose, so a failed move can never
    /// leave a half-written or clobbered copy behind: the destination it chose stays absent.</summary>
    [Fact]
    public void FailingFileMove_LeavesTheSourceBytes_AndTheEarlierQuarantinedCopy() {
        using var dir = new TempWorldDirectory();
        var target = Populate(dir: dir)[0];

        RetireCameraProgram(path: target);

        var firstBytes = File.ReadAllBytes(path: target);
        var first = Assert.Single(collection: Open(dir: dir).Discarded);

        Assert.True(condition: first.Moved, userMessage: first.Reason);

        File.WriteAllText(
            contents: "{ this is not a document either",
            path: target
        );

        var secondBytes = File.ReadAllBytes(path: target);
        WorldOwnedWorlds swept;

        using (var pinned = Pin(path: target)) {
            swept = Open(dir: dir);
        }

        var second = Assert.Single(collection: swept.Discarded);

        Assert.False(condition: second.Moved, userMessage: second.Reason);
        Assert.Contains(
            actualString: second.Reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "it could not be moved aside"
        );
        Assert.NotEqual(expected: first.QuarantinePath, actual: second.QuarantinePath);
        Assert.False(condition: File.Exists(path: second.QuarantinePath), userMessage: second.QuarantinePath);
        Assert.Equal(expected: secondBytes, actual: File.ReadAllBytes(path: target));
        Assert.Equal(expected: firstBytes, actual: File.ReadAllBytes(path: first.QuarantinePath));
    }
    /// <summary>CONTROL for the denial above: the SAME second document, with nothing holding it open, IS moved to the
    /// destination the suffix walk chose — so the retention proved above rests on the move failing, not on the
    /// disposal being inert whenever a quarantined copy of that name already exists.</summary>
    [Fact]
    public void MovableSecondCopy_IsQuarantinedBesideTheFirst() {
        using var dir = new TempWorldDirectory();
        var target = Populate(dir: dir)[0];

        RetireCameraProgram(path: target);

        var first = Assert.Single(collection: Open(dir: dir).Discarded);

        Assert.True(condition: first.Moved, userMessage: first.Reason);

        File.WriteAllText(
            contents: "{ this is not a document either",
            path: target
        );

        var secondBytes = File.ReadAllBytes(path: target);
        var second = Assert.Single(collection: Open(dir: dir).Discarded);

        Assert.True(condition: second.Moved, userMessage: second.Reason);
        Assert.False(condition: File.Exists(path: target));
        Assert.Equal(expected: secondBytes, actual: File.ReadAllBytes(path: second.QuarantinePath));
    }
    /// <summary>READ-BACK: a document refused IN PLACE is not only narrated on stderr — it is carried on
    /// <see cref="WorldOwnedWorlds.Refused"/>, naming the file that is still sitting in the catalog directory, so a
    /// session that starts after the boot line scrolls away can still learn it exists. It is never
    /// <see cref="WorldOwnedWorlds.Discarded"/>: nothing was moved.</summary>
    [Fact]
    public void RefusedInPlaceDocument_IsReadBackOnRefused_NotOnDiscarded() {
        using var dir = new TempWorldDirectory();
        var files = Populate(dir: dir);
        var target = files[0];
        WorldOwnedWorlds swept;

        using (var handle = Lock(path: target)) {
            swept = Open(dir: dir);
        }

        var refused = Assert.Single(collection: swept.Refused);

        Assert.Equal(expected: Path.GetFileName(path: target), actual: refused.FileName);
        Assert.Empty(collection: swept.Discarded);
        Assert.DoesNotContain(
            actualString: refused.Reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: dir.RootPath
        );
    }
    /// <summary>CONTROL for the read-back above: the documents that load carry no refusal at all, so the list is a
    /// verdict on what happened rather than a running inventory of the directory.</summary>
    [Fact]
    public void AdmittedDocuments_LeaveTheRefusedListEmpty() {
        using var dir = new TempWorldDirectory();

        _ = Populate(dir: dir);

        var reopened = Open(dir: dir);

        Assert.Empty(collection: reopened.Refused);
        Assert.Empty(collection: reopened.Discarded);
        Assert.NotEmpty(collection: reopened.All);
    }
    /// <summary>The refusal line is grouped by reason and names files, never paths: several documents failing the
    /// same way share ONE group, and the player's state directory never reaches the console through a reason.</summary>
    [Fact]
    public void RefusalNarration_GroupsSiblings_AndCarriesNoAbsolutePath() {
        using var dir = new TempWorldDirectory();
        var files = Populate(dir: dir);
        var locks = new List<FileStream>();
        var originalError = Console.Error;
        using var captured = new StringWriter();

        try {
            foreach (var path in files) {
                locks.Add(item: Lock(path: path));
            }
            Console.SetError(newError: captured);

            _ = Open(dir: dir);
        } finally {
            Console.SetError(newError: originalError);

            foreach (var handle in locks) {
                handle.Dispose();
            }
        }

        var narration = captured.ToString();

        Assert.Contains(
            actualString: narration,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: string.Join(
                separator: ", ",
                values: files.Select(selector: Path.GetFileName)
            )
        );
        Assert.DoesNotContain(
            actualString: narration,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: dir.RootPath
        );
    }
}
