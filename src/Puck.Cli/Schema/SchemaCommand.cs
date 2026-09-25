using System.CommandLine;
using System.Text;

using Puck.Cli.Qualification;
using Puck.Shaders;
using Puck.World;

namespace Puck.Cli.Schema;

// The `puck schema` verb: the report-and-write surface for the checked-in JSON Schema of puck.world.definition.v1 — a
// small root (src/Puck.World/Assets/worlds/puck.world.definition.v1.schema.json) plus one file per top-level document
// section and a common.schema.json for shapes shared by more than one section, both under
// src/Puck.World/Assets/worlds/schema/ — and of the TypeScript the dashboard portal types a world document with
// (src/Puck.Dashboard/src/portal/src/document/worldDefinition.generated.ts, WorldSchema.ToTypeScript over the bundle),
// and of the model shape the engine's model walks read instead of describing a type at run time
// (src/Puck.World.Schema/WorldModelShape.generated.cs, WorldModelShapeSource). It also writes the unsplit schemas of
// the silo document, of the puck.counters.report.v1 report `puck counters` writes, of the puck.release.profile.v1
// profile `puck qualify` reads, and of the puck.render.graph.v1 frame-graph document.
// The schema generation and its dedup/split logic live in Puck.World.WorldSchema (src/Puck.World.Schema) and the
// model shape's in WorldModelShapeSource, whose reflective walk no engine process runs; this verb only
// decides where the text goes and, under --check, whether every file agrees with what is on disk — the same
// drift-detection shape `puck architecture --map` establishes for docs/project-map.md's layering block. The one input
// the generator takes from outside the type model is the shipped post-render extension vocabulary: every
// puck.shader.manifest.v1 manifest under src/*/Assets/Shaders, whose id and config schema splice into
// render.extensions[] so an entry's config validates by id.
// Exit 0 wrote/matched, 1 check found drift, 2 usage error or missing repository root.
internal static class SchemaCommand {
    private const string CountersReportRelativePath = "tests/Puck.Counters/puck.counters.report.v1.schema.json";
    private const string ProjectionRelativePath = "src/Puck.World/Assets/worlds/puck.world.projection.v1.schema.json";
    private const string ReleaseProfileRelativePath = "tests/Puck.Qualification/puck.release.profile.v1.schema.json";
    private const string RenderGraphRelativePath = "src/Puck.Shaders/Assets/puck.render.graph.v1.schema.json";
    private const string RootRelativePath = "src/Puck.World/Assets/worlds/puck.world.definition.v1.schema.json";
    private const string SectionsRelativeDirectory = "src/Puck.World/Assets/worlds/schema";
    private const string SiloRelativePath = "src/Puck.World.Silo/Assets/puck.silo.configuration.v1.schema.json";
    private const string TypeScriptRelativePath = "src/Puck.Dashboard/src/portal/src/document/worldDefinition.generated.ts";

    private readonly record struct SchemaFile(string FullPath, string Text);

    /// <summary>Returns the types a file this generator writes is generated from: each fixed output's document type, and
    /// for a world section's schema the type of the <see cref="WorldDefinition"/> property its JSON key names.</summary>
    /// <param name="relativePath">The file, repository-relative with forward slashes.</param>
    /// <returns>The source types, or an empty list when this generator does not write the file.</returns>
    internal static IReadOnlyList<Type> SourceTypesOf(string relativePath) {
        ArgumentNullException.ThrowIfNull(argument: relativePath);

        switch (relativePath) {
            case RootRelativePath:
            case TypeScriptRelativePath:
            case WorldModelShapeSource.RelativePath:
            case ((SectionsRelativeDirectory + "/") + WorldSchema.CommonDefsFileName):
                return [typeof(WorldDefinition)];
            case ProjectionRelativePath:
                return [typeof(WorldProjectionDocument)];
            case SiloRelativePath:
                return [typeof(WorldSiloDefinition)];
            case RenderGraphRelativePath:
                return [typeof(RenderGraphDefinition)];
            case CountersReportRelativePath:
                return [typeof(WorldCountersReport)];
            case ReleaseProfileRelativePath:
                return [typeof(ReleaseProfile)];
        }

        const string SectionSuffix = ".schema.json";

        if (
            !relativePath.StartsWith(comparisonType: StringComparison.Ordinal, value: (SectionsRelativeDirectory + "/")) ||
            !relativePath.EndsWith(comparisonType: StringComparison.Ordinal, value: SectionSuffix)
        ) {
            return [];
        }

        var section = relativePath[(SectionsRelativeDirectory.Length + 1)..^SectionSuffix.Length];
        var property = typeof(WorldDefinition).GetProperties().FirstOrDefault(predicate: candidate => string.Equals(
            a: (candidate.GetCustomAttributes(attributeType: typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), inherit: true).OfType<System.Text.Json.Serialization.JsonPropertyNameAttribute>().FirstOrDefault()?.Name ?? System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(name: candidate.Name)),
            b: section,
            comparisonType: StringComparison.Ordinal
        ));

