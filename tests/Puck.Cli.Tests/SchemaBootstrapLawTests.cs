using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Puck.Cli.Schema;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>A model rename can regenerate stale getters without admitting a bootstrap binary as an engine.</summary>
public sealed class SchemaBootstrapLawTests {
    private static CliProcessResult Result(int exitCode, string stdout = "", bool timedOut = false) => new(
        ExitCode: exitCode, OutputLines: [], Stderr: string.Empty, Stdout: stdout, TimedOut: timedOut
    );

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APrivateCurrentModelBuildPrecedesTheSameSchemaOperation(bool check) {
        using var directory = new TemporaryDirectory();
        var calls = new List<string[]>();
        var run = directory.PathOf(name: "run");
        var output = directory.PathOf(name: "schema.json");
        var exit = SchemaBootstrap.RunInDirectory(
            repositoryRoot: directory.RootPath, directory: run, bundle: !check, check: check,
            output: check ? null : output, clock: TimeProvider.System,
            runner: (arguments, timeout) => {
                calls.Add(item: arguments.ToArray());
                Assert.True(condition: timeout > TimeSpan.Zero);
                if (arguments[0] == "build") {
                    Assert.Contains(expected: "-p:PuckSchemaBootstrap=true", collection: arguments);
                    Assert.Contains(expected: "-p:UseArtifactsOutput=true", collection: arguments);
                    Assert.Contains(expected: $"-p:ArtifactsPath={Puck.Abstractions.PuckPaths.Normalize(path: Path.Combine(path1: run, path2: "artifacts"))}", collection: arguments);
                    Assert.Contains(expected: CliOptions.NoNodeReuse, collection: arguments);
                    Assert.DoesNotContain(collection: arguments, filter: static argument => argument.Contains(value: "DesignTimeBuild", comparisonType: StringComparison.Ordinal));
                    _ = directory.WriteText(name: "run/cli/Puck.Cli.dll", text: "private current-model build");
                    return Result(exitCode: 0, stdout: "bootstrap build complete");
                }
                Assert.True(condition: File.Exists(path: arguments[0]));
                Assert.Equal(expected: "schema", actual: arguments[1]);
                Assert.DoesNotContain(expected: "--bootstrap", collection: arguments);
                Assert.Equal(expected: check ? new[] { "--check" } : new[] { "--bundle", "--output", output }, actual: arguments.Skip(count: 2));
                return Result(exitCode: 0, stdout: "same schema generator complete");
            }
        );

        Assert.Equal(expected: 0, actual: exit);
        Assert.Equal(expected: 2, actual: calls.Count);
        Assert.Equal(expected: "bootstrap build complete", actual: File.ReadAllText(path: directory.PathOf(name: "run/logs/Puck.Cli.build.log")));
        Assert.Contains(expectedSubstring: "same schema generator complete", actualString: File.ReadAllText(path: directory.PathOf(name: "run/logs/schema.bootstrap.log")));
        Assert.False(condition: Directory.Exists(path: directory.PathOf(name: "src/Puck.Cli/obj")));
        Assert.False(condition: Directory.Exists(path: directory.PathOf(name: "src/Puck.Cli/bin")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void AFailedOrMissingCurrentBuildCannotLaunchAStaleGenerator(int buildExit) {
        using var directory = new TemporaryDirectory();
        var calls = 0;
        var exit = SchemaBootstrap.RunInDirectory(
            repositoryRoot: directory.RootPath, directory: directory.PathOf(name: "run"), bundle: false, check: false,
            output: null, clock: TimeProvider.System,
            runner: (_, _) => {
                calls++;
                return Result(exitCode: buildExit, stdout: "current build diagnostic");
            }
        );

        Assert.Equal(expected: CliExit.Refused, actual: exit);
        Assert.Equal(expected: 1, actual: calls);
        Assert.Equal(expected: "current build diagnostic", actual: File.ReadAllText(path: directory.PathOf(name: "run/logs/Puck.Cli.build.log")));
        Assert.False(condition: File.Exists(path: directory.PathOf(name: "run/logs/schema.bootstrap.log")));
    }

    [Fact]
    public void ABootstrapRootCannotInvokeAModelTableConsumer() {
        var root = PuckRootCommand.Create(clock: TimeProvider.System, schemaBootstrap: true);

        Assert.Equal(expected: "schema", actual: Assert.Single(collection: root.Subcommands).Name);
        Assert.Empty(collection: root.Parse(args: ["schema", "--check"]).Errors);
        Assert.NotEmpty(collection: root.Parse(args: ["compile", "fixture.puck"]).Errors);
        var normal = PuckRootCommand.Create(clock: TimeProvider.System, schemaBootstrap: false);
        Assert.Contains(collection: normal.Subcommands, filter: static command => command.Name == "compile");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABootstrapModelTableRefusesInsteadOfInventingAnEmptyShape(bool lookup) {
        var source = File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "src/Puck.World.Schema/WorldModelShape.cs"));
        var parse = new CSharpParseOptions(preprocessorSymbols: ["PUCK_SCHEMA_BOOTSTRAP"]);
        var references = ((string)AppContext.GetData(name: "TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(separator: Path.PathSeparator)
            .Where(predicate: static path => !Path.GetFileName(path: path).StartsWith(value: "Puck.", comparisonType: StringComparison.Ordinal))
            .Select(selector: static path => MetadataReference.CreateFromFile(path: path));
        var compilation = CSharpCompilation.Create(
            assemblyName: "SchemaBootstrapLawModel",
            syntaxTrees: [
                CSharpSyntaxTree.ParseText(text: source, options: parse),
                CSharpSyntaxTree.ParseText(text: "global using System; global using System.Collections.Generic; namespace Puck.World { public sealed class WorldDefinition { } public sealed class WorldJsonContext { } }", options: parse)
            ],
            references: references,
            options: new CSharpCompilationOptions(outputKind: OutputKind.DynamicallyLinkedLibrary)
        );
        using var image = new MemoryStream();
        var emitted = compilation.Emit(peStream: image);
        Assert.True(condition: emitted.Success, userMessage: string.Join(separator: "\n", values: emitted.Diagnostics));
        image.Position = 0;
        var context = new AssemblyLoadContext(name: "schema-bootstrap-law", isCollectible: true);
        try {
            var assembly = context.LoadFromStream(assembly: image);
            var shape = assembly.GetType(name: "Puck.World.WorldModelShape", throwOnError: true)!;
            var failure = Assert.Throws<TargetInvocationException>(testCode: () => {
                if (lookup) {
                    _ = shape.GetMethod(name: "Of")!.Invoke(obj: null, parameters: [assembly.GetType(name: "Puck.World.WorldDefinition", throwOnError: true)!]);
                } else {
                    _ = shape.GetProperty(name: "Types")!.GetValue(obj: null);
                }
            });
            var refusal = Assert.IsType<InvalidOperationException>(@object: failure.InnerException);
            Assert.Contains(expectedSubstring: "schema bootstrap assembly has no generated model table", actualString: refusal.Message);
        } finally {
            context.Unload();
        }
    }
}
