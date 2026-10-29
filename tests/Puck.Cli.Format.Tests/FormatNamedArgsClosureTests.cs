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
public sealed class FormatNamedArgsClosureTests(BuiltSampleProject sample) : IClassFixture<BuiltSampleProject> {
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
    internal static (int Code, string Report) Format(string root, string configuration, CompileClosures? closures = null) {
        var (code, _, report) = ConsoleCapture.RunSplit(run: () => SemanticPhases.Run(
            closures: (closures ?? new CompileClosures()),
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
            closures: sample.Closures,
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
            closures: sample.Closures,
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
            closures: sample.Closures,
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
            closures: sample.Closures,
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
            closures: sample.Closures,
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
                closures: sample.Closures,
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
/// The named-args laws over areferencing pair of scratch projects: a stale project reference, batched closure
/// evaluation, granted internals, and sources shared or linked in from outside the project. The pair is built once in
/// <see cref="BuiltReferencingPair"/>, and this class runs beside the cases over <see cref="BuiltSampleProject"/>.
/// </summary>
public sealed class FormatNamedArgsProjectTests(BuiltReferencingPair pair) : IClassFixture<BuiltReferencingPair> {
    private static (int Code, string Report) Format(string root, string configuration, CompileClosures? closures = null) => FormatNamedArgsClosureTests.Format(
        closures: closures,
        configuration: configuration,
        root: root
    );

    /// <summary>
    /// A project reference resolves to the dependency's own reference assembly under its
    /// <c>obj/&lt;configuration&gt;/</c>, so cleaning or rebuilding that dependency elsewhere leaves the closure
    /// naming an assembly that is no longer there. The whole project is refused and said so: the closure could
    /// still bind most calls and leave the rest positional, which reads in the report exactly like a swept file.
    /// </summary>
    [Fact]
    public void AStaleProjectReferenceIsRefusedRatherThanBoundAround() {
        pair.ResetSources();
        const string Calls = "namespace Sample;\n\ninternal static class Probe {\n    public static string? Use() => string.Concat(Library.Reach.Find(), \"tail\");\n}\n";

        File.WriteAllText(
            contents: Calls,
            path: pair.SampleSource
        );
        var backup = (pair.LibraryRefAssembly + ".bak");

        File.Move(
            destFileName: backup,
            overwrite: true,
            sourceFileName: pair.LibraryRefAssembly
        );
        try {
            var (code, report) = Format(
                configuration: "Release",
                root: pair.SampleRoot
            );

            Assert.Equal(
                actual: code,
                expected: 1
            );
            Assert.Equal(
                actual: File.ReadAllText(path: pair.SampleSource),
                expected: Calls
            );
            Assert.Contains(
                actualString: report,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "reference(s) are missing"
            );
        } finally {
            File.Move(
                destFileName: pair.LibraryRefAssembly,
                overwrite: true,
                sourceFileName: backup
            );
        }
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
        pair.ResetSources();
        var batched = pair.EnsureBatchedClosures(cancellationToken: TestContext.Current.CancellationToken);

        foreach (var project in ((string[])[pair.LibraryProject, pair.SampleProject])) {
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
            collection: batched[pair.SampleProject].References,
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
        using var directory = FormatNamedArgsClosureTests.PinnedScratch(prefix: "puck-cli-tests-named-args-partition-");
        string Project(string relative) {
            var project = Path.Combine(
                path1: directory.RootPath,
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
            Directory.CreateDirectory(path: Path.Combine(path1: directory.RootPath, path2: pinned));
            File.Copy(
                destFileName: Path.Combine(path1: directory.RootPath, path2: pinned, path3: "global.json"),
                sourceFileName: Path.Combine(path1: directory.RootPath, path2: "global.json")
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
        pair.ResetSources();
        File.WriteAllText(
            contents: "namespace Sample;\n\ninternal static class Probe {\n    public static string Join(string left, string right) => (left + right);\n    public static string Use() => Library.Reach.Join(\"a\", \"b\");\n}\n",
            path: pair.SampleSource
        );

        Assert.Null(@object: RepositoryPaths.Ascend(
            probe: static directory => (File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx")) ? directory.FullName : null),
            start: pair.Root
        ));

        var (code, report) = Format(
            closures: pair.EnsureClosures(cancellationToken: TestContext.Current.CancellationToken),
            configuration: "Release",
            root: pair.Root
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
            actualString: File.ReadAllText(path: pair.LibrarySource),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "Join(left: value, right: value)"
        );
        Assert.Contains(
            actualString: File.ReadAllText(path: pair.SampleSource),
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
        pair.ResetSources();
        File.WriteAllText(
            contents: "namespace Sample;\n\ninternal static class Probe {\n    public static string Join(string left, string right) => (left + right);\n    public static string Use() => Library.InternalReach.Join(\"a\", \"b\");\n}\n",
            path: pair.SampleSource
        );

        var (code, _) = Format(
            closures: pair.EnsureClosures(cancellationToken: TestContext.Current.CancellationToken),
            configuration: "Release",
            root: pair.SampleRoot
        );

        Assert.Equal(
            actual: code,
            expected: 0
        );
        Assert.Contains(
            actualString: File.ReadAllText(path: pair.SampleSource),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "Library.InternalReach.Join(left: \"a\", right: \"b\")"
        );
    }
    /// <summary>
    /// A source that sits outside every project directory, shared into the projects beside it, is formatted in the
    /// compilation of a project that compiles it. Treated as belonging to no project, it would be skipped and fail
    /// every run that includes it.
    /// </summary>
    [Fact]
    public void ASharedSourceIsFormattedInTheProjectThatLinksIt() {
        pair.ResetSources();

        var (code, _, report) = ConsoleCapture.RunSplit(run: () => SemanticPhases.Run(
            closures: pair.EnsureClosures(cancellationToken: TestContext.Current.CancellationToken),
            configuration: "Release",
            namedArgs: true,
            nullPattern: false,
            rootArgument: pair.Root,
            targets: [pair.SharedSource],
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
            actualString: File.ReadAllText(path: pair.SharedSource),
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
        pair.ResetSources();
        File.WriteAllText(
            contents: "namespace Sample;\n\ninternal static class Probe {\n    public static string Join(string left, string right) => (left + right);\n    public static string Use() => Helper.Join(\"a\", \"b\");\n}\n",
            path: pair.SampleSource
        );

        var (code, report) = Format(
            closures: pair.EnsureClosures(cancellationToken: TestContext.Current.CancellationToken),
            configuration: "Release",
            root: pair.SampleRoot
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
            actualString: File.ReadAllText(path: pair.SampleSource),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "Helper.Join(left: \"a\", right: \"b\")"
        );
    }
}
/// <summary>
/// A <c>Library</c> project and a <c>Sample</c> project that references it and links <c>../Shared/Helper.cs</c>, built
/// once in <c>Release</c> and shared across the project-graph laws of <see cref="FormatNamedArgsProjectTests"/>.
/// </summary>
public sealed class BuiltReferencingPair : IDisposable {
    private const string DefaultLibrarySource = "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Sample\")]\n\nnamespace Library;\n\npublic static class Reach {\n    public static string? Find() => \"found\";\n    public static string Join(string left, string right) => (left + right);\n    public static string Twice(string value) => Join(value, value);\n}\n\ninternal static class InternalReach {\n    internal static string Join(string left, string right) => (left + right);\n}\n";
    private const string DefaultSampleSource = "namespace Sample;\n\ninternal static class Probe {\n    public static string Join(string left, string right) => (left + right);\n    public static string Use() => Library.Reach.Find()!;\n}\n";
    private const string DefaultSharedSource = "namespace Sample;\n\ninternal static class Helper {\n    public static string Join(string left, string right) => (left + right);\n    public static string Use() => Probe.Join(\"a\", \"b\");\n}\n";

    private readonly TemporaryDirectory m_directory = FormatNamedArgsClosureTests.PinnedScratch(prefix: "puck-cli-tests-named-args-");
    private readonly CompileClosures m_closures = new();
    private Dictionary<string, CompileClosure>? m_batched;

    public string Root => m_directory.RootPath;
    public string LibraryRoot => Path.Combine(path1: Root, path2: "Library");
    public string SampleRoot => Path.Combine(path1: Root, path2: "Sample");
    public string LibraryProject => Path.Combine(path1: LibraryRoot, path2: "Library.csproj");
    public string SampleProject => Path.Combine(path1: SampleRoot, path2: "Sample.csproj");
    public string LibrarySource => Path.Combine(path1: LibraryRoot, path2: "Library.cs");
    public string SampleSource => Path.Combine(path1: SampleRoot, path2: "Sample.cs");
    public string SharedSource => Path.Combine(path1: Root, path2: "Shared", path3: "Helper.cs");
    public string LibraryRefAssembly => Path.Combine(path1: LibraryRoot, path2: "obj", path3: "Release", path4: "net11.0/ref/Library.dll");

    public BuiltReferencingPair() {
        Directory.CreateDirectory(path: LibraryRoot);
        Directory.CreateDirectory(path: SampleRoot);
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: SharedSource)!);
        const string Target = "<Target Name=\"ObserveClosureExecution\" BeforeTargets=\"FindReferenceAssembliesForReferences\"><WriteLinesToFile File=\"$(MSBuildProjectDirectory)/closure.execution.txt\" Lines=\"$(MSBuildNodeCount)|$(BuildInParallel)\" Overwrite=\"true\" /></Target>";

        File.WriteAllText(
            contents: $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>{Target}</Project>",
            path: LibraryProject
        );
        File.WriteAllText(
            contents: $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include=\"../Library/Library.csproj\" /><Compile Include=\"../Shared/Helper.cs\" Link=\"Helper.cs\" /></ItemGroup>{Target}</Project>",
            path: SampleProject
        );
        ResetSources();
        FormatNamedArgsClosureTests.Build(
            configuration: "Release",
            project: SampleProject
        );
    }

    public void ResetSources() {
        File.WriteAllText(contents: DefaultLibrarySource, path: LibrarySource);
        File.WriteAllText(contents: DefaultSampleSource, path: SampleSource);
        File.WriteAllText(contents: DefaultSharedSource, path: SharedSource);
    }
    public Dictionary<string, CompileClosure> EnsureBatchedClosures(CancellationToken cancellationToken) {
        if (m_batched is null) {
            m_batched = CompileClosure.EvaluateAll(
                cancellationToken: cancellationToken,
                configuration: "Release",
                projects: [SampleProject, LibraryProject]
            );
            foreach (var (project, closure) in m_batched) {
                m_closures.Record(
                    closure: closure,
                    configuration: "Release",
                    project: project
                );
            }
        }

        return m_batched;
    }
    public CompileClosures EnsureClosures(CancellationToken cancellationToken) {
        _ = EnsureBatchedClosures(cancellationToken: cancellationToken);
        return m_closures;
    }
    public void Dispose() => m_directory.Dispose();
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

    /// <summary>Gets the shared closure cache for the sample project's unchanged configurations.</summary>
    public CompileClosures Closures { get; } = new();
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
                    <PropertyGroup><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
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
