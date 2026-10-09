using System.Text.Json;
using Puck.Hosting;

namespace Puck.Cli.Formats;

/// <summary>One project-level using directive: a <c>Using</c> item, whether a project file, an imported props or targets
/// file or the SDK's implicit usings declares it, or a <c>global using</c> a source file of the project states.</summary>
/// <param name="Name">The namespace or type the directive names.</param>
/// <param name="Alias">The alias the directive declares, or <see langword="null"/>.</param>
/// <param name="Static">Whether the directive is <c>using static</c>.</param>
public readonly record struct ProjectUsing(string Name, string? Alias = null, bool Static = false) {
    /// <summary>The directive spelled as the SDK's <c>GlobalUsings.g.cs</c> spells it, without <c>global</c>, so it scopes
    /// to the one file it is written into.</summary>
    public string Directive => (Static
        ? $"using static global::{Name};"
        : ((Alias is { } alias)
            ? $"using {alias} = global::{Name};"
            : $"using global::{Name};"));
}
/// <summary>
/// The sources a format closure compiles, and the compile context each one has in its project: the project-level usings
/// it compiles with, and the files its project compiles from outside <c>src/</c>.
/// </summary>
/// <param name="Files">Every source the closure can cover, by repository-relative path with forward slashes.</param>
/// <param name="Projects">Each project's directory (repository-relative, with a trailing slash; empty for one project
/// over every file) and the project-level usings its evaluation declares.</param>
/// <param name="Linked">Each file a project compiles from outside <c>src/</c>, by repository-relative path, with its
/// text and the directory of the project whose usings it binds under. Linked files bind names; they are never units.</param>
public sealed record FormatShapeSources(
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyDictionary<string, IReadOnlyList<ProjectUsing>> Projects,
    IReadOnlyDictionary<string, (string Text, string Project)> Linked
) {
    /// <summary>The implicit usings <c>Microsoft.NET.Sdk</c> declares for a C# project with <c>ImplicitUsings</c>
    /// enabled.</summary>
    public static readonly IReadOnlyList<string> SdkImplicitUsings = ["System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks"];

    /// <summary>Sources compiled as one <c>Microsoft.NET.Sdk</c> project with implicit usings and nothing linked: the
    /// compile context of sources written without a project, as a law's fixtures are.</summary>
    /// <param name="files">Every source, by repository-relative path with forward slashes.</param>
    /// <returns>The sources.</returns>
    public static FormatShapeSources InOneProject(IReadOnlyDictionary<string, string> files) => new(
        Files: files,
        Linked: new Dictionary<string, (string, string)>(comparer: StringComparer.Ordinal),
        Projects: new Dictionary<string, IReadOnlyList<ProjectUsing>>(comparer: StringComparer.Ordinal) {
            [string.Empty] = [.. SdkImplicitUsings.Select(selector: static name => new ProjectUsing(Name: name))],
        }
    );
    /// <summary>The project a file compiles in: the deepest project directory that holds it.</summary>
    /// <param name="path">A repository-relative path with forward slashes.</param>
    /// <returns>The project's directory, or <see langword="null"/> when no project holds the file.</returns>
    public string? ProjectOf(string path) {
        string? found = null;

        foreach (var directory in Projects.Keys) {
            if (path.StartsWith(comparisonType: StringComparison.Ordinal, value: directory) && ((found is null) || (directory.Length > found.Length))) { found = directory; }
        }

        return found;
    }

    /// <summary>How long one evaluation of every project may take.</summary>
    public static readonly TimeSpan EvaluationTimeout = TimeSpan.FromMinutes(value: 2);

    private const string Item = "PuckFormatProject";
    // Injected into each project through CustomAfterMicrosoftCommonTargets: reports what the evaluation declares, the Using
    // items and the Compile items, and runs no other target. A project that imports no common targets (one with no SDK)
    // reports nothing and compiles with no project-level using.
    private const string ProjectTargets = $"""
        <Project>
          <Target Name="{Item}" Returns="@(_{Item})">
            <ItemGroup>
              <_{Item} Include="@(Using)" PuckKind="Using" PuckProject="$(MSBuildProjectFullPath)" />
              <_{Item} Include="@(Compile->'%(FullPath)')" PuckKind="Compile" PuckProject="$(MSBuildProjectFullPath)" />
            </ItemGroup>
          </Target>
        </Project>
        """;

    /// <summary>
    /// Evaluates every project under <c>src/</c> for the usings and linked files its sources compile with. MSBuild
    /// evaluates each project and runs one target that only reads items, so nothing is built or restored, and the
    /// package-restore imports under <c>obj/</c> are switched off: the result is a function of the project files and the
    /// SDK <c>global.json</c> pins, never of what a build or restore left on disk.
    /// </summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="files">Every source the closure can cover, by repository-relative path with forward slashes.</param>
    /// <param name="projects">Every project file under <c>src/</c>, by repository-relative path with forward slashes.</param>
    /// <returns>The sources in their compile context.</returns>
    /// <exception cref="InvalidOperationException">MSBuild could not evaluate a project.</exception>
    /// <exception cref="TimeoutException">The evaluation did not finish within <see cref="EvaluationTimeout"/>.</exception>
    public static FormatShapeSources Evaluate(string repositoryRoot, IReadOnlyDictionary<string, string> files, IReadOnlyList<string> projects) {
        var usings = projects.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static _ => new SortedSet<ProjectUsing>(comparer: Comparer<ProjectUsing>.Create(comparison: static (a, b) => string.CompareOrdinal(strA: a.Directive, strB: b.Directive))),
            keySelector: static project => project[..(project.LastIndexOf(value: '/') + 1)]
        );
        var linked = new SortedDictionary<string, SortedSet<string>>(comparer: StringComparer.Ordinal);

        if (projects.Count != 0) {
            using var run = RunDirectory.Create(prefix: "puck-format-projects-");
            var targets = Path.Combine(path1: run.Path, path2: "projects.targets");
            var traversal = Path.Combine(path1: run.Path, path2: "projects.proj");

            File.WriteAllText(contents: ProjectTargets, path: targets);
            File.WriteAllText(
                contents: $"""
                    <Project>
                      <ItemGroup>
                    {string.Concat(values: projects.Select(selector: project => $"    <FormatProject Include=\"{Escape(text: Path.GetFullPath(path: Path.Combine(path1: repositoryRoot, path2: project)))}\" />\n"))}  </ItemGroup>
                      <Target Name="Projects">
                        <MSBuild Projects="@(FormatProject)" Targets="{Item}" BuildInParallel="false" SkipNonexistentTargets="true" Properties="ImportProjectExtensionProps=false;ImportProjectExtensionTargets=false;CustomAfterMicrosoftCommonTargets={Escape(text: targets)}">
                          <Output TaskParameter="TargetOutputs" ItemName="{Item}" />
                        </MSBuild>
                      </Target>
                    </Project>
                    """,
                path: traversal
            );

            var result = CliProcess.RunAsync(
                arguments: ["msbuild", traversal, "-nologo", "--disable-build-servers", "-nodeReuse:false", "-maxcpucount:1", "-t:Projects", $"-getItem:{Item}"],
                fileName: "dotnet",
                input: string.Empty,
                timeout: EvaluationTimeout,
                workingDirectory: repositoryRoot
            ).GetAwaiter().GetResult();

            if (result.TimedOut) { throw new TimeoutException(message: $"MSBuild evaluation of the format projects did not finish within {EvaluationTimeout}; its process tree was stopped."); }
            if (result.ExitCode != 0) { throw new InvalidOperationException(message: $"MSBuild could not evaluate the format projects: {Diagnostics(result: result)}"); }

            run.Conclude(passed: true);

            var root = Path.GetFullPath(path: repositoryRoot);

            using var document = JsonDocument.Parse(json: result.Stdout);

            foreach (var item in document.RootElement.GetProperty(propertyName: "Items").GetProperty(propertyName: Item).EnumerateArray()) {
                var project = Relative(path: item.GetProperty(propertyName: "PuckProject").GetString()!, root: root);
                var directory = project[..(project.LastIndexOf(value: '/') + 1)];
                var identity = item.GetProperty(propertyName: "Identity").GetString()!;

                switch (item.GetProperty(propertyName: "PuckKind").GetString()) {
                    case "Using":
                        usings[directory].Add(item: new ProjectUsing(
                            Alias: ((Metadata(item: item, name: "Alias") is { Length: > 0 } alias) ? alias : null),
                            Name: identity,
                            Static: string.Equals(a: Metadata(item: item, name: "Static"), b: "true", comparisonType: StringComparison.OrdinalIgnoreCase)
                        ));
                        break;
                    case "Compile":
                        var source = Relative(path: identity, root: root);

                        // A file the project links in from outside src/, inside the repository and outside any build output.
                        if (!source.StartsWith(comparisonType: StringComparison.Ordinal, value: "src/") && !source.StartsWith(comparisonType: StringComparison.Ordinal, value: "../") && !Path.IsPathRooted(path: source) && !source.Contains(comparisonType: StringComparison.Ordinal, value: "/obj/") && !source.Contains(comparisonType: StringComparison.Ordinal, value: "/bin/")) {
                            if (!linked.TryGetValue(key: source, value: out var owners)) { linked[source] = owners = new SortedSet<string>(comparer: StringComparer.Ordinal); }

                            owners.Add(item: directory);
                        }
                        break;
                }
            }
        }

        return new FormatShapeSources(
            Files: files,
            Linked: linked.Where(predicate: pair => File.Exists(path: Path.Combine(path1: repositoryRoot, path2: pair.Key))).ToDictionary(
                comparer: StringComparer.Ordinal,
                elementSelector: pair => (File.ReadAllText(path: Path.Combine(path1: repositoryRoot, path2: pair.Key)), pair.Value.Min!),
                keySelector: static pair => pair.Key
            ),
            Projects: usings.ToDictionary(
                comparer: StringComparer.Ordinal,
                elementSelector: static pair => ((IReadOnlyList<ProjectUsing>)[.. pair.Value]),
                keySelector: static pair => pair.Key
            )
        );
    }

    private static string? Metadata(JsonElement item, string name) => (item.TryGetProperty(propertyName: name, value: out var value) ? value.GetString() : null);
    private static string Relative(string path, string root) => Path.GetRelativePath(path: Path.GetFullPath(path: path), relativeTo: root).Replace(newChar: '/', oldChar: '\\');
    private static string Diagnostics(ChildProcessResult result) => string.Join(separator: " ", values: $"{result.Stdout}\n{result.Stderr}".Split(separator: '\n').Select(selector: static line => line.Trim()).Where(predicate: static line => line.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: "error")).Take(count: 3).DefaultIfEmpty(defaultValue: $"exit code {result.ExitCode}"));
    private static string Escape(string text) => Format.CompileClosure.Escape(path: text);
}
