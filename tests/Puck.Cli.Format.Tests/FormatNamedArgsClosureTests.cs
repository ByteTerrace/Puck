using System.Xml.Linq;
using Puck.Testing;

using Xunit;

namespace Puck.Cli.Format.Tests;

/// <summary>
/// Pins where the semantic <c>named-args</c> phase gets the reference set it binds against, and what it does when
/// that set cannot be trusted. Every case formats a project that was really built, because the closure comes from
/// MSBuild rather than from a directory probe: the requested configuration decides which assemblies the compiler would
/// receive, and a configuration that was never built, or one whose closure names an assembly that is not on disk,
/// resolves some types and leaves others unresolved. Binding against a set like that names arguments for whichever overload
/// survived, which is how a rewrite ends up spelling a parameter the compiler never chose.
/// </summary>
public sealed class FormatNamedArgsClosureTests(BuiltSampleProject sample) : IClassFixture<BuiltSampleProject>, IDisposable {
    private const string CallsAssertOnAReferenceType = """
        namespace Sample;

        internal static class Probe {
            public static string? Find() => "found";

            public static void Use() {
                var found = Find();

                Xunit.Assert.NotNull(found);
            }
        }

        """;
    private const string CallsAssertOnAnUnresolvedType = """
        namespace Sample;

        internal static class Probe {
            public static void Use() {
                Admission admission = default!;

                Xunit.Assert.NotNull(admission);
            }
        }

        """;
    // `Take(1, "x")` has one applicable overload, because the mirrored one would need int to convert to
    // string. Named and alphabetized it reads `Take(a: "x", b: 1)`, which both overloads accept.
    private const string CallsMirroredOverloads = """
        namespace Sample;

        internal static class Mirror {
            public static void Take(int b, string a) {
            }
            public static void Take(string a, int b) {
            }

            public static void Use() {
                Take(1, "x");
            }
        }

        """;
    private const string NamedForTheObjectOverload = "Xunit.Assert.NotNull(@object: found);";

    private readonly TemporaryDirectory m_directory = PinnedScratch(prefix: "puck-cli-tests-named-args-");

    private string Source => Path.Combine(
        path1: m_directory.RootPath,
        path2: "Probe.cs"
    );

    /// <summary>Creates a scratch directory that resolves the repository SDK, so a project built in it binds the same
    /// compiler the suite runs under.</summary>
    /// <param name="prefix">The directory-name prefix.</param>
    /// <returns>The directory; the caller disposes it.</returns>
    internal static TemporaryDirectory PinnedScratch(string prefix) {
        var directory = new TemporaryDirectory(prefix: prefix);

        CliScratchDirectories.PinSdk(directory: directory.RootPath);

        return directory;
    }

    // Names `root`'s source in `configuration` and returns the exit code alongside everything the run reported, so a
    // case can pin both the refusal and the fact that it was announced.
    private static (int Code, string Report) Format(string root, string configuration) {
        var (code, _, report) = ConsoleCapture.RunSplit(run: () => SemanticPhases.Run(
            closures: new CompileClosures(),
            configuration: configuration,
            namedArgs: true,
            nullPattern: false,
            rootArgument: root,
            check: false
        ));

        return (code, report);
    }

    internal static void Build(string project, string configuration, bool restore = true) =>
        Assert.Equal(
            actual: CliProcess.RunAsync(
                capture: false,
                fileName: "dotnet",
                workingDirectory: Path.GetDirectoryName(path: project),
                arguments: (restore
                    ? ["build", "--disable-build-servers", project, "-c", configuration]
                    : ["build", "--disable-build-servers", project, "-c", configuration, "--no-restore"])
            ).GetAwaiter().GetResult().ExitCode,
            expected: 0
        );

    private void Build(string configuration) =>
        Build(
            configuration: configuration,
            project: Path.Combine(
                path1: m_directory.RootPath,
                path2: "Sample.csproj"
            )
        );

