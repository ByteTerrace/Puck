using Microsoft.CodeAnalysis.CSharp;

using Puck.Cli.Format;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Pins what the semantic <c>named-args</c> phase is allowed to believe about a project's build closure. The phase
/// resolves each call against the project's real output, so "is this project built?" decides whether a run's silence
/// means "everything was named" or "almost nothing could be looked at" — and it decides whether writing is safe at
/// all. Phase 0 (<c>dotnet format whitespace</c>) runs first in the same invocation and leaves an empty <c>bin</c>
/// and a generated global-usings file behind it, so neither of those existing is evidence of anything.
/// </summary>
public sealed class FormatNamedArgsCoverageTests : IDisposable {
    private readonly string m_root;

    /// <summary>Creates the scratch project directory this fixture's cases build their layouts inside.</summary>
    public FormatNamedArgsCoverageTests() {
        m_root = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-cli-tests-named-args-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path: m_root);
    }

    /// <summary>Removes the scratch project directory.</summary>
    public void Dispose() {
        try {
            Directory.Delete(path: m_root, recursive: true);
        } catch (DirectoryNotFoundException) {
        }

        GC.SuppressFinalize(obj: this);
    }
    /// <summary>
    /// The exact state phase 0 leaves an unbuilt project in: a global-usings file under <c>obj</c> and an empty
    /// <c>bin</c>. The closure is framework-only, so the run must call itself degraded — otherwise the "not built"
    /// note never prints and the unresolved-call count points at guidance nobody was given.
    /// </summary>
    [Fact]
    public void AnEmptyBinDirectoryIsNotEvidenceOfABuild() {
        WriteProjectLayout(withOwnOutput: false);
        NamedArgsPhase.BuildProjectCompilation(degraded: out var degraded, parseOptions: new CSharpParseOptions(languageVersion: LanguageVersion.Preview), projectRoot: m_root, trees: []);

        Assert.True(condition: degraded);
    }
    /// <summary>
    /// The control for the case above: the project's own output assembly under <c>bin</c> IS evidence of a build, so
    /// the same layout with it present is not degraded. Without this the fix could be "always degraded".
    /// </summary>
    [Fact]
    public void TheProjectsOwnOutputAssemblyIsEvidenceOfABuild() {
        WriteProjectLayout(withOwnOutput: true);
        NamedArgsPhase.BuildProjectCompilation(degraded: out var degraded, parseOptions: new CSharpParseOptions(languageVersion: LanguageVersion.Preview), projectRoot: m_root, trees: []);

        Assert.False(condition: degraded);
    }
    /// <summary>
    /// Write mode over a project whose closure is framework-only must leave the source alone and say so through the
    /// exit code. Naming arguments from a partial semantic model is a guess written to disk, and the pass's own write
    /// guard only counts parse errors — a call named against the wrong overload still parses.
    /// </summary>
    [Fact]
    public void WriteModeDeclinesAProjectThatIsNotBuilt() {
        WriteProjectLayout(withOwnOutput: false);

        var file = Path.Combine(path1: m_root, path2: "Sample.cs");
        var before = File.ReadAllText(path: file);
        var code = NamedArgsPhase.Run(rootArgument: m_root, verify: false, whatIf: false);

        Assert.Equal(actual: File.ReadAllText(path: file), expected: before);
        Assert.Equal(actual: code, expected: 1);
    }

    // The layout phase 0 leaves behind: a project file, one source file holding a positional call the pass would
    // name, the SDK-generated global-usings file, and a `bin` that is empty unless the project's own output is asked
    // for. A zero-byte assembly is enough — the degraded probe asks whether it EXISTS, and the reference loader drops
    // anything that is not readable metadata.
    private void WriteProjectLayout(bool withOwnOutput) {
        var binDirectory = Path.Combine(path1: m_root, path2: "bin", path3: "Debug", path4: "net10.0");
        var objDirectory = Path.Combine(path1: m_root, path2: "obj", path3: "Debug", path4: "net10.0");

        Directory.CreateDirectory(path: binDirectory);
        Directory.CreateDirectory(path: objDirectory);
        File.WriteAllText(contents: "<Project Sdk=\"Microsoft.NET.Sdk\" />\n", path: Path.Combine(path1: m_root, path2: "Sample.csproj"));
        File.WriteAllText(contents: "global using System;\n", path: Path.Combine(path1: objDirectory, path2: "Sample.GlobalUsings.g.cs"));
        File.WriteAllText(
            contents: "namespace Sample;\n\ninternal static class Widget {\n    public static int Add(int left, int right) => (left + right);\n\n    public static int Use() => Add(1, 2);\n}\n",
            path: Path.Combine(path1: m_root, path2: "Sample.cs"));

        if (withOwnOutput) {
            File.WriteAllBytes(bytes: [], path: Path.Combine(path1: binDirectory, path2: "Sample.dll"));
        }
    }
}
