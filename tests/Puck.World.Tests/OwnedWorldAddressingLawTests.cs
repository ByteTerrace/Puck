using System.Text.Json.Nodes;

using Xunit;

using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Proves the rule that makes an owned-world id name exactly one storage location: ids are unique IGNORING
/// CASE, because <see cref="WorldOwnedWorldFileName"/> maps injectively into file-name strings while the filesystem
/// under the catalog resolves those strings case-insensitively. The authored seed list refuses a case-variant pair,
/// the catalog admits the file its id addresses whatever case that file's name carries, and the two doors that mint a
/// NEW id — <see cref="WorldOwnedWorlds.Create"/> and <see cref="WorldOwnedWorlds.ReplaceFromSync"/> — refuse rather
/// than write over an entry already sitting at that location.</summary>
public sealed class OwnedWorldAddressingLawTests {
    private static string[] CatalogFiles(TempWorldDirectory dir) => [.. Directory
        .GetFiles(
            path: dir.RootPath,
            searchOption: SearchOption.TopDirectoryOnly,
            searchPattern: $"*{WorldOwnedWorldFileName.Suffix}"
        )
        .Order(comparer: StringComparer.Ordinal)];
    // Whether the directory holding this catalog resolves names case-insensitively. The storage-location half of the
    // rule is a property of the filesystem, not of the catalog, so the assertions that rest on it are asked only
    // where it holds; every other assertion in these laws runs unconditionally.
    private static bool CaseInsensitive(TempWorldDirectory dir) {
        var probe = dir.WriteText(
            name: "case-probe.tmp",
            text: "probe"
        );
        var insensitive = File.Exists(path: Path.Combine(
            path1: dir.RootPath,
            path2: "CASE-PROBE.TMP"
        ));

        File.Delete(path: probe);

        return insensitive;
    }
    private static WorldOwnedWorlds Open(TempWorldDirectory dir) => new(
        directory: dir.RootPath,
        machineId: Guid.NewGuid(),
        template: Fixtures.BuildDocument()
    );
    // The seeded document re-declared under a chosen id and written under a chosen file name, so a law can put the
    // two deliberately out of step.
    private static string Rewrite(TempWorldDirectory dir, string source, string id, string fileName) {
        var node = JsonNode.Parse(json: File.ReadAllText(path: source))!.AsObject();
        var identity = node["identity"]!.AsObject();

        identity["id"] = id;
        identity["name"] = id;
        node["documentId"] = id;

        return dir.WriteText(
            name: fileName,
            text: node.ToJsonString()
        );
    }
    private static string Seed(TempWorldDirectory dir) {
        var seeded = Open(dir: dir);

        Assert.NotEmpty(collection: seeded.All);

        return Assert.Single(collection: CatalogFiles(dir: dir));
    }
    private static WorldDefinition WithSeeds(params WorldIdentitySeed[] seeds) {
        var template = Fixtures.BuildDocument();

        return (template with { PlayerDefaultsRaw = (template.PlayerDefaults with { IdentitiesRaw = seeds }) });
    }

