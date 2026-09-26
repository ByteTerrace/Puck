using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Puck.Shaders.Tests;

/// <summary>A resource's Direct3D 12 register number equals its Vulkan binding number, and its register space equals
/// its descriptor set, so no backend remaps a register. These laws read every HLSL source and include the build
/// compiles or packages: the shader items each <c>src</c> project declares for <c>build/Shaders.targets</c>, the graph's package-library kernels, and the
/// pipeline sources <c>build/WorldAssets.targets</c> hands the tree run that fills the shipped package store, with
/// every source a shipped <c>*.graph.json</c> names. They hold every declaration that carries both a
/// <c>[[vk::binding(N, S)]]</c> and a <c>register(xN, spaceS)</c> to that rule.</summary>
public sealed partial class ShaderRegisterBindingLawTests {
    // The item types build/Shaders.targets compiles, or hashes as includes of what it compiles, the pipeline sources
    // build/WorldAssets.targets packages into the store beside the shipped worlds, and the graph's package-library kernels.
    private static readonly string[] ShaderItemTypes = [
        "ComputeShaderSource",
        "Direct3D11KernelSource",
        "FragmentShaderSource",
        "PuckWorldPipelineSource",
        "ShaderInclude",
        "VertexShaderSource",
    ];

    [GeneratedRegex(pattern: @"vk::binding\(\s*(?<binding>\d+)\s*(?:,\s*(?<set>\d+)\s*)?\)")]
    private static partial Regex BindingAttribute();
    [GeneratedRegex(pattern: @"^\s*#\s*define\s+(?<name>\w+)\s+(?<value>\w+)\s*$")]
    private static partial Regex Define();
    [GeneratedRegex(pattern: @"(?<name>\w+)\s*(?:\[\s*\w*\s*\])?\s*:\s*register\(\s*(?<register>\w+)\s*(?:,\s*space(?<space>\d+)\s*)?\)")]
    private static partial Regex RegisterClause();
    [GeneratedRegex(pattern: @"^[bstu](?<number>\d+)$")]
    private static partial Regex RegisterName();
    // The files one Include attribute names, relative to its project directory. Only the two shapes the shader items
    // use are expanded, a literal path and `<directory>/**/<file pattern>`; any other glob is refused rather than read
    // as naming nothing.
    private static IEnumerable<string> Expand(string projectDirectory, string include) {
        var pattern = include.Replace(
            newChar: '/',
            oldChar: '\\'
        );
        var recursive = pattern.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: "**/"
        );

        if (recursive < 0) {
            Assert.DoesNotContain(
                actualString: pattern,
                expectedSubstring: "*"
            );

            return [Path.GetFullPath(path: Path.Combine(
                path1: projectDirectory,
                path2: pattern
            ))];
        }

        var filePattern = pattern[(recursive + 3)..];

        Assert.DoesNotContain(
            actualString: filePattern,
            expectedSubstring: "/"
        );

        var directory = Path.Combine(
            path1: projectDirectory,
            path2: pattern[..recursive]
        );

