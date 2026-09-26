using System.Text.RegularExpressions;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The SDF kernels' module tree (<c>src/Puck.SdfVm/Assets/Shaders/Sdf</c>) is layered, lowest first: the generated
/// declarations (<c>isa</c>), the field interpreter (<c>field</c>), the frame's data (<c>frame</c>), the march
/// (<c>march</c>), the surface resolve (<c>surface</c>), shading (<c>shade</c>), the debug views and levers
/// (<c>debug</c>), and the pass entry points and bodies (<c>passes</c>). A module includes only modules of its own layer
/// or a lower one, so no layer reaches one above it; every source lives in a layer's directory, and every include
/// resolves to a source in the tree.
/// </summary>
public sealed partial class SdfShaderLayeringLawTests {
    private static readonly string[] Layers = ["isa", "field", "frame", "march", "surface", "shade", "debug", "passes"];

    [Fact]
    public void NoModuleIncludesAHigherLayer() {
        var root = RepositoryPaths.Resolve(relativePath: SdfWorldInterfaces.KernelDirectory);
        var files = Directory.EnumerateFiles(path: root, searchPattern: "*.hlsl*", searchOption: SearchOption.AllDirectories)
            .Where(predicate: static path => (path.EndsWith(value: ".hlsl", comparisonType: StringComparison.Ordinal) || path.EndsWith(value: ".hlsli", comparisonType: StringComparison.Ordinal)))
            .ToDictionary(
                elementSelector: File.ReadAllText,
                keySelector: path => Path.GetRelativePath(path: path, relativeTo: root).Replace(oldChar: '\\', newChar: '/'),
                comparer: StringComparer.Ordinal
            );

        Assert.NotEmpty(collection: files);
        Assert.Empty(collection: Violations(files: files));
    }
    // The check refuses an upward include by name, and accepts one within a layer or to a lower one; it also refuses a
    // source outside every layer and an include that resolves nowhere in the tree.
    [Fact]
    public void AnUpwardIncludeIsRefusedByName() {
        var files = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["field/a.hlsli"] = "#include \"b.hlsli\"\n#include \"../isa/c.hlsli\"\n",
            ["field/b.hlsli"] = string.Empty,
            ["isa/c.hlsli"] = string.Empty,
            ["frame/d.hlsli"] = "  #  include \"../march/e.hlsli\"\n",
            ["march/e.hlsli"] = "#include \"../passes/missing.hlsli\"\n",
            ["loose.hlsli"] = string.Empty,
        };

        Assert.Equal(
            actual: Violations(files: files),
            expected: [
                "frame/d.hlsli includes march/e.hlsli, a higher layer",
                "loose.hlsli is in no layer",
                "march/e.hlsli includes passes/missing.hlsli, which is not in the tree",
            ]
        );
    }

    // Every rule a tree of sources (keyed by path relative to the tree's root, forward slashes) breaks, in path order.
    private static List<string> Violations(IReadOnlyDictionary<string, string> files) {
        var violations = new List<string>();

        foreach (var (path, text) in files.OrderBy(keySelector: static file => file.Key, comparer: StringComparer.Ordinal)) {
            var layer = LayerOf(path: path);

            if (layer < 0) {
                violations.Add(item: $"{path} is in no layer");

                continue;
            }

            foreach (Match include in IncludePattern().Matches(input: text)) {
                var target = Resolve(
                    from: path,
                    include: include.Groups[1].Value
                );

                if (!files.ContainsKey(key: target)) {
                    violations.Add(item: $"{path} includes {target}, which is not in the tree");
                } else if (LayerOf(path: target) > layer) {
                    violations.Add(item: $"{path} includes {target}, a higher layer");
                }
            }
        }

        return violations;
    }
    // A source's layer, by the directory it sits in, or -1 when it sits in none.
    private static int LayerOf(string path) {
        var slash = path.IndexOf(value: '/', comparisonType: StringComparison.Ordinal);

        return ((slash < 0)
            ? -1
            : Array.IndexOf(array: Layers, value: path[..slash]));
    }
    // An include's target relative to the tree's root: DXC resolves a quoted include against the including file's
    // directory, and the build passes no include directory.
    private static string Resolve(string from, string include) {
        var segments = new List<string>(collection: from.Split(separator: '/')[..^1]);

        foreach (var segment in include.Split(separator: '/')) {
            if (segment == "..") {
                if (segments.Count > 0) {
                    segments.RemoveAt(index: (segments.Count - 1));
                }
            } else if (segment != ".") {
                segments.Add(item: segment);
            }
        }

        return string.Join(separator: '/', values: segments);
    }

    [GeneratedRegex(pattern: "^\\s*#\\s*include\\s+\"([^\"]+)\"", options: RegexOptions.Multiline)]
    private static partial Regex IncludePattern();
}
