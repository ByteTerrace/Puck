using System.CommandLine;
using System.Text;

using Puck.Cli.Qualification;
using Puck.Shaders;
using Puck.World;

namespace Puck.Cli.Schema;

// The `puck schema` verb: the report-and-write surface for the checked-in JSON Schema of puck.world.definition.v1 — a
// small root (src/Puck.World.Schema/Schemas/puck.world.definition.v1.schema.json) plus one file per top-level document
// section and a common.schema.json for shapes shared by more than one section, both under
// src/Puck.World.Schema/Schemas/schema/ — and of the TypeScript the dashboard portal types a world document with
// (src/Puck.Dashboard/src/portal/src/document/worldDefinition.generated.ts, WorldSchema.ToTypeScript over the bundle),
// and of the model shape the engine's model walks read instead of describing a type at run time
// (src/Puck.World.Schema/WorldModelShape.generated.cs, WorldModelShapeSource). It also writes the unsplit schemas of
// the silo document, of the puck.counters.report.v1 report `puck counters` writes, of the puck.release.profile.v1
// profile `puck qualify` reads, and of the puck.render.graph.v1 frame-graph document.
// The schema generation and its dedup/split logic live in Puck.World.WorldSchema (src/Puck.World.Schema) and the
// model shape's in WorldModelShapeSource, whose reflective walk no engine process runs; this verb only
// decides where the text goes and, under --check, whether every file agrees with what is on disk — the same
// drift-detection shape `puck architecture --map` establishes for docs/project-map.md's layering block. The one input
// the generator takes from outside the type model is the post-process package vocabulary: every post-process package
// of the engine's render graph catalog, whose id and config schema splice into views.post[] so a row's config validates
// by package.
// Exit 0 wrote/matched, 1 check found drift, 2 usage error or missing repository root.
public static class SchemaCommand {
    private const string CountersReportRelativePath = "tests/Puck.Counters/puck.counters.report.v1.schema.json";
    private const string ProjectionRelativePath = "src/Puck.World.Schema/Schemas/puck.world.projection.v1.schema.json";
    private const string ReleaseProfileRelativePath = "tests/Puck.Qualification/puck.release.profile.v1.schema.json";
    private const string RenderGraphRelativePath = "src/Puck.Shaders/Assets/puck.render.graph.v1.schema.json";
    private const string RootRelativePath = "src/Puck.World.Schema/Schemas/puck.world.definition.v1.schema.json";
    private const string SectionsRelativeDirectory = "src/Puck.World.Schema/Schemas/schema";
    private const string SiloRelativePath = "src/Puck.World.Silo/Assets/puck.silo.configuration.v1.schema.json";
    private const string TypeScriptRelativePath = "src/Puck.Dashboard/src/portal/src/document/worldDefinition.generated.ts";


    // The engine catalog's post-process packages, each with its config schema, the vocabulary views.post[] validates
    // against.
    public static List<WorldSchema.PostProcessPackageSchema> PostProcessPackages() => [.. RenderGraphPackageCatalog.Engine.Packages
        .Where(predicate: static package => package.IsPostProcess)
        .Select(selector: static package => new WorldSchema.PostProcessPackageSchema(
            ConfigSchema: ShaderConfigBinding.JsonSchema(
                description: package.Summary,
                schema: package.Config
            ),
            Package: package.Id
        ))];

