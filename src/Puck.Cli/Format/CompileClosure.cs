using System.Text.Json;

namespace Puck.Cli.Format;

// One project's compile-time inputs, taken from the build system instead of guessed from a directory
// scan: the reference set and the `Compile` items, which carry the sources a project links in from outside
// its own directory. `dotnet msbuild -t:FindReferenceAssembliesForReferences
// -getItem:ReferencePathWithRefAssemblies` reports exactly the list the C# compiler receives on its
// `/reference:` switch for the requested configuration: the framework reference pack, every package
// assembly the restore resolved, and each project reference's own reference assembly under
// `obj/<configuration>/<tfm>/ref/`. Because the configuration is a property of the evaluation, a closure
// can never mix one configuration's assemblies with another's, and `BuildProjectReferences=false` keeps
// the evaluation a design-time one — it reports what a build would pass to the compiler without building
// anything.
//
// `Refusal` names why the closure cannot be trusted, and a closure carrying one is never used to name
// arguments: the requested configuration has no output, an assembly the compiler would receive is not on
// disk, or MSBuild could not evaluate the project at all. Each of those leaves some types resolved and
// others as error types, and an argument of error type is convertible to every parameter — so overload
// resolution picks whichever candidate survives, and naming that call writes the wrong parameter name into
// source that compiled before.
//
// `AssemblyName` is the name of the project's own output. A compilation carrying it is the one its references
// grant internals to, so a call into a member made visible by `InternalsVisibleTo` binds as the build binds it.
//
// `EvaluateAll` asks the same question of several projects in one MSBuild process, so a project closure they
// share is evaluated once rather than once per project that references it.
internal sealed record CompileClosure(string? AssemblyName, IReadOnlyList<string> References, IReadOnlyList<string> Sources, string? Refusal) {
    // The item `EvaluateAll` collects: every reference, compile item and target path, each tagged with its kind.
    private const string ClosureItem = "PuckFormatClosure";
    // Injected into each project through `CustomAfterMicrosoftCommonTargets`: runs the same reference resolution
    // `Evaluate` asks for, then returns what `Evaluate` reads from -getItem/-getProperty as one tagged item list. Each
    // item names its project explicitly: a reference resolved from a project reference already carries the MSBuild
    // task's `MSBuildSourceProjectFile`, naming the project it came from, so that metadata cannot say whose closure
    // it belongs to.
    // A multi-targeting outer build never imports it, and the traversal skips the missing target, so such a
    // project reports nothing here and `Evaluate` explains it.
    private const string ClosureTargets = $"""
        <Project>
          <Target Name="{ClosureItem}" DependsOnTargets="FindReferenceAssembliesForReferences" Returns="@(_{ClosureItem})">
            <ItemGroup>
              <_{ClosureItem} Include="@(ReferencePathWithRefAssemblies->'%(FullPath)')" PuckKind="Reference" PuckProject="$(MSBuildProjectFullPath)" />
              <_{ClosureItem} Include="@(Compile->'%(FullPath)')" PuckKind="Source" PuckProject="$(MSBuildProjectFullPath)" />
              <_{ClosureItem} Include="$(TargetPath)" PuckKind="Target" PuckProject="$(MSBuildProjectFullPath)" Condition="'$(TargetPath)' != ''" />
            </ItemGroup>
          </Target>
        </Project>
        """;

    // MSBuild's item-spec escaping, then XML's, for a path written into a generated project.
    private static string Escape(string path) =>
        System.Security.SecurityElement.Escape(str: path
            .Replace(
                newValue: "%25",
                oldValue: "%"
            )
            .Replace(
                newValue: "%3B",
                oldValue: ";"
            )
            .Replace(
                newValue: "%24",
                oldValue: "$"
            )
            .Replace(
                newValue: "%40",
                oldValue: "@"
            )
            .Replace(
                newValue: "%27",
                oldValue: "'"
            )
            .Replace(
                newValue: "%2A",
                oldValue: "*"
            )
            .Replace(
                newValue: "%3F",
                oldValue: "?"
            ));

