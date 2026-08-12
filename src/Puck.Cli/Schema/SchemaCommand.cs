using System.Text;

using Puck.World;

namespace Puck.Cli.Schema;

// The `puck schema` verb: the report-and-write surface for the checked-in JSON Schema of puck.world.def.v1 — a
// small root (src/Puck.World/Assets/worlds/puck.world.def.v1.schema.json) plus one file per top-level document
// section and a common.schema.json for shapes shared by more than one section, both under
// src/Puck.World/Assets/worlds/schema/. All generation and the dedup/split logic live in Puck.World.WorldSchema
// (src/Puck.World.Data); this verb only decides where the text goes and, under --check, whether every file agrees
// with what is on disk — the same drift-detection shape `puck architecture --map` establishes for
// docs/project-map.md's layering block.
// Exit 0 wrote/matched, 1 check found drift, 2 usage error or missing repository root.
internal static class SchemaCommand {
    private const string HelpText =
        """
        puck schema — generate the JSON Schema for puck.world.def.v1

        Usage: puck schema [options] [bundle-path]

        Options:
          --check          regenerate in memory and compare the root, every section file,
                            and common.schema.json against what is on disk; write nothing,
                            exit 1 with a drift report (missing file, orphan file, or a
                            content difference naming the first differing line) on any
                            disagreement
          --stdout          emit the generated ROOT document to stdout instead of writing
                            the checked-in files (skips --check)
          --bundle [path]   emit the single-file equivalent with every cross-file $ref
                            inlined (a genuinely recursive shape keeps its $ref, same as
                            the un-split generator always did) — not a checked-in artifact;
                            written to [path] if given, else stdout
          -h, --help        this text

        Generated from WorldDefinition (src/Puck.World.Data/WorldDefinition.cs) over the SAME
        source-generated WorldJsonContext the engine loads a world document through
        (System.Text.Json's JsonSchemaExporter) — never hand-maintained. Descriptions are
        pulled from Puck.World.Data.xml beside the assembly; when that file is missing the
        schema still writes, with no descriptions, and this verb says so on stderr.

        Written to: src/Puck.World/Assets/worlds/puck.world.def.v1.schema.json (root),
        src/Puck.World/Assets/worlds/puck.world.projection.v1.schema.json (the egress
        document, one unsplit file), and src/Puck.World/Assets/worlds/schema/*.schema.json
        (one file per document section, plus common.schema.json for shapes more than one
        section references).
        Exit codes: 0 wrote or matched, 1 check found drift, 2 usage error or missing
        repository root.
        """;
    private const string RootRelativePath = "src/Puck.World/Assets/worlds/puck.world.def.v1.schema.json";
    private const string ProjectionRelativePath = "src/Puck.World/Assets/worlds/puck.world.projection.v1.schema.json";
    private const string SectionsRelativeDirectory = "src/Puck.World/Assets/worlds/schema";

    private readonly record struct SchemaFile(string FullPath, string Text);

    public static int Run(string[] args) {
        var scanner = new ArgScanner().Flag(name: "h").Flag(name: "help").Flag(name: "check").Flag(name: "stdout").Flag(name: "bundle");

        if (!scanner.Parse(args: args)) {
            Console.Error.WriteLine(value: $"schema: {scanner.Error}");

            return 2;
        }

        if (scanner.Has(name: "h") || scanner.Has(name: "help")) {
            Console.Out.WriteLine(value: HelpText);

            return 0;
        }

        if (!WorldSchema.HasXmlDocumentation) {
            Console.Error.WriteLine(value: "schema: Puck.World.Data.xml not found beside the assembly — the generated schema will carry no descriptions.");
        }

        var split = WorldSchema.Export();

        if (scanner.Has(name: "bundle")) {
            return Bundle(split: split, path: ((scanner.Positionals.Count > 0) ? scanner.Positionals[0] : null));
        }

        if (scanner.Has(name: "stdout")) {
            Console.Out.Write(value: WorldSchema.ToCanonicalText(node: split.Root));

            return 0;
        }

        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        var (root, sections, common, sectionsDirectory) = BuildFileSet(repositoryRoot: repositoryRoot, split: split);
        var projection = new SchemaFile(
            FullPath: Path.Combine(path1: repositoryRoot, path2: ToNativePath(relativePath: ProjectionRelativePath)),
            Text: WorldSchema.ToCanonicalText(node: WorldSchema.ExportProjection()));

        return (scanner.Has(name: "check")
            ? Check(root: root, projection: projection, sections: sections, common: common, sectionsDirectory: sectionsDirectory)
            : Write(root: root, projection: projection, sections: sections, common: common, sectionsDirectory: sectionsDirectory));
    }