    private static int Bundle(string text, string? output) {
        if (output is null) {
            Console.Out.Write(value: text);

            return CliExit.Success;
        }

        var path = Path.GetFullPath(path: output);

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
        File.WriteAllText(
            path: path,
            contents: text,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
        Console.Out.WriteLine(value: $"schema: wrote bundle to {CliPaths.ToDisplay(fullPath: path)} ({text.Length} chars).");

        return CliExit.Success;
    }
    // A section file no current document section produces is an orphan: named under --check, deleted when writing.
    private static List<string> Orphans(string sectionsDirectory, IReadOnlyList<GeneratedFile> sections) {
        var expectedNames = sections
            .Select(selector: static section => Path.GetFileName(path: section.RelativePath))
            .ToHashSet(comparer: StringComparer.OrdinalIgnoreCase);

        return [.. (Directory.Exists(path: sectionsDirectory)
            ? Directory.EnumerateFiles(path: sectionsDirectory, searchPattern: "*.schema.json").Order(comparer: StringComparer.Ordinal)
                .Where(predicate: existing => !expectedNames.Contains(item: Path.GetFileName(path: existing)))
            : [])];
    }
    private static int Run(bool bundle, bool check, string? output, bool bootstrap, TimeProvider clock) {
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

        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }
        if (bootstrap && !SchemaBootstrap.IsBootstrap) {
            return SchemaBootstrap.Run(bundle: bundle, check: check, clock: clock, output: output, repositoryRoot: repositoryRoot);
        }
        if (!WorldSchema.HasXmlDocumentation) {
            Console.Error.WriteLine(value: "schema: an XML documentation file is missing beside its assembly — the generated schema will carry no descriptions.");
        }

        var postProcessPackages = PostProcessPackages();
        var split = WorldSchema.Export(postProcessPackages: postProcessPackages);
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
            new GeneratedFile(
                RelativePath: RootRelativePath,
                Text: WorldSchema.ToCanonicalText(node: split.Root)
            ),
            new GeneratedFile(
                RelativePath: ProjectionRelativePath,
                Text: WorldSchema.ToCanonicalText(node: WorldSchema.ExportProjection(postProcessPackages: postProcessPackages))
            ),
            new GeneratedFile(
                RelativePath: SiloRelativePath,
                Text: WorldSchema.ToCanonicalText(node: WorldSchema.ExportSilo())
            ),
            new GeneratedFile(
                RelativePath: RenderGraphRelativePath,
                Text: WorldSchema.ToCanonicalText(node: WorldSchema.ExportDocument(
                    // The graph's own records and the pipeline model's resources and passes it embeds.
                    documentation: [typeof(RenderGraphDefinition), typeof(ShaderPipelineResource)],
                    options: RenderGraphJsonContext.Default.Options,
                    schemaId: RenderGraphSchemas.Graph,
                    title: $"Puck frame graph ({RenderGraphSchemas.Graph})",
                    type: typeof(RenderGraphDefinition)
                ))
            ),
            new GeneratedFile(
                RelativePath: CountersReportRelativePath,
                Text: WorldSchema.ToCanonicalText(node: WorldSchema.ExportCountersReport())
            ),
            new GeneratedFile(
                RelativePath: ReleaseProfileRelativePath,
                Text: WorldSchema.ToCanonicalText(node: WorldSchema.ExportDocument(
                    options: QualificationJsonContext.Default.Options,
                    schemaId: ReleaseProfile.SchemaVersion,
                    title: $"Puck release profile ({ReleaseProfile.SchemaVersion})",
                    type: typeof(ReleaseProfile)
                ))
            ),
            new GeneratedFile(
                RelativePath: TypeScriptRelativePath,
                Text: WorldSchema.ToTypeScript(bundle: bundled)
            ),
            new GeneratedFile(
                RelativePath: WorldModelShapeSource.RelativePath,
                Text: WorldModelShapeSource.Render(shapes: WorldModelShapeSource.Reflect())
            ),
        };
        var sections = split.Sections.Select(selector: s => new GeneratedFile(
            RelativePath: $"{SectionsRelativeDirectory}/{s.Name}.schema.json",
            Text: WorldSchema.ToCanonicalText(node: s.Node)
        )).Append(element: new GeneratedFile(
            RelativePath: $"{SectionsRelativeDirectory}/{WorldSchema.CommonDefsFileName}",
            Text: WorldSchema.ToCanonicalText(node: split.Common)
        )).ToList();
        var orphans = Orphans(sections: sections, sectionsDirectory: sectionsDirectory);
        var ok = CliGeneratedFile.WriteOrCheckAll(
            check: check,
            detail: "",
            eachFile: false,
            files: [.. files, .. sections],
            repositoryRoot: repositoryRoot,
            source: "the generated schema",
            verb: "schema"
        );