    // Evaluates every project in `projects` (full paths) for `configuration` in one MSBuild process: a generated
    // traversal builds each project's closure target with the global properties `Evaluate` passes, and MSBuild
    // evaluates each project configuration of their shared reference graph once. Returns the closures the traversal
    // reported, keyed by full project path; a project it reported nothing for — one that failed to evaluate, or an
    // outer multi-targeting build — is absent, and `Evaluate` answers for it with its own diagnostics.
    internal static Dictionary<string, CompileClosure> EvaluateAll(IReadOnlyCollection<string> projects, string configuration) {
        var closures = new Dictionary<string, CompileClosure>(comparer: StringComparer.OrdinalIgnoreCase);

        if (projects.Count == 0) {
            return closures;
        }

        var scratch = Directory.CreateTempSubdirectory(prefix: "puck-format-closures-").FullName;
        var targets = Path.Combine(
            path1: scratch,
            path2: "closure.targets"
        );
        var traversal = Path.Combine(
            path1: scratch,
            path2: "closures.proj"
        );

        try {
            File.WriteAllText(
                contents: ClosureTargets,
                path: targets
            );
            File.WriteAllText(
                contents: $"""
                    <Project>
                      <ItemGroup>
                    {string.Concat(values: projects.Select(selector: static project => $"    <ClosureProject Include=\"{Escape(path: project)}\" />\n"))}  </ItemGroup>
                      <Target Name="Closures">
                        <MSBuild Projects="@(ClosureProject)" Targets="{ClosureItem}" BuildInParallel="true" ContinueOnError="true" SkipNonexistentTargets="true" Properties="Configuration={Escape(path: configuration)};BuildProjectReferences=false;DesignTimeBuild=true;CustomAfterMicrosoftCommonTargets={Escape(path: targets)}">
                          <Output TaskParameter="TargetOutputs" ItemName="{ClosureItem}" />
                        </MSBuild>
                      </Target>
                    </Project>
                    """,
                path: traversal
            );

            var result = CliProcess.RunAsync(
                arguments: ["msbuild", traversal, "-nologo", "-t:Closures", $"-getItem:{ClosureItem}"],
                fileName: "dotnet"
            ).GetAwaiter().GetResult();

            foreach (var (project, closure) in ReadAll(
                configuration: configuration,
                json: result.Stdout
            )) {
                closures[project] = closure;
            }
        } finally {
            Directory.Delete(
                path: scratch,
                recursive: true
            );
        }

        return closures;
    }
    // Reads `EvaluateAll`'s -getItem report into one closure per project that reported a target path and at least one
    // reference; anything else is left for `Evaluate` to explain.
    internal static IEnumerable<(string Project, CompileClosure Closure)> ReadAll(string json, string configuration) {
        JsonDocument document;

        try {
            document = JsonDocument.Parse(json: json);
        } catch (JsonException) {
            yield break;
        }

        using (document) {
            if (
                !document.RootElement.TryGetProperty(
                propertyName: "Items",
                value: out var items
            ) ||
                !items.TryGetProperty(
                propertyName: ClosureItem,
                value: out var reported
            )
            ) {
                yield break;
            }

            var byProject = new Dictionary<string, (string? Target, List<string> References, List<string> Sources)>(comparer: StringComparer.OrdinalIgnoreCase);

            foreach (var item in reported.EnumerateArray()) {
                if (
                    !item.TryGetProperty(
                    propertyName: "Identity",
                    value: out var identity
                ) ||
                    (identity.GetString() is not { Length: > 0 } path) ||
                    !item.TryGetProperty(
                    propertyName: "PuckProject",
                    value: out var owner
                ) ||
                    (owner.GetString() is not { Length: > 0 } project) ||
                    !item.TryGetProperty(
                    propertyName: "PuckKind",
                    value: out var kind
                )
                ) {
                    continue;
                }

                project = Path.GetFullPath(path: project);

                if (!byProject.TryGetValue(
                    key: project,
                    value: out var entry
                )) {
                    entry = (null, [], []);
                }

                switch (kind.GetString()) {
                    case "Reference":
                        entry.References.Add(item: path);
                        break;
                    case "Source":
                        entry.Sources.Add(item: path);
                        break;
                    case "Target":
                        entry.Target = path;
                        break;
                }

                byProject[project] = entry;
            }

            foreach (var (project, entry) in byProject) {
                if (
                    (entry.Target is not null) &&
                    (entry.References.Count > 0)
                ) {
                    yield return (project, From(
                        configuration: configuration,
                        references: entry.References,
                        sources: entry.Sources,
                        targetPath: entry.Target
                    ));
                }
            }
        }
    }
    internal static CompileClosure Evaluate(string project, string configuration) {
        var result = CliProcess.RunAsync(
            arguments: [
                "msbuild",
                project,
                "-nologo",
                "-t:FindReferenceAssembliesForReferences",
                "-getItem:ReferencePathWithRefAssemblies",
                "-getItem:Compile",
                "-getProperty:TargetPath",
                $"-p:Configuration={configuration}",
                "-p:BuildProjectReferences=false",
                "-p:DesignTimeBuild=true",
            ],
            fileName: "dotnet"
        ).GetAwaiter().GetResult();

        return ((result.ExitCode == 0)
            ? Read(
                configuration: configuration,
                json: result.Stdout,
                referenceItem: "ReferencePathWithRefAssemblies",
                sourceItem: "Compile"
            )
            : Refuse(reason: $"MSBuild could not evaluate it for {configuration} ({FirstLine(text: result.Stderr)})")
        );
    }
    // Reads a closure from MSBuild's -getItem/-getProperty report: the `TargetPath` property, the reference set under
    // `referenceItem`, and the compile items under `sourceItem`.
    internal static CompileClosure Read(string json, string configuration, string referenceItem, string sourceItem) {
        JsonDocument document;

        try {
            document = JsonDocument.Parse(json: json);
        } catch (JsonException) {
            return Refuse(reason: $"MSBuild reported no readable {configuration} reference set");
        }

        using (document) {
            if (
                !document.RootElement.TryGetProperty(
                propertyName: "Properties",
                value: out var properties
            ) ||
                !properties.TryGetProperty(
                propertyName: "TargetPath",
                value: out var targetPath
            ) ||
                !document.RootElement.TryGetProperty(
                propertyName: "Items",
                value: out var items
            ) ||
                !items.TryGetProperty(
                propertyName: referenceItem,
                value: out var references
            )
            ) {
                return Refuse(reason: $"MSBuild reported no readable {configuration} reference set");
            }

            var resolved = new List<string>();

            foreach (var reference in references.EnumerateArray()) {
                if (reference.TryGetProperty(
                    propertyName: "Identity",
                    value: out var identity
                ) && (identity.GetString() is { Length: > 0 } path)) {
                    resolved.Add(item: path);
                }
            }

            var sources = new List<string>();

            if (items.TryGetProperty(
                propertyName: sourceItem,
                value: out var compile
            )) {
                foreach (var item in compile.EnumerateArray()) {
                    if (item.TryGetProperty(
                        propertyName: "FullPath",
                        value: out var fullPath
                    ) && (fullPath.GetString() is { Length: > 0 } source)) {
                        sources.Add(item: source);
                    }
                }
            }

            return From(
                configuration: configuration,
                references: resolved,
                sources: sources,
                targetPath: targetPath.GetString()
            );
        }
    }

