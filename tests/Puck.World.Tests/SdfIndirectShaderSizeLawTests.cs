using Puck.SdfVm;
using System.Text.RegularExpressions;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Bounds the bytecode the indirect diagnostics hand to a driver. A field evaluation in an unrolled
/// placement or sampling loop duplicates the entire interpreter; views read stored partitions instead.
/// Stable and incoming indirect shadows share one fallback call site so the views kernels, which compile both fade slots, retain their ceiling.</summary>
public sealed partial class SdfIndirectShaderSizeLawTests(ITestOutputHelper output) {
    [Fact]
    public void IndirectDiagnosticsKeepTheirCompiledInterpreterExpansionBounded() {
        const int MiB = (1024 * 1024);

        foreach (var name in new[] { "sdf-indirect-trace-proof", "sdf-indirect-gather" }) {
            var source = File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: $"tests/Puck.World.Tests/Assets/Shaders/{name}.comp.hlsl"));
            var copies = 0;

            foreach (Match loop in UnrolledLoopPattern().Matches(input: source)) {
                var start = source.IndexOf('{', (loop.Index + loop.Length));
                var end = (start + 1);
                var depth = 1;

                for (; ((end < source.Length) && (depth > 0)); end++) {
                    if (source[end] == '{') { depth++; }
                    if (source[end] == '}') { depth--; }
                }
                Assert.Equal(actual: depth, expected: 0);
                copies += FieldCallPattern().Matches(input: source[start..end]).Count;
            }
            output.WriteLine(message: $"{name}: {copies} field call sites inside explicitly unrolled loops; ceiling 0");
            Assert.Equal(actual: copies, expected: 0);
        }
        var kernels = new List<(string Path, long Ceiling)>();

        foreach (var kernel in new[] { "sdf-indirect-trace-proof.comp", "sdf-indirect-gather.comp", "sdf-indirect-debug-proof.comp" }) {
            kernels.Add(item: (SdfIndirectProbeBytecode.PathOf(extension: string.Empty, kernel: kernel), SdfIndirectProbeBytecode.CeilingOf(kernel: kernel)));
        }

        foreach (var kernel in SdfKernelSet.Kernels.Where(predicate: kernel => SdfKernelSet.StemOf(kernel: kernel).StartsWith(comparisonType: StringComparison.Ordinal, value: "sdf-world-views"))) {
            kernels.Add(item: (Path.Combine(path1: SdfKernelSet.DefaultDirectory, path2: (SdfKernelSet.StemOf(kernel: kernel) + ".comp")), (3 * MiB)));
        }
        foreach (var (path, ceiling) in kernels) {
            foreach (var extension in new[] { ".spv", ".dxil" }) {
                var file = new FileInfo(fileName: (path + extension));

                Assert.True(condition: file.Exists, userMessage: $"The build carries no {file.Name}.");
                output.WriteLine(message: $"{file.Name}: {file.Length} bytes; ceiling {ceiling} bytes");
                Assert.InRange(file.Length, 1, ceiling);
            }
        }
    }

    [GeneratedRegex(@"\[unroll(?:\([^)]*\))?\]\s*for\s*\(")]
    private static partial Regex UnrolledLoopPattern();
    [GeneratedRegex(@"\b(?:mapDistance|sdfIndirect(?:Place|Partition|Launch|Prove|March|Segment|Sample|Gradient))\s*\(")]
    private static partial Regex FieldCallPattern();
}