        if (check) {
            foreach (var orphan in orphans) {
                Console.Error.WriteLine(value: $"schema: {CliPaths.ToDisplay(fullPath: orphan)} is an ORPHAN — no current document section produces it.");
            }

            if (ok && (orphans.Count == 0)) {
                Console.Out.WriteLine(value: $"schema: {string.Join(separator: ", ", values: files.Select(selector: static file => file.RelativePath))}, and {sections.Count} schema/ file(s) match the generated schema.");

                return CliExit.Success;
            }

            return CliExit.Failed;
        }

        foreach (var orphan in orphans) {
            File.Delete(path: orphan);
        }

        Console.Out.WriteLine(value: $"schema: wrote {string.Join(separator: " + ", values: files.Select(selector: static file => file.RelativePath))} + {(sections.Count - 1)} section file(s) + common.schema.json.");

        if (orphans.Count > 0) {
            Console.Out.WriteLine(value: $"schema: removed {orphans.Count} stale section file(s) no longer produced: {string.Join(
                separator: ", ",
                values: orphans.Select(selector: static orphan => CliPaths.ToDisplay(fullPath: orphan))
            )}");
        }

        return CliExit.Success;
    }
    public static Command Create(TimeProvider? clock = null) {
        var bootstrapOption = new Option<bool>(name: "--bootstrap") {
            Description = "Build a schema-only CLI from the current model without stale generated getters, using private artifacts, then run this schema operation.",
        };
        var bundleOption = new Option<bool>(name: "--bundle") {
            Description = "Emit the single-file schema, every cross-file $ref resolved through named $defs, instead of writing the checked-in files: to --output when given, else standard output.",
        };
        var checkOption = CliOptions.Check(description: "Regenerate in memory and compare every checked-in schema file, the portal's TypeScript and the model shape with what is on disk; write nothing, exit 1 naming each missing, orphaned or stale file.");
        var outputOption = CliOptions.Output(description: "Where --bundle writes the single-file schema.");
        var command = new Command(
            description: "Generate the world, silo, counters-report, release-profile and frame-graph JSON Schemas, the dashboard's TypeScript types and the engine's model shape, or check them.",
            name: "schema"
        ) { bootstrapOption, bundleOption, checkOption, outputOption };

        command.Detail(detail: """
            Generated from WorldDefinition (src/Puck.World.Schema/WorldDefinition.cs) over the same
            source-generated WorldJsonContext the engine loads a world document through
            (System.Text.Json's JsonSchemaExporter), never hand-maintained. Descriptions come from
            the model assemblies' XML documentation files; when one is missing the schema still
            writes, with no descriptions, and this verb says so on standard error.
            views.post[] takes its package vocabulary and per-package config schema from the
            post-process packages of the engine's render graph catalog.

            Written to:
              src/Puck.World.Schema/Schemas/puck.world.definition.v1.schema.json (the root)
              src/Puck.World.Schema/Schemas/schema/*.schema.json (one per document section, plus
                common.schema.json for shapes more than one section references)
              src/Puck.World.Schema/Schemas/puck.world.projection.v1.schema.json (the egress document)
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

            --bootstrap first builds the current source model in a private run directory, omitting
            only WorldModelShape.generated.cs. That temporary CLI accepts only schema; reading
            its absent generated table refuses. The existing generator writes or checks the same
            files. Normal bin/obj outputs are untouched; rebuild normally after regeneration.
            A failed build or generator keeps its run directory and logs.

            Exit codes: 0 wrote or matched, 1 --check found drift, 2 usage error or missing
            repository root.
            """);
        command.SetAction(action: parseResult => Run(
            bundle: parseResult.GetValue(option: bundleOption),
            check: parseResult.GetValue(option: checkOption),
            output: parseResult.GetValue(option: outputOption),
            bootstrap: parseResult.GetValue(option: bootstrapOption),
            clock: (clock ?? TimeProvider.System)
        ));

        return command;
    }
}
