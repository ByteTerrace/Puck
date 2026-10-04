using Puck.Abstractions;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the places a directory's listing order would reach an error message. A listing is the host's order, not the
/// repository's: Linux gives a file system's own order, Windows gives name order, and a message that names whichever entry
/// came first is a different message on each. These build directories whose entries make the order matter and assert the
/// ordinal result, and were proved red against the unsorted code under a shim that serves every directory reversed and
/// shuffled.</summary>
public sealed class DirectoryOrderLawTests {
    private static readonly string[] Names = ["zeta", "mid", "alpha", "omega", "beta", "kappa"];

    [Fact]
    public void ADuplicateProbeKindNamesTheTwoOrdinallyFirstPaths() {
        using var directory = new TemporaryDirectory(prefix: "puck-probe-order-");

        foreach (var name in Names) {
            Directory.CreateDirectory(path: directory.PathOf(name: name));
            File.WriteAllText(
                contents: "{}",
                path: directory.PathOf(name: $"{name}/dup{ProbeKindManifest.FileSuffix}")
            );
        }

        var refusal = Assert.Throws<InvalidDataException>(testCode: () => ProbeKindCatalog.Scan(rootDirectory: directory.RootPath));

        // The paths the walk meets first, in ordinal order, are alpha's and then beta's, whatever the file system lists.
        Assert.Contains(
            actualString: refusal.Message.Replace(newChar: '/', oldChar: '\\'),
            expectedSubstring: $"'{directory.PathOf(name: ("alpha/dup" + ProbeKindManifest.FileSuffix)).Replace(newChar: '/', oldChar: '\\')}' and '{directory.PathOf(name: ("beta/dup" + ProbeKindManifest.FileSuffix)).Replace(newChar: '/', oldChar: '\\')}'"
        );
    }
    [Fact]
    public void AStrayLibraryInAnExtensionsDirectoryIsNamedOrdinallyFirst() {
        using var directory = new TemporaryDirectory(prefix: "puck-extension-order-");

        foreach (var name in Names) {
            File.WriteAllText(
                contents: string.Empty,
                path: directory.PathOf(name: $"{name}.dll")
            );
        }

        var refusal = Assert.Throws<PuckExtensionException>(testCode: () => PuckExtensionDiscovery.Compose(
            builtIns: [],
            directories: [directory.RootPath]
        ));

        Assert.Contains(
            actualString: refusal.Message.Replace(newChar: '/', oldChar: '\\'),
            expectedSubstring: $"'{directory.PathOf(name: "alpha.dll").Replace(newChar: '/', oldChar: '\\')}' sits directly"
        );
    }
}
