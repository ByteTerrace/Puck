using System.Numerics;
using System.Text;
using Puck.Assets;
using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: a creation's canonical form is a fixed point, and its pin is the content pin of
/// exactly the bytes returned. For every prototype of every world a shipped document boots, canonicalizing the
/// canonical document again yields the same bytes, byte for byte; and a world whose prototype rows carry their
/// canonical documents and pins is written and read back through the world serializer, whose validator re-derives each
/// pin, to documents that canonicalize to the same bytes again.</summary>
/// <remarks>The corpus is enumerated, never listed (<see cref="WorldDocumentCorpus"/>). Each document is booted through
/// the game's door, so a prototype is read as the world resolves it: composed from its basis and imports, with its
/// document references resolved. A document that does not boot on its own is a module library or a fragment, whose
/// prototypes reach the documents that compose it.</remarks>
public sealed class CreationCanonicalFormLawTests(CreationCanonicalFormLawTests.Staging staging) : IClassFixture<CreationCanonicalFormLawTests.Staging> {
    public static TheoryData<string> Corpus() => [.. WorldDocumentCorpus.ShippedDocuments().Order(comparer: StringComparer.Ordinal)];
    [MemberData(nameof(Corpus))]
    [Theory]
    public void EveryPrototypeCanonicalizesToAFixedPointPinnedByItsBytes(string relativePath) {
        if (!WorldDocumentCorpus.TryBoot(
            path: RepositoryPaths.Resolve(relativePath: relativePath),
            reason: out var refusal,
            stagingDirectory: staging.For(relativePath: relativePath),
            worlds: out var worlds
        )) {
            Assert.True(
                condition: ((refusal == WorldDocumentCorpus.ModuleLibrary) || WorldDocumentCorpus.IsFragment(relativePath: relativePath)),
                userMessage: $"{relativePath} does not boot, and it is neither a module library nor a fragment: {refusal}"
            );

            return;
        }

        var failures = new List<string>();

        foreach (var (source, definition) in worlds) {
            Check(
                definition: definition,
                failures: failures,
                world: Path.GetFileName(path: source)
            );
        }

        Assert.True(
            condition: (failures.Count == 0),
            userMessage: string.Join(
                separator: Environment.NewLine,
                values: failures
            )
        );
    }
    // Float normalization is not idempotent: renormalizing a unit quaternion or direction moves its last bits. Every
    // magnitude class a document can author — far from unit, normalized once in float, and exactly unit — must
    // canonicalize to a fixed point.
    [Fact]
    public void RotationsAndDirectionsCanonicalizeToAFixedPoint() {
        var random = new Random(Seed: 20260924);
        var shapes = new ShapeDocument[256];

        for (var i = 0; (i < shapes.Length); i++) {
            var rotation = new Quaternion(
                w: ((random.NextSingle() * 2f) - 1f),
                x: ((random.NextSingle() * 2f) - 1f),
                y: ((random.NextSingle() * 2f) - 1f),
                z: ((random.NextSingle() * 2f) - 1f)
            );
            var normal = new Vector3(
                x: ((random.NextSingle() * 2f) - 1f),
                y: ((random.NextSingle() * 2f) - 1f),
                z: ((random.NextSingle() * 2f) - 1f)
            );

            (rotation, normal) = ((i % 3) switch {
                0 => ((rotation * (0.25f + (random.NextSingle() * 4f))), (normal * (0.25f + (random.NextSingle() * 4f)))),
                1 => (Quaternion.Normalize(value: rotation), Vector3.Normalize(value: normal)),
                _ => (Quaternion.Identity, Vector3.UnitY),
            });
            shapes[i] = (CreationFixtures.Shape(type: SdfSolidPrimitive.Box) with {
                Domain = [new ShapeDomainOp.Symmetry(Normal: normal)],
                Id = i,
                Rotation = rotation,
            });
        }

        var canonical = CreationCanonicalizer.Canonicalize(document: CreationFixtures.Document(
            name: "spread",
            shapes: shapes
        ));
        var again = CreationCanonicalizer.Canonicalize(document: canonical.Document).Bytes;

        Assert.True(
            condition: canonical.Bytes.AsSpan().SequenceEqual(other: again),
            userMessage: Parting(
                again: again,
                first: canonical.Bytes
            )
        );
    }

    // A prototype's document references resolve against the world that carries it, so its canonical form is read back
    // the way a saved world is: the whole definition written and parsed again.
    private static void Check(WorldDefinition definition, List<string> failures, string world) {
        var prototypes = definition.Creations;

        if (prototypes.Count == 0) {
            return;
        }

        var canonical = new CanonicalDocument<CreationDocument>[prototypes.Count];

        for (var i = 0; (i < prototypes.Count); i++) {
            canonical[i] = CreationCanonicalizer.Canonicalize(
                document: prototypes[i].Document,
                source: prototypes[i].Id
            );

            if (canonical[i].Hash != ContentPin.Compute(content: canonical[i].Bytes).Hex) {
                failures.Add(item: $"{world}/{prototypes[i].Id}: the reported pin {canonical[i].Hash} is not the content pin of the bytes returned");
            }

            Compare(
                again: CreationCanonicalizer.Canonicalize(document: canonical[i].Document).Bytes,
                failures: failures,
                first: canonical[i].Bytes,
                route: $"{world}/{prototypes[i].Id}: canonicalizing the canonical document"
            );
        }

        WorldDefinition read;

        try {
            read = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: (definition with {
                CreationsRaw = [.. prototypes.Select(selector: (prototype, i) => (prototype with {
                    Document = canonical[i].Document,
                    HashRaw = canonical[i].Hash,
                }))],
            })));
        } catch (InvalidDataException exception) {
            failures.Add(item: $"{world}: a world carrying its canonical prototypes does not read back: {exception.Message}");

            return;
        }

        for (var i = 0; (i < prototypes.Count); i++) {
            Compare(
                again: CreationCanonicalizer.Canonicalize(document: read.Creations[i].Document).Bytes,
                failures: failures,
                first: canonical[i].Bytes,
                route: $"{world}/{prototypes[i].Id}: canonicalizing the canonical document read back from its world"
            );
        }
    }
    private static void Compare(byte[] first, byte[] again, string route, List<string> failures) {
        if (!first.AsSpan().SequenceEqual(other: again)) {
            failures.Add(item: $"{route} moved the canonical bytes: {Parting(again: again, first: first)}");
        }
    }
    private static string Parting(byte[] first, byte[] again) {
        var at = first.AsSpan().CommonPrefixLength(other: again);
        var start = Math.Max(
            val1: 0,
            val2: (at - 48)
        );

        string Window(byte[] bytes) => Encoding.UTF8.GetString(bytes: bytes.AsSpan(start: start, length: Math.Min(
            val1: 96,
            val2: (bytes.Length - start)
        )));

        return $"'{Window(bytes: first)}' became '{Window(bytes: again)}'";
    }

    /// <summary>The directory composition sources stage their worlds into, deleted on dispose whatever the laws'
    /// outcome.</summary>
    public sealed class Staging : IDisposable {
        private readonly TemporaryDirectory m_directory = new();

        /// <inheritdoc/>
        public void Dispose() => m_directory.Dispose();
        /// <summary>Returns the directory one composition source's worlds stage into.</summary>
        /// <param name="relativePath">The repository-relative path of the composition source.</param>
        /// <returns>The full path of the staging directory, created.</returns>
        public string For(string relativePath) => Directory.CreateDirectory(path: m_directory.PathOf(name: relativePath)).FullName;
    }
}
