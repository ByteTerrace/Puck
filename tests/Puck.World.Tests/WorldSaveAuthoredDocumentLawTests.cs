using System.Text;
using System.Text.Json.Nodes;
using Puck.Launcher;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: a save writes the authored document. Every world document the game ships or this
/// suite boots as a fixture, and every world a canary manifest boots, is loaded through the door the game boots it through, booted into a fresh server,
/// and saved through the snapshot <c>world.save</c> writes: <see cref="WorldSessionCapture.Capture"/>, then
/// <see cref="WorldSessionLevers.Fold"/> over the levers a boot seeds, then
/// <see cref="WorldDefinitionSerialization.SavePreservingBasis"/> over the file it loaded. What was written must boot
/// through the same door, to a definition whose canonical bytes equal the original's, and must declare no top-level
/// section that neither the source file nor its composed document holds. A JSON document carrying a <c>basis</c> or
/// <c>imports</c> saves as a delta over them rather than flat.</summary>
/// <remarks>The corpus is enumerated, never listed (<see cref="WorldDocumentCorpus"/>): every document under the shipped
/// world trees and this suite's fixture directory, and every world a <c>canary.json</c> names. A shipped document the boot door does not load on its own
/// is accounted for rather than skipped: it is a module library or a fragment. The trees are copied to a
/// <see cref="Mirror"/> once, so a document's relative <c>basis</c>, <c>imports</c>, and neighbours still resolve;
/// each save overwrites its file there and the original bytes are put back afterwards.</remarks>
public sealed class WorldSaveAuthoredDocumentLawTests(WorldSaveAuthoredDocumentLawTests.Mirror mirror, ITestOutputHelper output) : IClassFixture<WorldSaveAuthoredDocumentLawTests.Mirror> {
    public static TheoryData<string> Corpus() => [.. CorpusPaths()];
    [MemberData(nameof(Corpus))]
    [Theory]
    public void ASavedWorldBootsAsTheDocumentItWasLoadedFrom(string relativePath) {
        if (!WorldDocumentCorpus.TryBoot(
            path: mirror.PathOf(relativePath: relativePath),
            reason: out var refusal,
            stagingDirectory: mirror.StagingOf(relativePath: relativePath),
            worlds: out var worlds
        )) {
            Assert.True(
                condition: (
                    (refusal == WorldDocumentCorpus.ModuleLibrary) ||
                    (WorldDocumentCorpus.ShippedDocuments().Contains(value: relativePath) && WorldDocumentCorpus.IsFragment(relativePath: relativePath))
                ),
                userMessage: $"{relativePath} does not boot, and it is neither a module library nor a fragment: {refusal}"
            );

            return;
        }

        var composed = IsComposedDocument(path: mirror.PathOf(relativePath: relativePath));

        foreach (var (source, loaded) in worlds) {
            var (kept, note) = SaveAndReload(
                loaded: loaded,
                source: source
            );

            output.WriteLine(message: $"{source}: {((note.Length > 0) ? note : "saved as written")}");
            Assert.True(
                condition: (kept || !composed),
                userMessage: $"{relativePath} names a basis or imports and saved flat instead of as a delta over them: {note}"
            );
        }
    }
    /// <summary>The corpus holds a JSON document that names a basis or imports and is no fragment, so its row in
    /// <see cref="ASavedWorldBootsAsTheDocumentItWasLoadedFrom"/> must boot it and save it as a delta, and the delta
    /// claim is not vacuous.</summary>
    [Fact]
    public void TheCorpusHoldsAComposedWorld() => Assert.Contains(
        collection: CorpusPaths(),
        filter: static relativePath => (
            IsComposedDocument(path: RepositoryPaths.Resolve(relativePath: relativePath)) &&
            !(WorldDocumentCorpus.ShippedDocuments().Contains(value: relativePath) && WorldDocumentCorpus.IsFragment(relativePath: relativePath))
        )
    );

