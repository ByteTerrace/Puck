using Puck.Testing;
using Xunit;

namespace Puck.Cli.Source.Tests;

/// <summary>
/// THE LAW: a project naming an analyzer whose file does not exist, as every project does for a project-built analyzer
/// not yet built in the loaded configuration, still answers <c>puck references</c>. The unresolvable analyzer is named
/// on stderr and dropped; the search neither throws nor loses the references it would otherwise report.
/// </summary>
public sealed class ReferencesUnresolvedAnalyzerLawTests {
    [Fact]
    public async Task AnUnresolvableAnalyzerIsNamedAndSkipped() {
        using var scratch = new TemporaryDirectory(prefix: "puck-references-analyzer-");

        CliScratchDirectories.PinSdk(directory: scratch.RootPath);
        var directory = scratch.RootPath;

        var project = Path.Combine(
            path1: directory,
            path2: "Probe.csproj"
        );

        await File.WriteAllTextAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            contents: """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><Analyzer Include="absent/Absent.Analyzer.dll" /></ItemGroup>
            </Project>
            """,
            path: project
        );
        await File.WriteAllTextAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            contents: "public sealed class Probe { public int Value() => 1; public int Read() => Value(); }",
            path: Path.Combine(
                path1: directory,
                path2: "Probe.cs"
            )
        );
        _ = await CliProcess.RunCheckedAsync(
            arguments: ["restore", "--disable-build-servers", project],
            cancellationToken: TestContext.Current.CancellationToken,
            capture: true,
            fileName: "dotnet",
            workingDirectory: directory
        );

        var (exitCode, output, error) = await ConsoleCapture.RunSplitAsync(run: () => SuiteRoot.InvokeAsync(args: ["references", "Value", "--project", project]));

        Assert.True(
            condition: (exitCode == 0),
            userMessage: $"references exited {exitCode}:{Environment.NewLine}{error}"
        );
        Assert.Contains(
            actualString: error,
            expectedSubstring: "Absent.Analyzer.dll"
        );
        Assert.Contains(
            actualString: output,
            expectedSubstring: "ref Method Probe.Value()"
        );
    }
}
