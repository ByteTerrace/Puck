using Xunit;

using Puck.World;
using Puck.World.Authoring;
using Puck.SignedDistance;
using Puck.Testing;
using System.Numerics;

namespace Puck.Cli.Worlds.Tests.Creation;

/// <summary><c>puck creation</c>: the shipped sculpt registry is empty, an unknown sculpt name and a malformed
/// world file both refuse without writing, and <c>stats</c> reports the moth prototype's shape budget.</summary>
public sealed class CreationCommandTests {
    private static (int ExitCode, string Output) Invoke(params string[] args) =>
        ConsoleCapture.Run(run: () => SuiteRoot.Invoke(args: args));
    // The moth world composed where it ships, through its basis and imports, written as one standalone document: the
    // file both `creation --world` and a direct deserialize read, with nothing left to resolve beside it.
    private static string TempWorldCopy(TemporaryDirectory directory) => directory.WriteBytes(
        bytes: ShippedWorldDocuments.Composed(path: RepositoryPaths.Resolve(relativePath: $"{ShippedWorldDocuments.WorldDirectory}/debug/avatars/moth.puck")),
        name: "moth.world.json"
    );

    /// <summary>A world file that is not a JSON object, requested under an unknown sculpt name, is refused before
    /// the file is ever parsed — with an empty registry, no name reaches the JSON-parse step.</summary>
    [Fact]
    public void SculptRefusesAMalformedWorldFileWithoutWriting() {
        using var directory = new TemporaryDirectory(prefix: "puck-creation-cli-");
        var path = directory.WriteText(
            name: "malformed.world.json",
            text: "[1, 2, 3]"
        );

        var before = File.ReadAllBytes(path: path);

        var (exitCode, output) = Invoke(
            "creation",
            "sculpt",
            "not-a-real-sculpt",
            "--world",
            path
        );

        Assert.Equal(
            actual: exitCode,
            expected: 2
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "unknown sculpt"
        );
        Assert.Equal(
            expected: before,
            actual: File.ReadAllBytes(path: path)
        );
    }
    /// <summary>An unknown sculpt name is refused BY NAME (naming the empty registry) and the file is left
    /// untouched.</summary>
    [Fact]
    public void SculptRefusesAnUnknownNameWithoutWriting() {
        using var directory = new TemporaryDirectory();
        var path = TempWorldCopy(directory: directory);
        var before = File.ReadAllBytes(path: path);

        var (exitCode, output) = Invoke(
            "creation",
            "sculpt",
            "not-a-real-sculpt",
            "--world",
            path
        );

        Assert.Equal(
            actual: exitCode,
            expected: 2
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "unknown sculpt"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "none registered"
        );
        Assert.Equal(
            expected: before,
            actual: File.ReadAllBytes(path: path)
        );
    }
    /// <summary>The shipped registry carries no sculpts.</summary>
    [Fact]
    public void SculptsReportsAnEmptyRegistry() {
        var (exitCode, output) = Invoke(
            "creation",
            "sculpts"
        );

        Assert.Equal(
            actual: exitCode,
            expected: 0
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "none registered"
        );
    }
    /// <summary>Schema and render admission cannot stand in for contact compilation. A capsule scaled apart in X and Z
    /// leaves a residual Scale; nonuniform values must fail only when it is actually placed as a solid.</summary>
    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    public void StatsConstructsContactForSolidPlacements(bool solid, bool nonuniform, int expectedExitCode) {
        using var directory = new TemporaryDirectory();
        var path = TempWorldCopy(directory: directory);

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: path));
        var shape = new ShapeDocument(
            Id: 0,
            Name: "capsule",
            Type: SdfSolidPrimitive.Capsule,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: (nonuniform
            ? new Vector3(
                    x: .4f,
                    y: .2f,
                    z: .1f
                )
            : new Vector3(value: .2f)),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var canonical = CreationCanonicalizer.Canonicalize(new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "contact-proof",
            Palette: null,
            Shapes: [shape],
            Frames: null
        ));

        WorldDefinitionSerialization.Save(
            definition: definition with {
                CreationsRaw = [.. definition.Creations, new WorldPrototype(
                    "contact-proof",
                    canonical.Document,
                    canonical.Hash
                )],
                PlacementRowsRaw = [.. definition.Placements, new WorldPlacement(
                    Id: "contact-proof",
                    PrototypeId: "contact-proof",
                    Position: new Vector3(
                        x: 3f,
                        y: 1f,
                        z: 0f
                    ),
                    YawDegrees: 0f,
                    Scale: 1f,
                    Solid: (solid
            ? new WorldSolid(Margin: 0f)
            : null)
                )],
            },
            path: path
        );
        var before = File.ReadAllBytes(path: path);
        // Deliberately filter to Moth: contact coverage is world-wide, not limited to the report's prototype.
        var (exitCode, output) = Invoke(
            "creation",
            "stats",
            "--world",
            path,
            "--prototype",
            "moth"
        );

        Assert.Equal(
            actual: exitCode,
            expected: expectedExitCode
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: ((expectedExitCode == 0)
            ? "contact: accepted"
            : "contact inspection failed")
        );
        if (expectedExitCode != 0) {
            Assert.Contains(
                actualString: output,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "Scale"
            );
        }
        Assert.Equal(
            before,
            File.ReadAllBytes(path: path)
        );
    }
    [Fact]
    public void StatsExposesSharedFlareClampsInsteadOfOnlyTheUnitGlobalScale() {
        using var directory = new TemporaryDirectory();
        var path = TempWorldCopy(directory: directory);

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: path));
        var shape = new ShapeDocument(
            Id: 0,
            Name: "flared",
            Type: SdfSolidPrimitive.Sphere,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: Vector3.One,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 1,
            Flare: new ShapeFlareDocument(
                2f,
                0f,
                1f
            )
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "scope-proof",
            Palette: null,
            Shapes: [shape, shape with { Id = 1, Name = "cutter", Flare = null, Blend = SdfBlendOp.Subtraction }],
            Frames: null,
            Noise: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(document);

        WorldDefinitionSerialization.Save(
            definition: definition with {
                CreationsRaw = [.. definition.Creations, new WorldPrototype(
                    "scope-proof",
                    canonical.Document,
                    canonical.Hash
                )],
            },
            path: path
        );
        var before = File.ReadAllBytes(path: path);

        var (exitCode, output) = Invoke(
            "creation",
            "stats",
            "--world",
            path,
            "--prototype",
            "scope-proof"
        );

        Assert.Equal(
            actual: exitCode,
            expected: 0
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "shape 0 (flared): flare"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "static: globalStepScale 1, scoped clamps 1 (1 shared)"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "creation: globalStepScale 1, scoped clamps 1 (1 shared)"
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "2 shape(s) sharing one clamp"
        );
        Assert.Matches(
            actualString: output,
            expectedRegexPattern: @"segments \d+ \(\d+ rigid\)"
        );
        Assert.Equal(
            before,
            File.ReadAllBytes(path: path)
        );
    }
    /// <summary>An unknown prototype id is refused, naming that it names no such prototype.</summary>
    [Fact]
    public void StatsRefusesAnUnknownPrototypeId() {
        using var directory = new TemporaryDirectory();
        var path = TempWorldCopy(directory: directory);

        var (exitCode, output) = Invoke(
            "creation",
            "stats",
            "--world",
            path,
            "--prototype",
            "not-a-real-prototype"
        );

        Assert.Equal(
            actual: exitCode,
            expected: 2
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "names no prototype"
        );
    }
    /// <summary><c>shape</c> reads the named shapes' laws in the order named, refuses an unknown prototype or shape by
    /// name (exit 2), and a file that does not load (exit 1).</summary>
    [Fact]
    public void ShapeReadsNamedShapesAndRefusesUnknownNames() {
        using var directory = new TemporaryDirectory();
        var path = TempWorldCopy(directory: directory);

        var (exitCode, output) = Invoke("creation", "shape", "--world", path, "--prototype", "moth", "--shape", "torso", "--shape", "beltClasp");

        Assert.Equal(expected: 0, actual: exitCode);
        Assert.Contains(expectedSubstring: "[creation shape: moth shapes=2/", actualString: output, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: " torso Superellipsoid ", actualString: output, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: " beltClasp Prism/Convex ", actualString: output, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: (output.IndexOf(value: " torso ", comparisonType: StringComparison.Ordinal) < output.IndexOf(value: " beltClasp ", comparisonType: StringComparison.Ordinal)), userMessage: output);

        (exitCode, output) = Invoke("creation", "shape", "--world", path, "--prototype", "nope");
        Assert.Equal(expected: 2, actual: exitCode);
        Assert.Contains(expectedSubstring: "names no prototype 'nope'", actualString: output, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "moth", actualString: output[output.IndexOf(value: "prototypes:", comparisonType: StringComparison.Ordinal)..], comparisonType: StringComparison.Ordinal);

        (exitCode, output) = Invoke("creation", "shape", "--world", path, "--prototype", "moth", "--shape", "nope");
        Assert.Equal(expected: 2, actual: exitCode);
        Assert.Contains(expectedSubstring: "'moth' has no shape named 'nope'", actualString: output, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "shapes: pelvis, torso,", actualString: output, comparisonType: StringComparison.Ordinal);

        (exitCode, output) = Invoke("creation", "shape", "--world", directory.WriteText(name: "malformed.world.json", text: "[1, 2, 3]"), "--prototype", "moth");
        Assert.Equal(expected: 1, actual: exitCode);
        Assert.Contains(expectedSubstring: "failed to load", actualString: output, comparisonType: StringComparison.Ordinal);
    }
}