    private static int Bundle(WorldSchema.SplitSchema split, string? path) {
        var text = WorldSchema.ToCanonicalText(node: WorldSchema.Bundle(split: split));

        if (path is null) {
            Console.Out.Write(value: text);

            return 0;
        }

        var directory = Path.GetDirectoryName(path: path);

        if ((directory is { Length: > 0 }) && !Directory.Exists(path: directory)) {
            Directory.CreateDirectory(path: directory);
        }

        File.WriteAllText(path: path, contents: text, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.Out.WriteLine(value: $"schema: wrote bundle to {path} ({text.Length} chars).");

        return 0;
    }
    private static (SchemaFile Root, IReadOnlyList<SchemaFile> Sections, SchemaFile Common, string SectionsDirectory) BuildFileSet(string repositoryRoot, WorldSchema.SplitSchema split) {
        var rootPath = Path.Combine(path1: repositoryRoot, path2: ToNativePath(relativePath: RootRelativePath));
        var sectionsDirectory = Path.Combine(path1: repositoryRoot, path2: ToNativePath(relativePath: SectionsRelativeDirectory));
        var root = new SchemaFile(FullPath: rootPath, Text: WorldSchema.ToCanonicalText(node: split.Root));
        var sections = split.Sections
            .Select(selector: s => new SchemaFile(FullPath: Path.Combine(path1: sectionsDirectory, path2: $"{s.Name}.schema.json"), Text: WorldSchema.ToCanonicalText(node: s.Node)))
            .ToList();
        var common = new SchemaFile(FullPath: Path.Combine(path1: sectionsDirectory, path2: WorldSchema.CommonDefsFileName), Text: WorldSchema.ToCanonicalText(node: split.Common));

        return (root, sections, common, sectionsDirectory);
    }
    private static string ToNativePath(string relativePath) =>
        relativePath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar);
    private static int Write(SchemaFile root, SchemaFile projection, IReadOnlyList<SchemaFile> sections, SchemaFile common, string sectionsDirectory) {
        WriteFile(file: root);
        WriteFile(file: projection);

        foreach (var section in sections) {
            WriteFile(file: section);
        }

        WriteFile(file: common);

        var expectedNames = ExpectedFileNames(sections: sections, common: common);
        var removed = new List<string>();

        if (Directory.Exists(path: sectionsDirectory)) {
            foreach (var existing in Directory.EnumerateFiles(path: sectionsDirectory, searchPattern: "*.schema.json")) {
                if (!expectedNames.Contains(item: Path.GetFileName(path: existing))) {
                    File.Delete(path: existing);
                    removed.Add(item: CliPaths.ToDisplay(fullPath: existing));
                }
            }
        }

        Console.Out.WriteLine(value: $"schema: wrote {CliPaths.ToDisplay(fullPath: root.FullPath)} + {CliPaths.ToDisplay(fullPath: projection.FullPath)} + {sections.Count} section file(s) + common.schema.json.");

        if (removed.Count > 0) {
            Console.Out.WriteLine(value: $"schema: removed {removed.Count} stale section file(s) no longer produced: {string.Join(separator: ", ", values: removed)}");
        }

        return 0;
    }
    private static void WriteFile(SchemaFile file) {
        var directory = Path.GetDirectoryName(path: file.FullPath);

        if ((directory is { Length: > 0 }) && !Directory.Exists(path: directory)) {
            Directory.CreateDirectory(path: directory);
        }

        File.WriteAllText(path: file.FullPath, contents: file.Text, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
    private static int Check(SchemaFile root, SchemaFile projection, IReadOnlyList<SchemaFile> sections, SchemaFile common, string sectionsDirectory) {
        var problems = new List<string>();

        CheckFile(file: root, problems: problems);
        CheckFile(file: projection, problems: problems);

        foreach (var section in sections) {
            CheckFile(file: section, problems: problems);
        }

        CheckFile(file: common, problems: problems);

        var expectedNames = ExpectedFileNames(sections: sections, common: common);

        if (Directory.Exists(path: sectionsDirectory)) {
            foreach (var existing in Directory.EnumerateFiles(path: sectionsDirectory, searchPattern: "*.schema.json")) {
                if (!expectedNames.Contains(item: Path.GetFileName(path: existing))) {
                    problems.Add(item: $"{CliPaths.ToDisplay(fullPath: existing)} is an ORPHAN — no current document section produces it.");
                }
            }
        }

        if (problems.Count == 0) {
            Console.Out.WriteLine(value: $"schema: {CliPaths.ToDisplay(fullPath: root.FullPath)}, {CliPaths.ToDisplay(fullPath: projection.FullPath)}, and {(sections.Count + 1)} schema/ file(s) match the generated schema.");

            return 0;
        }

        Console.Error.WriteLine(value: $"schema: {problems.Count} problem(s) found — regenerate with 'puck schema'.");

        foreach (var problem in problems) {
            Console.Error.WriteLine(value: $"  {problem}");
        }

        return 1;
    }
    private static HashSet<string> ExpectedFileNames(IReadOnlyList<SchemaFile> sections, SchemaFile common) =>
        sections
            .Select(selector: s => Path.GetFileName(path: s.FullPath))
            .Append(element: Path.GetFileName(path: common.FullPath))
            .ToHashSet(comparer: StringComparer.OrdinalIgnoreCase);
    private static void CheckFile(SchemaFile file, List<string> problems) {
        if (!File.Exists(path: file.FullPath)) {
            problems.Add(item: $"{CliPaths.ToDisplay(fullPath: file.FullPath)} is MISSING — run 'puck schema' first.");

            return;
        }

        // CRLF is checkout noise, never content: git normalizes line endings at commit, so a CRLF working copy of a
        // canonical LF file must compare EQUAL — without this, every file on a Windows checkout reports stale and the
        // one real finding drowns in false positives.
        var onDisk = File.ReadAllText(path: file.FullPath).Replace(oldValue: "\r\n", newValue: "\n");

        if (string.Equals(a: onDisk, b: file.Text, comparisonType: StringComparison.Ordinal)) {
            return;
        }

        var (lineNumber, onDiskLine, generatedLine) = FirstDifference(onDisk: onDisk, generated: file.Text);

        problems.Add(item: $"{CliPaths.ToDisplay(fullPath: file.FullPath)} is STALE — first difference at line {lineNumber}: checked-in [{onDiskLine}] vs generated [{generatedLine}].");
    }

    // Both texts are LF-only by the time they arrive here (generated text by construction, on-disk text by
    // CheckFile's CRLF normalization), so splitting on '\n' alone lines them up one-for-one.
    private static (int LineNumber, string OnDisk, string Generated) FirstDifference(string onDisk, string generated) {
        var onDiskLines = onDisk.Split(separator: '\n');
        var generatedLines = generated.Split(separator: '\n');
        var count = Math.Max(val1: onDiskLines.Length, val2: generatedLines.Length);

        for (var index = 0; (index < count); index++) {
            var onDiskLine = ((index < onDiskLines.Length) ? onDiskLines[index] : "(line absent)");
            var generatedLine = ((index < generatedLines.Length) ? generatedLines[index] : "(line absent)");

            if (!string.Equals(a: onDiskLine, b: generatedLine, comparisonType: StringComparison.Ordinal)) {
                return ((index + 1), onDiskLine, generatedLine);
            }
        }

        return (0, string.Empty, string.Empty);
    }
}
