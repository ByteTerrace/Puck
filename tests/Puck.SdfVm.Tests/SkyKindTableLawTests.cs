using System.Numerics;
using System.Runtime.InteropServices;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the sky's kinds are an open set. A kind is its parameter record and one module under
/// <c>sky/kinds/</c>, registered in the generated kind table (<see cref="SdfSkyKindsHlsl"/>), and adding one touches no
/// other kind and no pass: generating the table with a fixture kind added inserts the fixture's own lines into both
/// generated includes and changes none of the rest; no pass source names a kind; each kind's module names no other kind's
/// parameters; and the sky and composite passes' interface reads the layer table whatever kinds it holds.
/// </summary>
public sealed class SkyKindTableLawTests {
    private static string Root => RepositoryPaths.Resolve(relativePath: SdfKernelInterfaces.KernelDirectory);

    [Fact]
    public void AddingAKindInsertsItsOwnLinesAndChangesNoOtherKindsOrAnyPass() {
        var fixture = SdfSkyKindDeclaration.Of<SdfSkyFixture>();
        IReadOnlyList<SdfSkyKindDeclaration> extended = [.. SdfSkyKindsHlsl.Kinds, fixture];

        Assert.Equal(
            actual: WithoutFixture(text: SdfSkyKindsHlsl.Generate(kinds: extended), fixture: fixture),
            expected: SdfSkyKindsHlsl.Generate()
        );
        Assert.Equal(
            actual: WithoutFixture(text: SdfSkyKindsHlsl.GenerateTable(kinds: extended), fixture: fixture),
            expected: SdfSkyKindsHlsl.GenerateTable()
        );
        Assert.Contains(actualString: SdfSkyKindsHlsl.GenerateTable(kinds: extended), expectedSubstring: "#include \"kinds/fixture.hlsli\"");
    }
    [Fact]
    public void NoPassNamesAKindAndNoModuleNamesAnotherKind() {
        foreach (var path in Directory.EnumerateFiles(path: Path.Combine(path1: Root, path2: "passes"), searchPattern: "*.hlsl*")) {
            Assert.DoesNotContain(actualString: File.ReadAllText(path: path), expectedSubstring: "SDF_SKY_KIND_");
        }

        foreach (var kind in SdfSkyKindsHlsl.Kinds) {
            var module = File.ReadAllText(path: Path.Combine(path1: Root, path2: "sky", path3: kind.Module));

            Assert.Contains(actualString: module, expectedSubstring: $"float4 {kind.Evaluator}({kind.Parameters.Name} ");
            foreach (var other in SdfSkyKindsHlsl.Kinds.Where(predicate: other => (other.Kind != kind.Kind))) {
                Assert.DoesNotContain(actualString: module, expectedSubstring: other.Parameters.Name);
            }
        }

        // The passes read the layer table as one structured buffer of layer records, the same whatever kinds it holds.
        Assert.Contains(
            collection: SdfKernelInterfaces.LightAndSkyTables,
            filter: static member => ((member.Name == SdfKernelInterfaces.SkyLayers) && (member.Structure!.Name == nameof(SdfSkyLayer)))
        );
    }

    // A generated text without the fixture's lines: its declaration block, from its header comment through its decoder's
    // closing brace, and every other line that names it.
    private static string WithoutFixture(string text, SdfSkyKindDeclaration fixture) {
        var lines = text.Split(separator: '\n').ToList();
        var header = lines.FindIndex(match: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: $"// The {fixture.Name} kind ("));

        if (header >= 0) {
            var decoder = lines.FindIndex(startIndex: header, match: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: $"{fixture.Parameters.Name} {fixture.Decoder}("));
            var end = lines.FindIndex(match: static line => (line == "}"), startIndex: decoder);

            // The block and the blank line that separates it from the kind before it.
            lines.RemoveRange(count: ((end - header) + 2), index: (header - 1));
        }

        lines.RemoveAll(match: line => line.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: fixture.Name));
        // A switch arm's return line names the fixture's evaluator, which the removal above takes with its case line.
        return string.Join(separator: '\n', values: lines);
    }

    // A fixture kind no kernel draws: a tint and a seed, a field kind, so the field predicate gains its case too.
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct SdfSkyFixture : ISdfSkyKind {
        [FieldOffset(0)] public Vector3 Tint;
        [FieldOffset(12)] public uint Seed;

        public static SdfSkyLayerKind Kind => ((SdfSkyLayerKind)99u);
        public static SdfSkyLayerClass Class => SdfSkyLayerClass.Field;
        public static string Name => "fixture";
    }
}
