using System.Text.RegularExpressions;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The SDF kernels read the program header, every instruction header and the directory headers only through what
/// <see cref="SdfIsaHlsl"/> generates from the model: a lane through its accessor (<c>SDF_INSTRUCTION_SHAPE(header)</c>),
/// never a swizzle, and a header's place through the generated vector counts (<c>SDF_PROGRAM_HEADER_VECTORS</c>,
/// <c>SDF_INSTRUCTION_DATA_VECTORS</c>), never a literal. A header load held in a variable is read the same way for the
/// rest of its function. So a lane the model moves moves in every kernel, and the fingerprint that names the encoding
/// names what the kernels read.
/// </summary>
public sealed partial class SdfKernelHeaderReadLawTests {
    [Fact]
    public void EveryKernelReadsTheHeadersThroughTheGeneratedAccessors() {
        var root = RepositoryPaths.Resolve(relativePath: SdfWorldInterfaces.KernelDirectory);
        var files = Directory.EnumerateFiles(path: root, searchOption: SearchOption.AllDirectories, searchPattern: "*.hlsl*")
            .Where(predicate: path => !Path.GetRelativePath(path: path, relativeTo: root).StartsWith(comparisonType: StringComparison.Ordinal, value: "isa"))
            .ToDictionary(
                elementSelector: File.ReadAllText,
                keySelector: path => Path.GetRelativePath(path: path, relativeTo: root).Replace(newChar: '/', oldChar: '\\'),
                comparer: StringComparer.Ordinal
            );

        Assert.NotEmpty(collection: files);
        Assert.Equal(
            actual: string.Join(separator: Environment.NewLine, values: Violations(files: files)),
            expected: string.Empty
        );
    }
    // The check refuses a literal header offset, a swizzled read of a header load, and a swizzled read of a variable a
    // header load was held in, and accepts the accessors, a record's swizzle and a variable reassigned from elsewhere.
    [Fact]
    public void ASwizzledHeaderReadIsRefusedByName() {
        var files = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["field/a.hlsli"] = """
                uint Op(uint index) {
                    return sdfWords[1u + index].x;
                }
                uint Shape(uint index) {
                    uint4 header = sdfWords[SDF_PROGRAM_HEADER_VECTORS + index];
                    return header.y;
                }
                uint Good(uint index) {
                    uint4 header = sdfWords[SDF_PROGRAM_HEADER_VECTORS + index];
                    uint4 meta = sdfWords[boundsOffset + 1u];
                    return SDF_INSTRUCTION_SHAPE(header) + meta.x + SDF_PROGRAM_MATERIAL_COUNT(sdfWords[0]);
                }
                uint Count() {
                    return sdfWords[0].y + sdfWords[dataOffset + (2u * 3u)].x;
                }
                """,
        };

        Assert.Equal(
            actual: Violations(files: files),
            expected: [
                "field/a.hlsli:2 reads a header by a literal offset",
                "field/a.hlsli:6 reads header, a header load, by its swizzle",
                "field/a.hlsli:14 reads a header by its swizzle",
                "field/a.hlsli:14 reads a header by a literal offset",
            ]
        );
    }

    [GeneratedRegex(pattern: @"sdfWords\[1u \+|dataOffset \+ \(?2u \*")]
    private static partial Regex LiteralOffset();
    [GeneratedRegex(pattern: @"sdfWords\[(?:SDF_PROGRAM_HEADER_VECTORS \+ [^\]]+|0|segmentOffset|instanceOffset|worldSegmentOffset|sdfSegmentDirectoryOffset\(\)|sdfInstanceDirectoryOffset\(\))\]\.[xyzw]\b")]
    private static partial Regex SwizzledLoad();
    [GeneratedRegex(pattern: @"\b(?<name>\w+)\s*=\s*sdfWords\[(?:SDF_PROGRAM_HEADER_VECTORS \+ [^\]]+|0|segmentOffset|instanceOffset)\]\s*;")]
    private static partial Regex HeldLoad();
    // Every place a kernel reads a header by a literal or a swizzle, in path and line order.
    private static List<string> Violations(IReadOnlyDictionary<string, string> files) {
        var violations = new List<string>();

        foreach (var (path, text) in files.OrderBy(keySelector: static file => file.Key, comparer: StringComparer.Ordinal)) {
            var lines = text.Split(separator: '\n');

            for (var index = 0; (index < lines.Length); index++) {
                var line = lines[index];

                if (line.TrimStart().StartsWith(comparisonType: StringComparison.Ordinal, value: "//")) {
                    continue;
                }
                if (SwizzledLoad().IsMatch(input: line)) {
                    violations.Add(item: $"{path}:{(index + 1)} reads a header by its swizzle");
                }
                if (LiteralOffset().IsMatch(input: line)) {
                    violations.Add(item: $"{path}:{(index + 1)} reads a header by a literal offset");
                }
                if (HeldLoad().Match(input: line) is { Success: true } held) {
                    var name = held.Groups["name"].Value;
                    var read = new Regex(pattern: $@"\b{Regex.Escape(str: name)}\.[xyzw]\b");
                    var reassigned = new Regex(pattern: $@"\b{Regex.Escape(str: name)}\s*=(?!=)");

                    for (var next = (index + 1); ((next < lines.Length) && !lines[next].StartsWith(value: '}')); next++) {
                        if (reassigned.IsMatch(input: lines[next]) && !HeldLoad().IsMatch(input: lines[next])) {
                            break;
                        }
                        if (!lines[next].TrimStart().StartsWith(comparisonType: StringComparison.Ordinal, value: "//") && read.IsMatch(input: lines[next])) {
                            violations.Add(item: $"{path}:{(next + 1)} reads {name}, a header load, by its swizzle");
                        }
                    }
                }
            }
        }

        return violations;
    }
}