    private static IEnumerable<string> CorpusPaths() => WorldDocumentCorpus.ShippedDocuments()
        .Concat(second: WorldDocumentCorpus.FixtureDocuments())
        .Union(
            comparer: StringComparer.Ordinal,
            second: WorldDocumentCorpus.CanaryWorlds()
        )
        .Order(comparer: StringComparer.Ordinal);
    // A JSON document naming a basis or imports, which a save keeps as a delta over them rather than writing flat.
    private static bool IsComposedDocument(string path) => (
        !WorldDocumentName.IsSourceFile(path: path) &&
        (WorldDocumentCorpus.ReadObject(path: path) is { } document) &&
        (document.ContainsKey(propertyName: WorldDocumentBasis.BasisMemberName) || document.ContainsKey(propertyName: WorldDocumentBasis.ImportsMemberName))
    );
    // Saves one booted world the way world.save does and boots what was written, then puts the loaded file back so
    // the next document reads the corpus as checked in. Returns whether the save kept the file's basis or imports, and
    // the note naming why when it did not.
    private static (bool Preserved, string Note) SaveAndReload(string source, WorldDefinition loaded) {
        var original = File.ReadAllBytes(path: source);
        // A .puck source is never overwritten: world.save refuses one and a save names a JSON target instead.
        var target = (WorldDocumentName.IsSourceFile(path: source)
            ? Path.Combine(
                path1: Path.GetDirectoryName(path: source)!,
                path2: $"{Path.GetFileNameWithoutExtension(path: source)}.saved.world.json"
            )
            : source
        );
        // A save may declare a section the file declares, or one its composed document holds a value for; a section
        // neither holds is one the save made up.
        var declared = (WorldDocumentCorpus.ReadObject(path: source) ?? [])
            .Concat(second: ((JsonObject)JsonNode.Parse(utf8Json: WorldDefinitionSerialization.Serialize(definition: loaded))!).Where(predicate: static member => (member.Value is not null)))
            .Select(selector: static member => member.Key)
            .ToHashSet(comparer: StringComparer.Ordinal);

        try {
            using var fixture = Fixtures.FreshServer(
                definition: loaded,
                documentPath: source,
                machineCatalog: TestHookInstaller.CreateMachineCatalog()
            );
            var server = fixture.Server;
            var snapshot = WorldSessionLevers.Fold(
                audio: new UnengagedAudioLever(),
                bindingBar: new WorldBindingBarVisibility(),
                definition: WorldSessionCapture.Capture(
                    definition: server.Definition,
                    engineTick: server.CompletedEngineTicks,
                    machines: server.Machines,
                    population: server.Population,
                    tick: (server.NextInputTick - 1UL)
                ),
                pacing: new PresentPacingControl(initialTargetHertz: ((loaded.Host.TargetHertz > 0.0)
                    ? loaded.Host.TargetHertz
                    : null)),
                settings: new WorldRenderSettings(defaults: loaded.Render)
            );

            _ = WorldDefinitionSerialization.SavePreservingBasis(
                basisPath: out var basisPath,
                catalog: TestHookInstaller.CreateMachineCatalog(),
                definition: snapshot,
                imports: out var imports,
                note: out var note,
                path: target
            );

            Assert.True(
                condition: WorldDefinitionLoader.TryResolve(
                    catalog: TestHookInstaller.CreateMachineCatalog(),
                    explicitPath: target,
                    failure: out var failure,
                    source: out var reloaded
                ),
                userMessage: $"{source} saved a document its own boot refuses: {failure}"
            );

            var appeared = WorldDocumentCorpus.ReadObject(path: target)!
                .Select(selector: static member => member.Key)
                .Where(predicate: key => !declared.Contains(item: key))
                .ToArray();

            Assert.True(
                condition: (appeared.Length == 0),
                userMessage: $"{source} saved sections its source does not declare: {string.Join(separator: ", ", value: appeared)} ({((note.Length > 0) ? note : "saved as written")})"
            );
            Assert.Equal(
                actual: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: reloaded.Definition)),
                expected: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: loaded))
            );

            return (((basisPath is not null) || (imports.Count > 0)), note);
        } finally {
            if (target == source) {
                File.WriteAllBytes(
                    bytes: original,
                    path: source
                );
            } else {
                File.Delete(path: target);
            }
        }
    }

    /// <summary>The trees a corpus document may reach through a relative <c>basis</c>, <c>imports</c>, neighbour, or
    /// content path, copied once into a temporary directory the fixture deletes on dispose, whatever the laws'
    /// outcome; a composition source's worlds stage beneath it too.</summary>
    public sealed class Mirror : IDisposable {
        private static readonly string[] MirroredTrees = [
            "src/Puck.World/Assets",
            WorldDocumentCorpus.CanaryDirectory,
            WorldDocumentCorpus.FixtureDirectory,
            "worlds",
        ];

        private readonly TemporaryDirectory m_directory = new();

        /// <summary>Initializes a new instance of the <see cref="Mirror"/> class, copying every mirrored tree.</summary>
        public Mirror() {
            foreach (var tree in MirroredTrees) {
                var source = RepositoryPaths.Resolve(relativePath: tree);

                foreach (var file in Directory.EnumerateFiles(
                    path: source,
                    searchOption: SearchOption.AllDirectories,
                    searchPattern: "*"
                )) {
                    var target = PathOf(relativePath: Path.Combine(
                        path1: tree,
                        path2: Path.GetRelativePath(
                            path: file,
                            relativeTo: source
                        )
                    ));

                    _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
                    File.Copy(
                        destFileName: target,
                        sourceFileName: file
                    );
                }
            }
        }

        /// <inheritdoc/>
        public void Dispose() => m_directory.Dispose();
        /// <summary>Returns the mirrored copy of a repository-relative path.</summary>
        /// <param name="relativePath">The repository-relative path.</param>
        /// <returns>The full path of the copy.</returns>
        public string PathOf(string relativePath) => m_directory.PathOf(name: relativePath);
        /// <summary>Returns the directory a composition source's worlds stage into, one per source.</summary>
        /// <param name="relativePath">The repository-relative path of the composition source.</param>
        /// <returns>The full path of the staging directory, created.</returns>
        public string StagingOf(string relativePath) => Directory.CreateDirectory(path: m_directory.PathOf(name: $"staged/{relativePath}")).FullName;
    }

    private sealed class UnengagedAudioLever : IWorldAudioLever {
        public float? SessionMasterVolume => null;

        public void SetMasterVolume(float value) => throw new InvalidOperationException(message: "A boot never engages the master-volume lever.");
    }
}
