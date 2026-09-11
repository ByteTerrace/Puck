using Xunit;

using Puck.World;
using Puck.World.Authoring;
using Puck.SignedDistance;
using System.Numerics;

namespace Puck.Cli.Tests.Creation;

/// <summary><c>puck creation</c>: the shipped sculpt registry is empty, an unknown sculpt name and a malformed
/// world file both refuse without writing, and <c>stats</c> reports the moth prototype's shape budget.</summary>
public sealed class CreationCommandTests {
    private static string RepoRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return directory!.FullName;
    }
    private static string TempWorldCopy() {
        var source = Path.Combine(RepoRoot(), "src", "Puck.World", "Assets", "worlds", "moth.world.json");
        var target = Path.Combine(Path.GetTempPath(), $"puck-creation-cli-{Guid.NewGuid():N}.world.json");

        File.Copy(sourceFileName: source, destFileName: target);

        return target;
    }
    private static (int ExitCode, string Output) Invoke(params string[] args) {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();

        Console.SetOut(newOut: output);
        Console.SetError(newError: output);

        try {
            return (PuckRootCommand.Invoke(args: args), output.ToString());
        } finally {
            Console.SetOut(newOut: originalOut);
            Console.SetError(newError: originalError);
        }
    }

    /// <summary>The shipped registry carries no sculpts.</summary>
    [Fact]
    public void SculptsReportsAnEmptyRegistry() {
        var (exitCode, output) = Invoke("creation", "sculpts");

        Assert.Equal(expected: 0, actual: exitCode);
        Assert.Contains(expectedSubstring: "none registered", actualString: output, comparisonType: StringComparison.Ordinal);
    }
    /// <summary>An unknown sculpt name is refused BY NAME (naming the empty registry) and the file is left
    /// untouched.</summary>
    [Fact]
    public void SculptRefusesAnUnknownNameWithoutWriting() {
        var path = TempWorldCopy();
        var before = File.ReadAllBytes(path: path);

        try {
            var (exitCode, output) = Invoke("creation", "sculpt", "not-a-real-sculpt", "--world", path);

            Assert.Equal(expected: 2, actual: exitCode);
            Assert.Contains(expectedSubstring: "unknown sculpt", actualString: output, comparisonType: StringComparison.Ordinal);
            Assert.Contains(expectedSubstring: "none registered", actualString: output, comparisonType: StringComparison.Ordinal);
            Assert.Equal(expected: before, actual: File.ReadAllBytes(path: path));
        } finally {
            File.Delete(path: path);
        }
    }
    /// <summary>A world file that is not a JSON object, requested under an unknown sculpt name, is refused before
    /// the file is ever parsed — with an empty registry, no name reaches the JSON-parse step.</summary>
    [Fact]
    public void SculptRefusesAMalformedWorldFileWithoutWriting() {
        var path = Path.Combine(Path.GetTempPath(), $"puck-creation-cli-{Guid.NewGuid():N}.world.json");

        File.WriteAllText(path: path, contents: "[1, 2, 3]");

        var before = File.ReadAllBytes(path: path);

        try {
            var (exitCode, output) = Invoke("creation", "sculpt", "not-a-real-sculpt", "--world", path);

            Assert.Equal(expected: 2, actual: exitCode);
            Assert.Contains(expectedSubstring: "unknown sculpt", actualString: output, comparisonType: StringComparison.Ordinal);
            Assert.Equal(expected: before, actual: File.ReadAllBytes(path: path));
        } finally {
            File.Delete(path: path);
        }
    }
    /// <summary><c>stats</c> reports the moth prototype's shape count against its budget — the counts the fixture
    /// document itself carries (its authored shapes, and its stamp charge, where a panelled shape counts twice),
    /// never a pinned literal that goes stale with every art pass.</summary>
    [Fact]
    public void StatsReportsMothShapeBudget() {
        var path = TempWorldCopy();

        try {
            var moth = WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: path)).Creations.Single(predicate: static creation => string.Equals(
                a: creation.Id.Value,
                b: "moth",
                comparisonType: StringComparison.Ordinal
            ));
            var (exitCode, output) = Invoke("creation", "stats", "--world", path, "--prototype", "moth");

            Assert.Equal(expected: 0, actual: exitCode);
            Assert.Contains(expectedSubstring: $"[moth] shapes: {moth.Document.Shapes!.Count}, stamp budget: {moth.Document.StampShapeCount()}/{WorldPlacementPolicy.MaxShapesPerStamp}", actualString: output, comparisonType: StringComparison.Ordinal);
            Assert.Contains(expectedSubstring: "primitive:", actualString: output, comparisonType: StringComparison.Ordinal);
        } finally {
            File.Delete(path: path);
        }
    }
    [Fact]
    public void StatsExposesSharedFlareClampsInsteadOfOnlyTheUnitGlobalScale() {
        var path = TempWorldCopy();
        try {
            var definition = WorldDefinitionSerialization.Deserialize(File.ReadAllBytes(path));
            var shape = new ShapeDocument(
                Id: 0, Name: "flared", Type: SdfSolidPrimitive.Sphere,
                Position: Vector3.Zero, Rotation: Quaternion.Identity, Scale: Vector3.One,
                Material: 0, Blend: SdfBlendOp.Union, Smooth: 0f, Group: 1,
                Flare: new ShapeFlareDocument(2f, 0f, 1f));
            var document = new CreationDocument(
                Schema: CreationDocument.CurrentSchema, Name: "scope-proof", Palette: null,
                Shapes: [shape, shape with { Id = 1, Name = "cutter", Flare = null, Blend = SdfBlendOp.Subtraction }],
                Frames: null, Noise: null);
            var canonical = CreationCanonicalizer.Canonicalize(document);
            WorldDefinitionSerialization.Save(definition with {
                CreationsRaw = [.. definition.Creations, new WorldPrototype("scope-proof", canonical.Document, canonical.Hash)],
            }, path);
            var before = File.ReadAllBytes(path);
            var (exitCode, output) = Invoke("creation", "stats", "--world", path, "--prototype", "scope-proof");

            Assert.Equal(0, exitCode);
            Assert.Contains("shape 0 (flared): flare", output, StringComparison.Ordinal);
            Assert.Contains("static: globalStepScale 1, scoped clamps 1 (1 shared)", output, StringComparison.Ordinal);
            Assert.Contains("pooled: globalStepScale 1, scoped clamps 1 (1 shared)", output, StringComparison.Ordinal);
            Assert.Contains("2 shape(s) sharing one clamp", output, StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllBytes(path));
        } finally {
            File.Delete(path);
        }
    }

    /// <summary>An unknown prototype id is refused, naming that it names no such prototype.</summary>
    [Fact]
    public void StatsRefusesAnUnknownPrototypeId() {
        var path = TempWorldCopy();

        try {
            var (exitCode, output) = Invoke("creation", "stats", "--world", path, "--prototype", "not-a-real-prototype");

            Assert.Equal(expected: 2, actual: exitCode);
            Assert.Contains(expectedSubstring: "names no prototype", actualString: output, comparisonType: StringComparison.Ordinal);
        } finally {
            File.Delete(path: path);
        }
    }
}
