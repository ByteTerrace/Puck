using System.Text.Json.Nodes;

using Xunit;

using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Proves <see cref="WorldOwnedWorlds"/>'s handling of a document it cannot admit: the file leaves the
/// catalog's own glob ONCE, is named once with its own reason, and a document that still parses is never touched by
/// the sweep.</summary>
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
}