    // Judges a reported closure: the project's output, the references the compiler would receive, and the compile items.
    private static CompileClosure From(string configuration, string? targetPath, IEnumerable<string> references, IEnumerable<string> sources) {
        // The project's own output assembly is the one artifact only a real compile writes, so it is the
        // evidence that this configuration was built. A design-time evaluation resolves the path either
        // way, and a restore or evaluation leaves `obj` populated, so neither of those is evidence.
        if (
            (targetPath is not { Length: > 0 } output) ||
            !File.Exists(path: output)
        ) {
            return Refuse(reason: $"not built for {configuration}");
        }

        var resolved = references.Select(selector: static path => Path.GetFullPath(path: path)).ToList();

        // An outer multi-targeting build resolves no references of its own, and neither does a project the
        // evaluation could not carry to the compile. Either way there is nothing to bind against.
        if (resolved.Count == 0) {
            return Refuse(reason: $"MSBuild reported no {configuration} compile references");
        }

        var missing = resolved.Where(predicate: static path => !File.Exists(path: path)).ToList();

        // A reference the compiler would receive but which is not on disk is the stale closure this pass
        // must never paper over: its types bind to nothing, and every call that touches one is a coin
        // toss between overloads.
        return ((missing.Count == 0)
            ? new CompileClosure(
                AssemblyName: Path.GetFileNameWithoutExtension(path: output),
                References: resolved,
                Refusal: null,
                Sources: [.. sources.Select(selector: static path => Path.GetFullPath(path: path))]
            )
            : Refuse(reason: $"{missing.Count} {configuration} reference(s) are missing, starting at {CliPaths.ToDisplay(fullPath: missing[0])}")
        );
    }
    private static string FirstLine(string text) =>
        (text.Split('\n').Select(selector: static line => line.Trim()).FirstOrDefault(predicate: static line => (line.Length > 0)) ?? "no diagnostics");

    internal static CompileClosure Refuse(string reason) =>
        new(
            AssemblyName: null,
            References: [],
            Refusal: reason,
            Sources: []
        );
}
