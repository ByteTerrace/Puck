using Puck.Cli.Format;

using Xunit;

namespace Puck.Cli.Tests;

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

    private readonly string m_root = Path.Combine(
        path1: Path.GetTempPath(),
        path2: $"puck-cli-tests-named-args-{Guid.NewGuid():N}"
    );

    private string Source => Path.Combine(
        path1: m_root,
        path2: "Probe.cs"
    );

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
                arguments: (restore
                    ? ["build", project, "-c", configuration]
                    : ["build", project, "-c", configuration, "--no-restore"])
            ).GetAwaiter().GetResult().ExitCode,
            expected: 0
        );

    private void Build(string configuration) =>
        Build(
            configuration: configuration,
            project: Path.Combine(
                path1: m_root,
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
        try {
            Directory.Delete(
                path: m_root,
                recursive: true
            );
        } catch (DirectoryNotFoundException) {
        }

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
            path1: m_root,
            path2: "Library"
        );
        var sample = Path.Combine(
            path1: m_root,
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
                path1: m_root,
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
    /// it from the project that references it.
    /// </summary>
    [Fact]
    public void ABatchedEvaluationReportsEachProjectsOwnClosure() {
        var (library, sample) = BuildReferencingPair(
            librarySource: "namespace Library;\n\npublic static class Reach {\n    public static string Find() => \"found\";\n}\n",
            sampleSource: "namespace Sample;\n\ninternal static class Probe {\n    public static string Use() => Library.Reach.Find();\n}\n"
        );
        var batched = CompileClosure.EvaluateAll(
            configuration: "Release",
            projects: [sample, library]
        );

        foreach (var project in ((string[])[library, sample])) {
            var alone = CompileClosure.Evaluate(
                configuration: "Release",
                project: project
            );

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
            path1: m_root,
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
            rootArgument: m_root,
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
        var shared = $"{m_root}-shared";

        Directory.CreateDirectory(path: m_root);
        Directory.CreateDirectory(path: shared);

        try {
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
                    path1: m_root,
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
                root: m_root
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
        } finally {
            Directory.Delete(
                path: shared,
                recursive: true
            );
        }
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

    /// <summary>Gets the project directory.</summary>
    public string Root { get; } = Path.Combine(
        path1: Path.GetTempPath(),
        path2: $"puck-cli-tests-named-args-sample-{Guid.NewGuid():N}"
    );
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

        Directory.CreateDirectory(path: Root);
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
    public void Dispose() {
        try {
            Directory.Delete(
                path: Root,
                recursive: true
            );
        } catch (DirectoryNotFoundException) {
        }
    }
}