    /// <summary>An authored seed list may not carry two ids differing only in case: both address one owned-world
    /// file, so one of the two identities would silently not exist. The control is the identical list with the second
    /// id spelled differently rather than merely re-cased.</summary>
    [Fact]
    public void AuthoredSeedIds_AreUniqueIgnoringCase() => Laws.RefusalWithControl(
        lawId: "owned-world.seed-ids-unique-ignoring-case",
        deniedOutcome: () => WorldDefinitionValidator.TryValidate(
            definition: WithSeeds(
                new WorldIdentitySeed(Id: SafeName.Parse(candidate: "amber"), Name: "amber", Color: "#ED8530"),
                new WorldIdentitySeed(Id: SafeName.Parse(candidate: "Amber"), Name: "amberling", Color: "#112233")
            ),
            neighbours: null,
            reason: out _
        ),
        controlOutcome: () => WorldDefinitionValidator.TryValidate(
            definition: WithSeeds(
                new WorldIdentitySeed(Id: SafeName.Parse(candidate: "amber"), Name: "amber", Color: "#ED8530"),
                new WorldIdentitySeed(Id: SafeName.Parse(candidate: "cobalt"), Name: "amberling", Color: "#112233")
            ),
            neighbours: null,
            reason: out _
        )
    );
    /// <summary>The refusal above names the id and the rule, so an author reads why two spellings collide rather than
    /// hunting for a duplicate that is not literally one.</summary>
    [Fact]
    public void CaseVariantSeedRefusal_NamesTheIdAndTheRule() {
        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: WithSeeds(
                new WorldIdentitySeed(Id: SafeName.Parse(candidate: "amber"), Name: "amber", Color: "#ED8530"),
                new WorldIdentitySeed(Id: SafeName.Parse(candidate: "Amber"), Name: "amberling", Color: "#112233")
            ),
            neighbours: null,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "'Amber' is duplicated"
        );
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "unique ignoring case"
        );
    }
    /// <summary>A catalog file whose name differs from its declared id ONLY in case is the file that id addresses, so
    /// it is admitted — not refused, and never re-seeded over. The file keeps the name it carries: a save writes
    /// through the id's own spelling, which the filesystem resolves onto the existing entry.</summary>
    [Fact]
    public void CaseRenamedCatalogFile_IsAdmitted_AndKeepsItsOneStorageLocation() {
        using var dir = new TempWorldDirectory();
        var seeded = Seed(dir: dir);
        var insensitive = CaseInsensitive(dir: dir);
        var name = Path.GetFileName(path: seeded);
        // Only the id half is re-cased: the suffix keeps its spelling so the catalog's own '*.world.json' glob still
        // enumerates the file on a case-sensitive filesystem, where the glob is case-sensitive too.
        var renamed = Path.Combine(
            path1: dir.RootPath,
            path2: $"{char.ToUpperInvariant(c: name[0])}{name[1..]}"
        );
        var bytes = File.ReadAllBytes(path: seeded);

        File.Delete(path: seeded);
        File.WriteAllBytes(
            bytes: bytes,
            path: renamed
        );

        var catalog = Open(dir: dir);
        var identity = Assert.Single(collection: catalog.All);

        Assert.Empty(collection: catalog.Refused);
        Assert.Empty(collection: catalog.Discarded);
        Assert.Equal(expected: "amber", actual: identity.Id);
        Assert.NotNull(@object: catalog.FindById(id: "AMBER"));

        catalog.Save();

        // The addressing claim itself: after a save through the id's own spelling, the directory still holds ONE
        // catalog file, still under the name it carried.
        if (insensitive) {
            var after = Assert.Single(collection: CatalogFiles(dir: dir));

            Assert.Equal(
                actual: Path.GetFileName(path: after),
                comparer: StringComparer.Ordinal,
                expected: Path.GetFileName(path: renamed)
            );
            Assert.Single(collection: Open(dir: dir).All);
        }
    }
    /// <summary>A file whose name is not the one its declared id maps to — beyond case — cannot be addressed: it is
    /// refused and left exactly where it is. The control is the identical document written under the name its own id
    /// maps to.</summary>
    [Fact]
    public void CatalogFileName_MustBeTheNameItsIdMapsTo() {
        using var dir = new TempWorldDirectory();
        var seeded = Seed(dir: dir);

        Laws.RefusalWithControl(
            lawId: "owned-world.file-name-is-its-id",
            deniedOutcome: () => {
                var stray = Rewrite(
                    dir: dir,
                    fileName: $"stranger{WorldOwnedWorldFileName.Suffix}",
                    id: "cobalt",
                    source: seeded
                );
                var before = File.ReadAllBytes(path: stray);
                var catalog = Open(dir: dir);
                var refused = Assert.Single(collection: catalog.Refused);

                Assert.Equal(expected: $"stranger{WorldOwnedWorldFileName.Suffix}", actual: refused.FileName);
                Assert.Contains(
                    actualString: refused.Reason,
                    comparisonType: StringComparison.Ordinal,
                    expectedSubstring: "a name no file in this directory carries"
                );
                Assert.Equal(expected: before, actual: File.ReadAllBytes(path: stray));
                File.Delete(path: stray);

                return (catalog.FindById(id: "cobalt") is not null);
            },
            controlOutcome: () => {
                _ = Rewrite(
                    dir: dir,
                    fileName: $"cobalt{WorldOwnedWorldFileName.Suffix}",
                    id: "cobalt",
                    source: seeded
                );

                var catalog = Open(dir: dir);

                Assert.Empty(collection: catalog.Refused);

                return (catalog.FindById(id: "cobalt") is not null);
            }
        );
    }
    /// <summary>The refusal's discriminator reports what it checked: when another file in the directory really does
    /// carry the addressed name, the message says so rather than claiming the name is unheld.</summary>
    [Fact]
    public void MismatchRefusal_NamesTheFileThatHoldsTheAddressedName() {
        using var dir = new TempWorldDirectory();
        var seeded = Seed(dir: dir);

        _ = Rewrite(
            dir: dir,
            fileName: $"stranger{WorldOwnedWorldFileName.Suffix}",
            id: "amber",
            source: seeded
        );

        var refused = Assert.Single(collection: Open(dir: dir).Refused);

        Assert.Contains(
            actualString: refused.Reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "the name another file in this directory carries"
        );
    }
    /// <summary>An entry this boot did not admit still occupies its id's catalog path, so <c>identity.create</c>
    /// refuses that id BY NAME instead of saving over the bytes the refusal left there. The control is the same verb
    /// against an id whose path is free.</summary>
    [Fact]
    public void Create_RefusesAnIdWhoseCatalogPathIsOccupied() {
        using var dir = new TempWorldDirectory();
        var seeded = Seed(dir: dir);
        var before = File.ReadAllBytes(path: seeded);
        WorldOwnedWorlds catalog;

        // Unreadable at load: the catalog comes back EMPTY (no in-memory id to collide with) while the bytes stay on
        // disk — the one arrangement in which a create can reach an occupied path at all.
        using (var handle = new FileStream(
            access: FileAccess.Read,
            mode: FileMode.Open,
            path: seeded,
            share: FileShare.None
        )) {
            catalog = Open(dir: dir);
        }

        Assert.Empty(collection: catalog.All);
        Assert.NotEmpty(collection: catalog.Refused);
        Laws.RefusalWithControl(
            lawId: "owned-world.create-never-writes-over-an-occupied-path",
            deniedOutcome: () => {
                var created = catalog.Create(
                    colorHex: "#ED8530",
                    name: SafeName.Parse(candidate: "amber"),
                    reason: out var reason
                );

                Assert.Contains(
                    actualString: reason,
                    comparisonType: StringComparison.Ordinal,
                    expectedSubstring: "is already occupied by an entry this boot did not admit"
                );
                Assert.Equal(expected: before, actual: File.ReadAllBytes(path: seeded));

                return (created is not null);
            },
            controlOutcome: () => (catalog.Create(
                colorHex: "#ED8530",
                name: SafeName.Parse(candidate: "cobalt"),
                reason: out _
            ) is not null)
        );
    }
    /// <summary>A pulled cloud copy whose id differs from a local id in case only would adopt onto that local world's
    /// one file, so adoption refuses BY NAME. The control is the same document under the local spelling, which
    /// replaces the local copy as an ordinary pull does.</summary>
    [Fact]
    public void SyncAdoption_RefusesAnIdCollidingInCaseOnly() {
        using var dir = new TempWorldDirectory();
        var seeded = Seed(dir: dir);
        var catalog = Open(dir: dir);

        Assert.NotNull(@object: catalog.FindById(id: "amber"));
        Laws.RefusalWithControl(
            lawId: "owned-world.sync-adoption-unique-ignoring-case",
            deniedOutcome: () => {
                var adopted = catalog.ReplaceFromSync(
                    document: Variant(
                        id: "AMBER",
                        source: seeded
                    ),
                    reason: out var reason
                );

                Assert.Contains(
                    actualString: reason,
                    comparisonType: StringComparison.Ordinal,
                    expectedSubstring: "in case only"
                );

                return adopted;
            },
            controlOutcome: () => catalog.ReplaceFromSync(
                document: Variant(
                    id: "amber",
                    source: seeded
                ),
                reason: out _
            )
        );
        Assert.Single(collection: catalog.All);
    }

    // The seeded document re-declared under one id, parsed the way a pull's own probe parses it.
    private static WorldDefinition Variant(string source, string id) {
        var node = JsonNode.Parse(json: File.ReadAllText(path: source))!.AsObject();

        node["identity"]!.AsObject()["id"] = id;
        node["documentId"] = id;

        return WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: node.ToJsonString()));
    }
}