    /// <summary>
    /// A project built only in other configurations is refused, not bound against another configuration's output.
    /// The project's own output is the evidence: a design-time evaluation resolves the requested configuration's
    /// paths whether or not anything ever wrote them.
    /// </summary>
    [Fact]
    public void AConfigurationThatWasNeverBuiltIsRefused() {
        File.WriteAllText(
            contents: CallsAssertOnAReferenceType,
            path: sample.Source
        );

        var (code, report) = Format(
            configuration: BuiltSampleProject.NeverBuilt,
            root: sample.Root
        );

        Assert.Equal(
            actual: code,
            expected: 1
        );
        Assert.Equal(
            actual: File.ReadAllText(path: sample.Source),
            expected: CallsAssertOnAReferenceType
        );
        Assert.Contains(
            actualString: report,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"not built for {BuiltSampleProject.NeverBuilt}"
        );
    }
    /// <summary>
    /// A reference-typed argument binds to the object overload of <c>Assert.NotNull</c>, whose parameter is the
    /// verbatim identifier <c>@object</c>. The pointer and nullable-value overloads take a <c>value</c>, so naming
    /// this call <c>value:</c> would move it to another overload and stop compiling.
    /// </summary>
    [Fact]
    public void AReferenceTypedArgumentIsNamedForTheOverloadItAlreadyBindsTo() {
        File.WriteAllText(
            contents: CallsAssertOnAReferenceType,
            path: sample.Source
        );

        var (code, _) = Format(
            configuration: "Release",
            root: sample.Root
        );

        Assert.Equal(
            actual: code,
            expected: 0
        );
        Assert.Contains(
            actualString: File.ReadAllText(path: sample.Source),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: NamedForTheObjectOverload
        );
    }
    /// <summary>
    /// A rewrite may not change which method the call resolves to. Naming and alphabetizing these arguments makes
    /// the mirrored overload applicable too, so the rewritten call is ambiguous where the written one was not.
    /// </summary>
    [Fact]
    public void ANamingThatWouldMoveTheCallToAnotherOverloadIsDropped() {
        File.WriteAllText(
            contents: CallsMirroredOverloads,
            path: sample.Source
        );

        var (code, _) = Format(
            configuration: "Release",
            root: sample.Root
        );

        Assert.Equal(
            actual: code,
            expected: 0
        );
        Assert.Equal(
            actual: File.ReadAllText(path: sample.Source),
            expected: CallsMirroredOverloads
        );
    }
    /// <summary>
    /// An argument whose type does not resolve leaves its call exactly as written. Overload resolution over an
    /// unresolved argument reports candidates rather than a chosen method, and a candidate is not a signature to
    /// copy parameter names from.
    /// </summary>
    [Fact]
    public void AnArgumentOfUnresolvedTypeLeavesItsCallAlone() {
        File.WriteAllText(
            contents: CallsAssertOnAnUnresolvedType,
            path: sample.Source
        );

        var (code, _) = Format(
            configuration: "Release",
            root: sample.Root
        );

        Assert.Equal(
            actual: code,
            expected: 0
        );
        Assert.Equal(
            actual: File.ReadAllText(path: sample.Source),
            expected: CallsAssertOnAnUnresolvedType
        );
    }
    /// <summary>Removes the scratch project directory.</summary>
    public void Dispose() {
        m_directory.Dispose();
        GC.SuppressFinalize(obj: this);
    }
    /// <summary>
    /// A project reference resolves to the dependency's own reference assembly under its
    /// <c>obj/&lt;configuration&gt;/</c>, so cleaning or rebuilding that dependency elsewhere leaves the closure
    /// naming an assembly that is no longer there. The whole project is refused and said so: the closure could
    /// still bind most calls and leave the rest positional, which reads in the report exactly like a swept file.
    /// </summary>
    [Fact]
    public void AStaleProjectReferenceIsRefusedRatherThanBoundAround() {
        var library = Path.Combine(
            path1: m_directory.RootPath,
            path2: "Library"
        );
        var sample = Path.Combine(
            path1: m_directory.RootPath,
            path2: "Sample"
        );

        Directory.CreateDirectory(path: library);
        Directory.CreateDirectory(path: sample);
        File.WriteAllText(
            contents: """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""",
            path: Path.Combine(
                path1: library,
                path2: "Library.csproj"
            )
        );
        File.WriteAllText(
            contents: "namespace Library;\n\npublic static class Reach {\n    public static string? Find() => \"found\";\n}\n",
            path: Path.Combine(
                path1: library,
                path2: "Reach.cs"
            )
        );
        File.WriteAllText(
            contents: """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="../Library/Library.csproj" /></ItemGroup></Project>""",
            path: Path.Combine(
                path1: sample,
                path2: "Sample.csproj"
            )
        );

        var source = Path.Combine(
            path1: sample,
            path2: "Probe.cs"
        );
        const string Calls = "namespace Sample;\n\ninternal static class Probe {\n    public static string? Use() => string.Concat(Library.Reach.Find(), \"tail\");\n}\n";

        File.WriteAllText(
            contents: Calls,
            path: source
        );
        Build(
            configuration: "Release",
            project: Path.Combine(
                path1: sample,
                path2: "Sample.csproj"
            )
        );
        File.Delete(path: Path.Combine(
            path1: library,
            path2: "obj",
            path3: "Release",
            path4: "net10.0/ref/Library.dll"
        ));

        var (code, report) = Format(
            configuration: "Release",
            root: sample
        );

        Assert.Equal(
            actual: code,
            expected: 1
        );
        Assert.Equal(
            actual: File.ReadAllText(path: source),
            expected: Calls
        );
        Assert.Contains(
            actualString: report,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "reference(s) are missing"
        );
    }