        return (Directory.Exists(path: directory)
            ? Directory.EnumerateFiles(
                enumerationOptions: new EnumerationOptions {
                    MatchType = MatchType.Simple,
                    RecurseSubdirectories = true,
                },
                path: directory,
                searchPattern: filePattern
            )
            : []);
    }
    // Whether one Exclude attribute, relative to its project directory, names a file: a literal path, or
    // `<directory>/**`, every file under that directory.
    private static bool Excludes(string projectDirectory, string exclude, string file) {
        var pattern = exclude.Replace(
            newChar: '/',
            oldChar: '\\'
        );

        if (pattern.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "/**"
        )) {
            var directory = (Path.GetFullPath(path: Path.Combine(
                path1: projectDirectory,
                path2: pattern[..^3]
            )) + Path.DirectorySeparatorChar);

            return Path.GetFullPath(path: file).StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: directory
            );
        }

        Assert.DoesNotContain(
            actualString: pattern,
            expectedSubstring: "*"
        );

        return string.Equals(
            a: Path.GetFullPath(path: file),
            b: Path.GetFullPath(path: Path.Combine(
                path1: projectDirectory,
                path2: pattern
            )),
            comparisonType: StringComparison.OrdinalIgnoreCase
        );
    }
    // A project file and every file it imports by a literal path, which is where build/WorldAssets.targets declares
    // the pipeline sources of the project that imports it.
    private static IEnumerable<XDocument> ProjectAndImports(string project) {
        var document = XDocument.Load(uri: project);

        yield return document;

        foreach (var import in document.Descendants().Where(predicate: static element => (element.Name.LocalName == "Import"))) {
            var imported = (((string?)import.Attribute(name: "Project")) ?? string.Empty);

            if (!imported.Contains(value: '$')) {
                yield return XDocument.Load(uri: Path.GetFullPath(path: Path.Combine(
                    path1: Path.GetDirectoryName(path: project)!,
                    path2: imported
                )));
            }
        }
    }
    // The shader sources a graph document's passes name, relative to the document.
    private static IEnumerable<string> PipelineSources(string pipeline) {
        using var document = System.Text.Json.JsonDocument.Parse(json: File.ReadAllText(path: pipeline));

        foreach (var pass in document.RootElement.GetProperty(propertyName: "passes").EnumerateArray()) {
            yield return Path.GetFullPath(path: Path.Combine(
                path1: Path.GetDirectoryName(path: pipeline)!,
                path2: pass.GetProperty(propertyName: "source").GetString()!
            ));
        }
    }
    // Each project's shader items as its csproj and imports declare them: the project, the item type, and every file the
    // item's includes reach less its excludes, projects in ordinal path order.
    private static IEnumerable<(string Project, string ItemType, string File)> ShaderItems(string root) {
        foreach (var project in Directory.EnumerateFiles(
            path: Path.Combine(
                path1: root,
                path2: "src"
            ),
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*.csproj"
        ).Order(comparer: StringComparer.Ordinal)) {
            var projectDirectory = Path.GetDirectoryName(path: project)!;

            foreach (var element in ProjectAndImports(project: project)
                .SelectMany(selector: static document => document.Descendants())
                .Where(predicate: static element => ShaderItemTypes.Contains(value: element.Name.LocalName))) {
                var excludes = (((string?)element.Attribute(name: "Exclude")) ?? string.Empty).Split(
                    options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
                    separator: ';'
                );

                foreach (var file in (((string?)element.Attribute(name: "Include")) ?? string.Empty).Split(
                    options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
                    separator: ';'
                ).SelectMany(selector: include => Expand(
                    include: include,
                    projectDirectory: projectDirectory
                )).Where(predicate: file => !excludes.Any(predicate: exclude => Excludes(
                    exclude: exclude,
                    file: file,
                    projectDirectory: projectDirectory
                )))) {
                    yield return (project, element.Name.LocalName, file);
                }
            }
        }
    }
    // The build's shader sources and includes, grouped by the project that declares them, in ordinal path order. A
    // graph document among them stands for the sources its passes name.
    private static IEnumerable<string[]> ShippedShaderProjects(string root) {
        foreach (var project in ShaderItems(root: root).GroupBy(keySelector: static item => item.Project)) {
            var files = project
                .Select(selector: static item => item.File)
                .SelectMany(selector: static file => (file.EndsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: ".graph.json"
                )
                    ? PipelineSources(pipeline: file)
                    : [file]))
                .Distinct(comparer: StringComparer.Ordinal)
                .Order(comparer: StringComparer.Ordinal)
                .ToArray();

            if (files.Length > 0) {
                yield return files;
            }
        }
    }
    // A line with its `//` and `/* */` comments removed; `inBlock` carries an open block comment to the next line.
    private static string StripComments(string line, ref bool inBlock) {
        var code = new System.Text.StringBuilder(capacity: line.Length);

        for (var index = 0; (index < line.Length); index++) {
            if (inBlock) {
                if ((line[index] == '*') && ((index + 1) < line.Length) && (line[(index + 1)] == '/')) {
                    inBlock = false;
                    index++;
                }
            } else if ((line[index] == '/') && ((index + 1) < line.Length) && (line[(index + 1)] == '/')) {
                break;
            } else if ((line[index] == '/') && ((index + 1) < line.Length) && (line[(index + 1)] == '*')) {
                inBlock = true;
                index++;
            } else {
                _ = code.Append(value: line[index]);
            }
        }

        return code.ToString();
    }

    /// <summary>Returns every declaration in <paramref name="lines"/> whose register breaks the rule, formatted as
    /// <c>"&lt;path&gt; &lt;name&gt; register(&lt;register&gt;[, spaceS]) binding(N, S)"</c>. A register named by a macro is
    /// checked once for each value <paramref name="defines"/> gives it. A <c>vk::binding</c> must carry its
    /// <c>register</c> on the same line, and a macro register must have a definition.</summary>
    internal static List<string> Violations(string path, IReadOnlyList<string> lines, IReadOnlyDictionary<string, SortedSet<string>> defines) {
        var inBlock = false;
        var violations = new List<string>();

        for (var index = 0; (index < lines.Count); index++) {
            var code = StripComments(
                inBlock: ref inBlock,
                line: lines[index]
            );
            var binding = BindingAttribute().Match(input: code);

            if (!binding.Success) {
                continue;
            }

            var clause = RegisterClause().Match(
                input: code,
                startat: (binding.Index + binding.Length)
            );

            Assert.True(
                condition: clause.Success,
                userMessage: $"{path}:{(index + 1)} declares a Vulkan binding without its register on the same line."
            );

            var bindingNumber = int.Parse(
                provider: CultureInfo.InvariantCulture,
                s: binding.Groups["binding"].Value
            );
            var set = (binding.Groups["set"].Success
                ? int.Parse(
                    provider: CultureInfo.InvariantCulture,
                    s: binding.Groups["set"].Value
                )
                : 0);
            var space = (clause.Groups["space"].Success
                ? int.Parse(
                    provider: CultureInfo.InvariantCulture,
                    s: clause.Groups["space"].Value
                )
                : 0);
            var named = clause.Groups["register"].Value;
            IEnumerable<string> registers = (RegisterName().IsMatch(input: named)
                ? [named]
                : (defines.TryGetValue(
                    key: named,
                    value: out var values
                )
                    ? values
                    : []));

            Assert.True(
                condition: registers.Any(),
                userMessage: $"{path}:{(index + 1)} names register macro {named}, which no shipped shader defines."
            );

            foreach (var register in registers) {
                var parsed = RegisterName().Match(input: register);

                Assert.True(
                    condition: parsed.Success,
                    userMessage: $"{path}:{(index + 1)} register macro {named} expands to '{register}', which is not a register."
                );

                if ((int.Parse(
                    provider: CultureInfo.InvariantCulture,
                    s: parsed.Groups["number"].Value
                ) != bindingNumber) || (space != set)) {
                    var spaceText = (clause.Groups["space"].Success
                        ? $", space{space}"
                        : string.Empty);

                    violations.Add(item: $"{path} {clause.Groups["name"].Value} register({register}{spaceText}) binding({bindingNumber}, {set})");
                }
            }
        }

        return violations;
    }

    [Fact]
    public void Every_shipped_register_equals_its_binding() {
        var root = RepositoryPaths.RequireRoot();
        var checkedFiles = 0;
        var violations = new List<string>();

        foreach (var files in ShippedShaderProjects(root: root)) {
            var lines = files.ToDictionary(
                elementSelector: static file => File.ReadAllLines(path: file),
                keySelector: static file => file
            );
            var defines = new Dictionary<string, SortedSet<string>>(comparer: StringComparer.Ordinal);

            foreach (var match in lines.Values.SelectMany(selector: static text => text).Select(selector: static line => Define().Match(input: line)).Where(predicate: static match => match.Success)) {
                if (!defines.TryGetValue(
                    key: match.Groups["name"].Value,
                    value: out var values
                )) {
                    defines.Add(
                        key: match.Groups["name"].Value,
                        value: (values = new SortedSet<string>(comparer: StringComparer.Ordinal))
                    );
                }

                _ = values.Add(item: match.Groups["value"].Value);
            }

            foreach (var file in files) {
                violations.AddRange(collection: Violations(
                    defines: defines,
                    lines: lines[file],
                    path: Path.GetRelativePath(
                        path: file,
                        relativeTo: root
                    ).Replace(
                        newChar: '/',
                        oldChar: '\\'
                    )
                ));
                checkedFiles++;
            }
        }

        Assert.True(
            condition: (checkedFiles > 0),
            userMessage: "No shipped shader source was found."
        );
        Assert.Empty(collection: violations);
    }
    // The register law holds only the files the item types reach, so an item type that stops reaching any file, such as a
    // renamed item or a moved glob, would leave its kernels unchecked while the law still passed.
    [Fact]
    public void Every_shader_item_type_reaches_a_source_and_the_package_library_reaches_its_resample_kernel() {
        var root = RepositoryPaths.RequireRoot();
        var filesByType = ShaderItems(root: root).ToLookup(
            elementSelector: item => Path.GetRelativePath(
                path: item.File,
                relativeTo: root
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            ),
            keySelector: static item => item.ItemType
        );

        Assert.Equal(
            actual: ShaderItemTypes.Where(predicate: type => !filesByType[type].Any()),
            expected: []
        );
        Assert.Contains(
            collection: filesByType["ComputeShaderSource"],
            expected: "src/Puck.Shaders/Assets/Shaders/Graph/place.comp.hlsl"
        );
    }
    [Fact]
    public void A_register_that_differs_from_its_binding_or_set_is_a_violation() {
        var defines = new Dictionary<string, SortedSet<string>>(comparer: StringComparer.Ordinal) {
            ["MASKS_REGISTER"] = new(collection: ["t3", "t7"], comparer: StringComparer.Ordinal),
        };

        Assert.Equal(
            actual: Violations(
                defines: defines,
                lines: [
                    "[[vk::binding(2, 0)]] StructuredBuffer<uint> equal : register(t2);",
                    "[[vk::binding(1, 3)]] ConstantBuffer<Pass> spaced : register(b1, space3);",
                    "[[vk::binding(4)]] RWTexture2D<float4> images[5] : register(u4); // register(u9) in a comment",
                    "[[vk::push_constant]] ConstantBuffer<Frame> frame : register(b0, space0);",
                    "Texture2D<float4> direct3D : register(t9);",
                    "[[vk::binding(3, 0)]] StructuredBuffer<float> number : register(t4);",
                    "[[vk::binding(1, 0)]] ConstantBuffer<Pass> set : register(b1, space3);",
                    "[[vk::combinedImageSampler]] [[vk::binding(5, 0)]] SamplerState sampler : register(s0);",
                    "[[vk::binding(7, 0)]] StructuredBuffer<uint> masks : register(MASKS_REGISTER);",
                    "/* [[vk::binding(8, 0)]] StructuredBuffer<uint> commented : register(t0); */",
                ],
                path: "probe.hlsl"
            ),
            expected: [
                "probe.hlsl number register(t4) binding(3, 0)",
                "probe.hlsl set register(b1, space3) binding(1, 0)",
                "probe.hlsl sampler register(s0) binding(5, 0)",
                "probe.hlsl masks register(t3) binding(7, 0)",
            ]
        );
    }
}
