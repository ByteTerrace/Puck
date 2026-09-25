using System.Text.Json;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary><c>puck shaders package</c> writes a package that <c>puck shaders pipeline</c> loads from a clean directory,
/// and the shader verbs answer under the shared exit codes: 0 when it compiles, 2 for a refusal. The round trip needs
/// DXC on the search path.</summary>
public sealed class ShadersPackageCommandLawTests {
    private const string Image = """
        [[vk::binding(0, 0)]] RWTexture2D<float4> image : register(u0);

        [numthreads(8, 8, 1)]
        void main(uint3 id : SV_DispatchThreadID) {
            image[id.xy] = float4(0.25, 0.5, 0.75, 1.0);
        }
        """;

    private static bool HasDxc() =>
        (Environment.GetEnvironmentVariable(variable: "PATH") ?? string.Empty).Split(options: StringSplitOptions.RemoveEmptyEntries, separator: Path.PathSeparator).Any(predicate: static directory => (File.Exists(path: Path.Combine(path1: directory, path2: "dxc")) || File.Exists(path: Path.Combine(path1: directory, path2: "dxc.exe"))));
    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args) =>
        await ConsoleCapture.RunSplitAsync(run: () => PuckRootCommand.InvokeAsync(args: args));

    [Fact]
    public async Task A_missing_source_is_refused() {
        var root = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-cli-package-" + Guid.NewGuid().ToString(format: "N")[..12])
        );

        var (exitCode, _, error) = await RunAsync("shaders", "package", Path.Combine(path1: root, path2: "absent.hlsl"), "--output", Path.Combine(path1: root, path2: "package"));

        Assert.Equal(
            actual: exitCode,
            expected: CliExit.Refused
        );
        Assert.Contains(
            actualString: error,
            expectedSubstring: "no such file"
        );
        Assert.False(condition: Directory.Exists(path: root));
    }
    // A missing source, an unknown option value, and a package named by its manifest rather than its directory are
    // refusals, exit 2, never the exit 1 that means a source was compiled and failed.
    [Fact]
    public async Task Compile_and_pipeline_refuse_what_they_cannot_run_with_exit_two() {
        var root = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-cli-shaders-" + Guid.NewGuid().ToString(format: "N")[..12])
        );

        try {
            Directory.CreateDirectory(path: root);

            var manifest = Path.Combine(
                path1: root,
                path2: "puck.shader.package.json"
            );

            File.WriteAllText(
                contents: "{}",
                path: manifest
            );

            foreach (var args in new[] {
                new[] { "shaders", "compile", Path.Combine(path1: root, path2: "absent.hlsl"), "--out", Path.Combine(path1: root, path2: "out") },
                new[] { "shaders", "compile", manifest, "--out", Path.Combine(path1: root, path2: "out"), "--stage", "geometry" },
                new[] { "shaders", "pipeline", Path.Combine(path1: root, path2: "absent.graph.json") },
                new[] { "shaders", "pipeline", manifest },
            }) {
                var (exitCode, _, error) = await RunAsync(args: args);

                Assert.Equal(
                    actual: exitCode,
                    expected: CliExit.Refused
                );
                Assert.StartsWith(
                    actualString: error,
                    expectedStartString: $"puck shaders {args[1]}: "
                );
            }
        } finally {
            Directory.Delete(
                path: root,
                recursive: true
            );
        }
    }
    [Fact]
    public async Task A_package_relocates_reloads_and_refuses_an_altered_file() {
        Assert.SkipWhen(
            condition: !HasDxc(),
            reason: "DXC is required to compile the package."
        );

        var root = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-cli-package-" + Guid.NewGuid().ToString(format: "N")[..12])
        );

        try {
            var source = Path.Combine(path1: root, path2: "source", path3: "image.hlsl");
            var package = Path.Combine(path1: root, path2: "package");
            var cache = Path.Combine(path1: root, path2: "cache");

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: source)!);
            File.WriteAllText(
                contents: Image,
                path: source
            );

            var (built, output, _) = await RunAsync("shaders", "package", source, "--output", package, "--cache", cache, "--json");

            Assert.Equal(
                actual: built,
                expected: CliExit.Success
            );

            using (var record = JsonDocument.Parse(json: output)) {
                Assert.Equal(
                    actual: record.RootElement.GetProperty(propertyName: "status").GetString(),
                    expected: "Compiled"
                );
                Assert.Equal(
                    actual: record.RootElement.GetProperty(propertyName: "files").GetInt32(),
                    expected: 1
                );
            }

            var relocated = Path.Combine(path1: root, path2: "relocated");

            Directory.Move(
                destDirName: relocated,
                sourceDirName: package
            );
            Directory.Delete(
                path: Path.GetDirectoryName(path: source)!,
                recursive: true
            );
            Assert.Equal(
                actual: (await RunAsync("shaders", "pipeline", relocated, "--cache", Path.Combine(path1: root, path2: "clean-cache"))).ExitCode,
                expected: CliExit.Success
            );

            File.AppendAllText(
                contents: "\n// altered\n",
                path: Path.Combine(path1: relocated, path2: "image.hlsl")
            );

            var (altered, _, error) = await RunAsync("shaders", "pipeline", relocated, "--cache", cache);

            Assert.Equal(
                actual: altered,
                expected: CliExit.Refused
            );
            Assert.Contains(
                actualString: error,
                expectedSubstring: "SHADERPKG_FILE_PIN"
            );
        } finally {
            if (Directory.Exists(path: root)) {
                Directory.Delete(
                    path: root,
                    recursive: true
                );
            }
        }
    }
}