    // Writes a Library project and a Sample project that references it under the scratch root, each with one source,
    // builds Sample (and so Library) in Release, and returns both project files.
    private (string Library, string Sample) BuildReferencingPair(string librarySource, string sampleSource) {
        var projects = new List<string>();

        foreach (var (name, reference, source) in (((string, string, string)[])[
            ("Library", "", librarySource),
            ("Sample", """<ItemGroup><ProjectReference Include="../Library/Library.csproj" /></ItemGroup>""", sampleSource),
        ])) {
            var directory = Path.Combine(
                path1: m_directory.RootPath,
                path2: name
            );
            var project = Path.Combine(
                path1: directory,
                path2: $"{name}.csproj"
            );

            Directory.CreateDirectory(path: directory);
            File.WriteAllText(
                contents: $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>{reference}</Project>""",
                path: project
            );
            File.WriteAllText(
                contents: source,
                path: Path.Combine(
                    path1: directory,
                    path2: $"{name}.cs"
                )
            );
            projects.Add(item: project);
        }

        Build(
            configuration: "Release",
            project: projects[1]
        );

        return (projects[0], projects[1]);
    }

    /// <summary>
    /// Evaluating several projects in one MSBuild process answers for each exactly as evaluating it alone does. A
    /// reference resolved from a project reference carries MSBuild's own note of the project it came from, so a batch
    /// that sorted its report by that note would hand the dependency's reference assembly to the dependency and take
    /// it from the project that references it. Both entry points use one node and disable nested parallel requests;
    /// each project's own target observes those execution settings alongside the unchanged closure assertions.
    /// </summary>
    [Fact]
    public void ABatchedEvaluationReportsEachProjectsOwnClosure() {
        var (library, sample) = BuildReferencingPair(
            librarySource: "namespace Library;\n\npublic static class Reach {\n    public static string Find() => \"found\";\n}\n",
            sampleSource: "namespace Sample;\n\ninternal static class Probe {\n    public static string Use() => Library.Reach.Find();\n}\n"
        );
        foreach (var project in ((string[])[library, sample])) {
            var document = XDocument.Load(uri: project);

            document.Root!.Add(content: new XElement(name: "Target",
                new XAttribute(name: "Name", value: "ObserveClosureExecution"),
                new XAttribute(name: "BeforeTargets", value: "FindReferenceAssembliesForReferences"),
                new XElement(name: "WriteLinesToFile",
                    new XAttribute(name: "File", value: "$(MSBuildProjectDirectory)/closure.execution.txt"),
                    new XAttribute(name: "Lines", value: "$(MSBuildNodeCount)|$(BuildInParallel)"),
                    new XAttribute(name: "Overwrite", value: "true"))));
            document.Save(fileName: project);
        }
        var batched = CompileClosure.EvaluateAll(
            cancellationToken: TestContext.Current.CancellationToken,
            configuration: "Release",
            projects: [sample, library]
        );

        foreach (var project in ((string[])[library, sample])) {
            var execution = Path.Combine(path1: Path.GetDirectoryName(path: project)!, path2: "closure.execution.txt");

            Assert.Equal(expected: "1|false", actual: File.ReadAllText(path: execution).Trim());
            var alone = CompileClosure.Evaluate(
                cancellationToken: TestContext.Current.CancellationToken,
                configuration: "Release",
                project: project
            );

            Assert.Equal(expected: "1|false", actual: File.ReadAllText(path: execution).Trim());
            Assert.Null(@object: alone.Refusal);
            Assert.True(condition: batched.TryGetValue(
                key: project,
                value: out var closure
            ));
            Assert.Equal(
                actual: closure.References.Order(comparer: StringComparer.OrdinalIgnoreCase),
                expected: alone.References.Order(comparer: StringComparer.OrdinalIgnoreCase)
            );
            Assert.Equal(
                actual: closure.Sources.Order(comparer: StringComparer.OrdinalIgnoreCase),
                expected: alone.Sources.Order(comparer: StringComparer.OrdinalIgnoreCase)
            );
            Assert.Equal(
                actual: closure.AssemblyName,
                expected: Path.GetFileNameWithoutExtension(path: project)
            );
        }

        Assert.Contains(
            collection: batched[sample].References,
            filter: static reference => reference.EndsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: $"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}Library.dll"
            )
        );
    }
    /// <summary>
    /// A batch evaluates together only projects that select their SDK from the same global.json: projects under
    /// different global.json roots land in separate partitions, projects sharing a root share one, and a project
    /// directly under the outer root is keyed by that root rather than by a nested one beside it.
    /// </summary>
    [Fact]
    public void ABatchIsPartitionedByTheGlobalJsonEachProjectSelectsItsSdkFrom() {
        string Project(string relative) {
            var project = Path.Combine(
                path1: m_directory.RootPath,
                path2: relative
            );

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: project)!);
            File.WriteAllText(
                contents: "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                path: project
            );

            return Path.GetFullPath(path: project);
        }

        foreach (var pinned in ((string[])["Alpha", "Beta"])) {
            Directory.CreateDirectory(path: Path.Combine(path1: m_directory.RootPath, path2: pinned));
            File.Copy(
                destFileName: Path.Combine(path1: m_directory.RootPath, path2: pinned, path3: "global.json"),
                sourceFileName: Path.Combine(path1: m_directory.RootPath, path2: "global.json")
            );
        }

        var one = Project(relative: "Alpha/One/One.csproj");
        var two = Project(relative: "Alpha/Two/Two.csproj");
        var three = Project(relative: "Beta/Three/Three.csproj");
        var four = Project(relative: "Four/Four.csproj");

        var partitions = CompileClosure.PartitionBySdkContext(projects: [four, three, two, one]);

        Assert.Equal(
            actual: partitions.Select(selector: static partition => partition.ToArray()),
            expected: [[one, two], [three], [four]]
        );
    }
    /// <summary>
    /// Formatting a tree of sibling projects outside any Puck checkout evaluates them in one batch, with the SDK the
    /// projects themselves select, and each project's calls bind against its own closure: the library's call into its
    /// own type and the sample's call into the library are both named.
    /// </summary>
    [Fact]
    public void ABatchedFormatOfSiblingProjectsOutsideACheckoutEvaluatesBoth() {
        var (library, sample) = BuildReferencingPair(
            librarySource: "namespace Library;\n\npublic static class Reach {\n    public static string Join(string left, string right) => (left + right);\n    public static string Twice(string value) => Join(value, value);\n}\n",
            sampleSource: "namespace Sample;\n\ninternal static class Probe {\n    public static string Use() => Library.Reach.Join(\"a\", \"b\");\n}\n"
        );

        Assert.Null(@object: RepositoryPaths.Ascend(
            probe: static directory => (File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx")) ? directory.FullName : null),
            start: m_directory.RootPath
        ));

        var (code, report) = Format(
            configuration: "Release",
            root: m_directory.RootPath
        );

        Assert.Equal(
            actual: code,
            expected: 0
        );
        Assert.DoesNotContain(
            actualString: report,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "skipped"
        );
        Assert.Contains(
            actualString: File.ReadAllText(path: Path.Combine(path1: Path.GetDirectoryName(path: library)!, path2: "Library.cs")),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "Join(left: value, right: value)"
        );
        Assert.Contains(
            actualString: File.ReadAllText(path: Path.Combine(path1: Path.GetDirectoryName(path: sample)!, path2: "Sample.cs")),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "Library.Reach.Join(left: \"a\", right: \"b\")"
        );
    }
    /// <summary>
    /// A member another project makes visible with <c>InternalsVisibleTo</c> binds only in a compilation carrying the
    /// name the grant was made to, which is the project's own assembly name. Under any other name the call has an
    /// inaccessible candidate rather than a method, and is left positional.
    /// </summary>
    [Fact]
    public void ACallIntoInternalsItsReferenceGrantsItIsNamed() {
        var (_, sample) = BuildReferencingPair(
            librarySource: "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Sample\")]\n\nnamespace Library;\n\ninternal static class Reach {\n    internal static string Join(string left, string right) => (left + right);\n}\n",
            sampleSource: "namespace Sample;\n\ninternal static class Probe {\n    public static string Use() => Library.Reach.Join(\"a\", \"b\");\n}\n"
        );
        var sampleRoot = Path.GetDirectoryName(path: sample)!;

        var (code, _) = Format(
            configuration: "Release",
            root: sampleRoot
        );

        Assert.Equal(
            actual: code,
            expected: 0
        );
        Assert.Contains(
            actualString: File.ReadAllText(path: Path.Combine(
                path1: sampleRoot,
                path2: "Sample.cs"
            )),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "Library.Reach.Join(left: \"a\", right: \"b\")"
        );
    }
    /// <summary>
    /// A source that sits outside every project directory, shared into the projects beside it, is formatted in the
    /// compilation of a project that compiles it. Treated as belonging to no project, it would be skipped and fail
    /// every run that includes it.
    /// </summary>
    [Fact]
    public void ASharedSourceIsFormattedInTheProjectThatLinksIt() {
        var shared = Path.Combine(
            path1: m_directory.RootPath,
            path2: "Shared",
            path3: "Helper.cs"
        );

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: shared)!);
        File.WriteAllText(
            contents: "namespace Sample;\n\ninternal static class Helper {\n    public static string Use() => Probe.Join(\"a\", \"b\");\n}\n",
            path: shared
        );

        var (_, sample) = BuildReferencingPair(
            librarySource: "namespace Library;\n\npublic static class Reach {\n}\n",
            sampleSource: "namespace Sample;\n\ninternal static class Probe {\n    public static string Join(string left, string right) => (left + right);\n}\n"
        );

        File.WriteAllText(
            contents: File.ReadAllText(path: sample).Replace(
                newValue: "<ItemGroup><Compile Include=\"../Shared/Helper.cs\" Link=\"Helper.cs\" /></ItemGroup></Project>",
                oldValue: "</Project>"
            ),
            path: sample
        );
        Build(
            configuration: "Release",
            project: sample
        );

        var (code, _, report) = ConsoleCapture.RunSplit(run: () => SemanticPhases.Run(
            closures: new CompileClosures(),
            configuration: "Release",
            namedArgs: true,
            nullPattern: false,
            rootArgument: m_directory.RootPath,
            targets: [shared],
            check: false
        ));

        Assert.Equal(
            actual: code,
            expected: 0
        );
        Assert.DoesNotContain(
            actualString: report,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "compiled by no project"
        );
        Assert.Contains(
            actualString: File.ReadAllText(path: shared),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "Probe.Join(left: \"a\", right: \"b\")"
        );
    }
    /// <summary>
    /// A source the project links in from outside its own directory is one of its compile items, so the closure
    /// carries it and a call into its types binds and is named. A walk of the project directory alone never sees it.
    /// </summary>
    [Fact]
    public void ASourceLinkedInFromOutsideTheProjectDirectoryBinds() {
        using var sharedDirectory = new TemporaryDirectory(prefix: "puck-cli-tests-named-args-shared-");
        var shared = sharedDirectory.RootPath;

        File.WriteAllText(
            contents: """
                namespace Sample;

                internal static class Shared {
                    public static string Join(string left, string right) => (left + right);
                }

                """,
            path: Path.Combine(
                path1: shared,
                path2: "Shared.cs"
            )
        );
        File.WriteAllText(
            contents: $"""
                <Project Sdk="Microsoft.NET.Sdk">
                    <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                    <ItemGroup><Compile Include="{Path.Combine(path1: shared, path2: "Shared.cs")}" Link="Shared.cs" /></ItemGroup>
                </Project>
                """,
            path: Path.Combine(
                path1: m_directory.RootPath,
                path2: "Sample.csproj"
            )
        );
        File.WriteAllText(
            contents: """
                namespace Sample;

                internal static class Probe {
                    public static string Use() => Shared.Join("a", "b");
                }

                """,
            path: Source
        );
        Build(configuration: "Release");

        var (code, report) = Format(
            configuration: "Release",
            root: m_directory.RootPath
        );

        Assert.Equal(
            actual: code,
            expected: 0
        );
        Assert.DoesNotContain(
            actualString: report,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "could not be resolved"
        );
        Assert.Contains(
            actualString: File.ReadAllText(path: Source),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "Shared.Join(left: \"a\", right: \"b\")"
        );
    }
    /// <summary>
    /// With both configurations built, the requested one decides which assemblies resolve. The same source is named
    /// where the assertion reference is part of the closure and left alone where it is not, so an output from the
    /// other configuration can never supply a binding.
    /// </summary>
    [Fact]
    public void TheRequestedConfigurationDecidesWhichClosureBinds() {
        File.WriteAllText(
            contents: CallsAssertOnAReferenceType,
            path: sample.Source
        );

        var (code, report) = Format(
            configuration: "Debug",
            root: sample.Root
        );

        Assert.Equal(
            actual: code,
            expected: 0
        );
        Assert.Equal(
            actual: File.ReadAllText(path: sample.Source),
            expected: CallsAssertOnAReferenceType
        );
        Assert.Contains(
            actualString: report,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "could not be resolved"
        );
        Assert.Equal(
            actual: Format(
                configuration: "Release",
                root: sample.Root
            ).Code,
            expected: 0
        );
        Assert.Contains(
            actualString: File.ReadAllText(path: sample.Source),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: NamedForTheObjectOverload
        );
    }
}
/// <summary>
/// One scratch project, built once in <c>Debug</c> and <c>Release</c>, whose source each case writes before formatting
/// it. The reference is a plain HintPath to the assembly this suite already resolved, so the project needs no package
/// restore of its own, and a configuration condition makes its two builds' closures differ observably: only
/// <c>Release</c> can bind <c>Xunit.Assert</c>. Cases in one class run one at a time, so each owns the source while it
/// runs.
/// </summary>
public sealed class BuiltSampleProject : IDisposable {
    // Compiles in every configuration and with or without the assertion reference, so both builds succeed before a
    // case writes the source under test over it.
    private const string CompilesAnywhere = """
        namespace Sample;

        internal static class Widget {
            public static int Add(int left, int right) => (left + right);
        }

        """;

    /// <summary>A configuration name this project is never built in.</summary>
    public const string NeverBuilt = "Checked";

    private readonly TemporaryDirectory m_directory = FormatNamedArgsClosureTests.PinnedScratch(prefix: "puck-cli-tests-named-args-sample-");

    /// <summary>Gets the project directory.</summary>
    public string Root => m_directory.RootPath;
    /// <summary>Gets the one source file the cases rewrite.</summary>
    public string Source => Path.Combine(
        path1: Root,
        path2: "Probe.cs"
    );

    /// <summary>Writes the project and builds it in both configurations.</summary>
    public BuiltSampleProject() {
        var project = Path.Combine(
            path1: Root,
            path2: "Sample.csproj"
        );

        File.WriteAllText(
            contents: $"""
                <Project Sdk="Microsoft.NET.Sdk">
                    <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                    <ItemGroup Condition="'$(Configuration)' == 'Release'">
                        <Reference Include="xunit.v3.assert"><HintPath>{typeof(Assert).Assembly.Location}</HintPath></Reference>
                    </ItemGroup>
                </Project>
                """,
            path: project
        );
        File.WriteAllText(
            contents: CompilesAnywhere,
            path: Source
        );
        FormatNamedArgsClosureTests.Build(
            configuration: "Debug",
            project: project
        );
        FormatNamedArgsClosureTests.Build(
            configuration: "Release",
            project: project,
            restore: false
        );
    }

    /// <summary>Removes the project directory.</summary>
    public void Dispose() => m_directory.Dispose();
}
