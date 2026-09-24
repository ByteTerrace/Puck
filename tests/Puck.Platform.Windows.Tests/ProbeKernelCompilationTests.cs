using Puck.Shaders;
using Xunit;

namespace Puck.Platform.Windows.Tests;

/// <summary>A kernel-class probe compiles at build, never on the camera's device: every entry point a shipped kind's
/// manifest names has Direct3D 11 compute bytecode where <see cref="ProbeKindManifest.KernelBytecodePath"/> looks for
/// it.</summary>
public sealed class ProbeKernelCompilationTests {
    public static TheoryData<string> ShippedKinds => new(values: Directory.EnumerateFiles(
        path: RepositoryPaths.Resolve(relativePath: "src/Puck.Shaders/Assets/Probes"),
        searchPattern: ("*" + ProbeKindManifest.FileSuffix)
    ).Select(selector: static path => Path.GetFileName(path: path)).Order(comparer: StringComparer.Ordinal).ToArray());

    [MemberData(memberName: nameof(ShippedKinds))]
    [Theory]
    public void Every_kernel_entry_point_a_shipped_kind_names_has_precompiled_bytecode(string manifestName) {
        var manifest = ProbeKindManifest.Load(manifestPath: RepositoryPaths.Resolve(relativePath: $"src/Puck.Shaders/Assets/Probes/{manifestName}"));

        if (manifest.Kernel is not { } kernel) {
            return;
        }

        foreach (var entry in ((string[])[kernel.Accumulate, kernel.Finalize])) {
            var bytecode = File.ReadAllBytes(path: manifest.KernelBytecodePath(entry: entry));

            // A DXBC container opens with its four-character code.
            Assert.Equal(
                actual: System.Text.Encoding.ASCII.GetString(bytes: bytecode, count: 4, index: 0),
                expected: "DXBC"
            );
        }
    }
}