        return ((property is null)
            ? [typeof(WorldDefinition)]
            : [ElementTypeOf(type: property.PropertyType)]
        );
    }

    // A section's own shape: a list's or a nullable's element, as the schema describes each entry.
    private static Type ElementTypeOf(Type type) {
        if (Nullable.GetUnderlyingType(nullableType: type) is { } underlying) {
            return underlying;
        }

        return ((type.IsGenericType && (type.GetGenericArguments() is [var element]))
            ? element
            : type
        );
    }

    // Every src/<project>/Assets/Shaders tree is a shipped shader asset tree (the shared shader recipe ships its
    // manifests beside its bytecode), so their manifests are the extension vocabulary the runtime catalog sees.
    internal static List<WorldSchema.PostRenderExtensionSchema> LoadPostRenderExtensions(string repositoryRoot) {
        var extensions = new List<WorldSchema.PostRenderExtensionSchema>();
        var seen = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var project in Directory.EnumerateDirectories(path: Path.Combine(
            path1: repositoryRoot,
            path2: "src"
        )).Order(comparer: StringComparer.Ordinal)) {
            var catalog = ShaderSetCatalog.Scan(rootDirectory: Path.Combine(
                path1: project,
                path2: "Assets",
                path3: "Shaders"
            ));

            foreach (var id in catalog.Ids) {
                if (!seen.TryAdd(
                    key: id,
                    value: project
                )) {
                    throw new InvalidDataException(message: $"Shader set '{id}' is shipped by both '{seen[id]}' and '{project}'.");
                }

                extensions.Add(item: new WorldSchema.PostRenderExtensionSchema(
                    Id: id,
                    ConfigSchema: catalog.Load(id: id).ConfigJsonSchema()
                ));
            }
        }

        extensions.Sort(comparison: static (a, b) => string.CompareOrdinal(
            strA: a.Id,
            strB: b.Id
        ));

        return extensions;
    }

    private static SchemaFile At(string repositoryRoot, string relativePath, string text) => new(
        FullPath: Path.Combine(
            path1: repositoryRoot,
            path2: relativePath
        ),
        Text: text
    );
    private static int Bundle(string text, string? output) {
        if (output is null) {
            Console.Out.Write(value: text);

            return CliExit.Success;
        }

        var path = Path.GetFullPath(path: output);

        WriteFile(file: new SchemaFile(
            FullPath: path,
            Text: text
        ));
        Console.Out.WriteLine(value: $"schema: wrote bundle to {CliPaths.ToDisplay(fullPath: path)} ({text.Length} chars).");

        return CliExit.Success;
    }
    private static int Check(IReadOnlyList<SchemaFile> files, IReadOnlyList<SchemaFile> sections, SchemaFile common, string sectionsDirectory) {
        var problems = new List<string>();

        foreach (var file in files.Concat(second: sections).Append(element: common)) {
            CheckFile(
                file: file,
                problems: problems
            );
        }

        var expectedNames = ExpectedFileNames(
            common: common,
            sections: sections
        );

        if (Directory.Exists(path: sectionsDirectory)) {
            foreach (var existing in Directory.EnumerateFiles(
                path: sectionsDirectory,
                searchPattern: "*.schema.json"
            )) {
                if (!expectedNames.Contains(item: Path.GetFileName(path: existing))) {
                    problems.Add(item: $"{CliPaths.ToDisplay(fullPath: existing)} is an ORPHAN — no current document section produces it.");
                }
            }
        }

        if (problems.Count == 0) {
            Console.Out.WriteLine(value: $"schema: {string.Join(separator: ", ", values: files.Select(selector: static file => CliPaths.ToDisplay(fullPath: file.FullPath)))}, and {(sections.Count + 1)} schema/ file(s) match the generated schema.");

            return CliExit.Success;
        }

        Console.Error.WriteLine(value: $"schema: {problems.Count} problem(s) found — regenerate with 'puck schema'.");

        foreach (var problem in problems) {
            Console.Error.WriteLine(value: $"  {problem}");
        }

        return CliExit.Failed;
    }
    private static void CheckFile(SchemaFile file, List<string> problems) {
        if (!File.Exists(path: file.FullPath)) {
            problems.Add(item: $"{CliPaths.ToDisplay(fullPath: file.FullPath)} is MISSING — run 'puck schema' first.");

            return;
        }

        // CRLF is checkout noise, never content: git normalizes line endings at commit, so a CRLF working copy of a
        // canonical LF file must compare EQUAL — without this, every file on a Windows checkout reports stale and the
        // one real finding drowns in false positives.
        var onDisk = File.ReadAllText(path: file.FullPath).Replace(
            newValue: "\n",
            oldValue: "\r\n"
        );

        if (string.Equals(
            a: onDisk,
            b: file.Text,
            comparisonType: StringComparison.Ordinal
        )) {
            return;
        }

        var (lineNumber, onDiskLine, generatedLine) = FirstDifference(
            onDisk: onDisk,
            generated: file.Text
        );

        problems.Add(item: $"{CliPaths.ToDisplay(fullPath: file.FullPath)} is STALE — first difference at line {lineNumber}: checked-in [{onDiskLine}] vs generated [{generatedLine}].");
    }
    private static HashSet<string> ExpectedFileNames(IReadOnlyList<SchemaFile> sections, SchemaFile common) =>
        sections
            .Select(selector: s => Path.GetFileName(path: s.FullPath))
            .Append(element: Path.GetFileName(path: common.FullPath))
            .ToHashSet(comparer: StringComparer.OrdinalIgnoreCase);
    // Both texts are LF-only by the time they arrive here (generated text by construction, on-disk text by
    // CheckFile's CRLF normalization), so splitting on '\n' alone lines them up one-for-one.
    private static (int LineNumber, string OnDisk, string Generated) FirstDifference(string onDisk, string generated) {
        var onDiskLines = onDisk.Split(separator: '\n');
        var generatedLines = generated.Split(separator: '\n');
        var count = Math.Max(
            val1: onDiskLines.Length,
            val2: generatedLines.Length
        );

        for (var index = 0; (index < count); index++) {
            var onDiskLine = ((index < onDiskLines.Length)
                ? onDiskLines[index]
                : "(line absent)"
            );
            var generatedLine = ((index < generatedLines.Length)
                ? generatedLines[index]
                : "(line absent)"
            );

            if (!string.Equals(
                a: onDiskLine,
                b: generatedLine,
                comparisonType: StringComparison.Ordinal
            )) {
                return ((index + 1), onDiskLine, generatedLine);
            }
        }

        return (0, string.Empty, string.Empty);
    }
    private static int Run(bool bundle, bool check, string? output) {
        if (
            (output is not null) &&
            !bundle
        ) {
            return CliExit.Refuse(
                verb: "schema",
                what: "--output",
                why: "only --bundle writes to a path of the caller's choosing; the checked-in files have fixed homes"
            );
        }

        if (
            bundle &&
            check
        ) {
            return CliExit.Refuse(
                verb: "schema",
                what: "--bundle with --check",
                why: "the bundle is not a checked-in artifact, so there is nothing to compare it with"
            );
        }

        if (!WorldSchema.HasXmlDocumentation) {
            Console.Error.WriteLine(value: "schema: an XML documentation file is missing beside its assembly — the generated schema will carry no descriptions.");
        }
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }

        var postRenderExtensions = LoadPostRenderExtensions(repositoryRoot: repositoryRoot);
        var split = WorldSchema.Export(postRenderExtensions: postRenderExtensions);
        var bundled = WorldSchema.Bundle(split: split);

        if (bundle) {
            return Bundle(
                output: output,
                text: WorldSchema.ToCanonicalText(node: bundled)
            );
        }

        var sectionsDirectory = Path.Combine(
            path1: repositoryRoot,
            path2: SectionsRelativeDirectory
        );
        var files = new[] {
            At(
                relativePath: RootRelativePath,
                repositoryRoot: repositoryRoot,
                text: WorldSchema.ToCanonicalText(node: split.Root)
            ),
            At(
                relativePath: ProjectionRelativePath,
                repositoryRoot: repositoryRoot,
                text: WorldSchema.ToCanonicalText(node: WorldSchema.ExportProjection(postRenderExtensions: postRenderExtensions))
            ),
            At(
                relativePath: SiloRelativePath,
                repositoryRoot: repositoryRoot,
                text: WorldSchema.ToCanonicalText(node: WorldSchema.ExportSilo())
            ),
            At(
                relativePath: RenderGraphRelativePath,
                repositoryRoot: repositoryRoot,
                text: WorldSchema.ToCanonicalText(node: WorldSchema.ExportDocument(
                    options: RenderGraphJsonContext.Default.Options,
                    schemaId: RenderGraphSchemas.Graph,
                    title: $"Puck frame graph ({RenderGraphSchemas.Graph})",
                    type: typeof(RenderGraphDefinition)
                ))
            ),
            At(
                relativePath: CountersReportRelativePath,
                repositoryRoot: repositoryRoot,
                text: WorldSchema.ToCanonicalText(node: WorldSchema.ExportCountersReport())
            ),
            At(
                relativePath: ReleaseProfileRelativePath,
                repositoryRoot: repositoryRoot,
                text: WorldSchema.ToCanonicalText(node: WorldSchema.ExportDocument(
                    options: QualificationJsonContext.Default.Options,
                    schemaId: ReleaseProfile.SchemaVersion,
                    title: $"Puck release profile ({ReleaseProfile.SchemaVersion})",
                    type: typeof(ReleaseProfile)
                ))
            ),
            At(
                relativePath: TypeScriptRelativePath,
                repositoryRoot: repositoryRoot,
                text: WorldSchema.ToTypeScript(bundle: bundled)
            ),
            At(
                relativePath: WorldModelShapeSource.RelativePath,
                repositoryRoot: repositoryRoot,
                text: WorldModelShapeSource.Render(shapes: WorldModelShapeSource.Reflect())
            ),
        };
        var sections = split.Sections.Select(selector: s => new SchemaFile(
            FullPath: Path.Combine(
                path1: sectionsDirectory,
                path2: $"{s.Name}.schema.json"
            ),
            Text: WorldSchema.ToCanonicalText(node: s.Node)
        )).ToList();
        var common = new SchemaFile(
            FullPath: Path.Combine(
                path1: sectionsDirectory,
                path2: WorldSchema.CommonDefsFileName
            ),
            Text: WorldSchema.ToCanonicalText(node: split.Common)
        );

        return (check
            ? Check(
                common: common,
                files: files,
                sections: sections,
                sectionsDirectory: sectionsDirectory
            )
            : Write(
                common: common,
                files: files,
                sections: sections,
                sectionsDirectory: sectionsDirectory
            )
        );
    }
    private static int Write(IReadOnlyList<SchemaFile> files, IReadOnlyList<SchemaFile> sections, SchemaFile common, string sectionsDirectory) {
        foreach (var file in files.Concat(second: sections).Append(element: common)) {
            WriteFile(file: file);
        }

        var expectedNames = ExpectedFileNames(
            common: common,
            sections: sections
        );
        var removed = new List<string>();

        if (Directory.Exists(path: sectionsDirectory)) {
            foreach (var existing in Directory.EnumerateFiles(
                path: sectionsDirectory,
                searchPattern: "*.schema.json"
            )) {
                if (!expectedNames.Contains(item: Path.GetFileName(path: existing))) {
                    File.Delete(path: existing);
                    removed.Add(item: CliPaths.ToDisplay(fullPath: existing));
                }
            }
        }

        Console.Out.WriteLine(value: $"schema: wrote {string.Join(separator: " + ", values: files.Select(selector: static file => CliPaths.ToDisplay(fullPath: file.FullPath)))} + {sections.Count} section file(s) + common.schema.json.");

        if (removed.Count > 0) {
            Console.Out.WriteLine(value: $"schema: removed {removed.Count} stale section file(s) no longer produced: {string.Join(
                separator: ", ",
                values: removed
            )}");
        }

        return CliExit.Success;
    }
    private static void WriteFile(SchemaFile file) {
        var directory = Path.GetDirectoryName(path: file.FullPath);

        if (
            (directory is { Length: > 0 }) &&
            !Directory.Exists(path: directory)
        ) {
            Directory.CreateDirectory(path: directory);
        }

        File.WriteAllText(
            path: file.FullPath,
            contents: file.Text,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
    }

    public static Command Create() {
        var bundleOption = new Option<bool>(name: "--bundle") {
            Description = "Emit the single-file schema, every cross-file $ref resolved through named $defs, instead of writing the checked-in files: to --output when given, else standard output.",
        };
        var checkOption = CliOptions.Check(description: "Regenerate in memory and compare every checked-in schema file, the portal's TypeScript and the model shape with what is on disk; write nothing, exit 1 naming each missing, orphaned or stale file.");
        var outputOption = CliOptions.Output(description: "Where --bundle writes the single-file schema.");
        var command = new Command(
            description: "Generate the world, silo, counters-report, release-profile and frame-graph JSON Schemas, the dashboard's TypeScript types and the engine's model shape, or check them.",
            name: "schema"
        ) { bundleOption, checkOption, outputOption };

        command.Detail(detail: """
            Generated from WorldDefinition (src/Puck.World.Schema/WorldDefinition.cs) over the same
            source-generated WorldJsonContext the engine loads a world document through
            (System.Text.Json's JsonSchemaExporter), never hand-maintained. Descriptions come from
            the model assemblies' XML documentation files; when one is missing the schema still
            writes, with no descriptions, and this verb says so on standard error.
            render.extensions[] takes its id vocabulary and per-id config schema from the shipped
            puck.shader.manifest.v1 manifests under src/*/Assets/Shaders.

            Written to:
              src/Puck.World/Assets/worlds/puck.world.definition.v1.schema.json (the root)
              src/Puck.World/Assets/worlds/schema/*.schema.json (one per document section, plus
                common.schema.json for shapes more than one section references)
              src/Puck.World/Assets/worlds/puck.world.projection.v1.schema.json (the egress document)
              src/Puck.World.Silo/Assets/puck.silo.configuration.v1.schema.json (the silo document)
              tests/Puck.Counters/puck.counters.report.v1.schema.json (the report puck counters writes)
              tests/Puck.Qualification/puck.release.profile.v1.schema.json (the profile puck qualify reads)
              src/Puck.Shaders/Assets/puck.render.graph.v1.schema.json (the frame-graph document)
              src/Puck.Dashboard/src/portal/src/document/worldDefinition.generated.ts (the portal's
                WorldDefinition types, emitted from the single-file schema)
              src/Puck.World.Schema/WorldModelShape.generated.cs (every type reachable from
                WorldDefinition with its members, arms and element type, the table the engine's
                model walks read instead of describing a type at run time)
            A section file no current model produces is deleted.

            Exit codes: 0 wrote or matched, 1 --check found drift, 2 usage error or missing
            repository root.
            """);
        command.SetAction(action: parseResult => Run(
            bundle: parseResult.GetValue(option: bundleOption),
            check: parseResult.GetValue(option: checkOption),
            output: parseResult.GetValue(option: outputOption)
        ));

        return command;
    }
}
