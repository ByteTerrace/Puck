using System.Diagnostics;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: a creation's bake is one set of bytes on every host. The real <c>puck compile</c>
/// process, run once as this host's JIT compiles it and once with every hardware intrinsic withheld
/// (<c>DOTNET_EnableHWIntrinsic=0</c>: no SIMD, no fused multiply-add, <see cref="System.Numerics.Vector{T}"/> one lane
/// wide), writes the same bake pack, so a bake whose bytes follow the host's vector width, its multiply-add fusion or a
/// width-dependent reduction fails here on any machine rather than only against a pin recorded on another one. Each run
/// bakes into a memory-only cache, so neither reads the other's outcome.</summary>
public sealed class BakePortabilityLawTests {
    // One creation that bakes a mesh, its surface textures and its impostor.
    private const string Document = """
        {
          "schema": "puck.world.definition.v1",
          "documentId": "bake-portability",
          "prototypes": [
            { "id": "pip", "document": { "schema": "puck.creation.v1", "name": "pip", "palette": [{ "color": "#CC3322", "emissive": 0, "specular": 0, "roughness": 0 }], "shapes": [{ "id": 0, "name": "pip", "type": "Sphere", "position": [0, 0.5, 0], "rotation": [0, 0, 0, 1], "scale": [0.5, 0.5, 0.5], "material": 0, "blend": "Union" }] } }
          ]
        }
        """;

    // The runtime's instruction-set switches a test host could carry into its children; each run sets them itself.
    private static readonly string[] IsaKnobs = ["DOTNET_EnableHWIntrinsic", "DOTNET_EnableAVX", "DOTNET_EnableAVX2", "DOTNET_EnableAVX512F", "DOTNET_EnableFMA", "DOTNET_PreferredVectorBitWidth"];

    [Fact]
    public async Task ABakeIsTheSameBytesWithAndWithoutHardwareIntrinsics() {
        using var temporary = new TemporaryDirectory(prefix: "puck-bake-portability-");
        var token = TestContext.Current.CancellationToken;
        var runs = await Task.WhenAll(
            Compile(directory: temporary.PathOf(name: "host"), intrinsics: true, token: token),
            Compile(directory: temporary.PathOf(name: "scalar"), intrinsics: false, token: token)
        );

        Assert.True(condition: runs[0].SequenceEqual(second: runs[1]), userMessage: $"the bake pack is {runs[0].Length} bytes as this host compiles it and {runs[1].Length} bytes without hardware intrinsics, and they differ at byte {runs[0].Zip(second: runs[1]).TakeWhile(predicate: static pair => (pair.First == pair.Second)).Count()}: a bake reads the host's vector width or multiply-add fusion");
    }

    // Compiles the world in `directory` as its own puck process and returns the bake pack it writes.
    private static async Task<byte[]> Compile(string directory, bool intrinsics, CancellationToken token) {
        _ = Directory.CreateDirectory(path: directory);
        var document = Path.Combine(path1: directory, path2: "bakes.world.json");

        await File.WriteAllTextAsync(cancellationToken: token, contents: Document, path: document);

        var start = new ProcessStartInfo(fileName: "dotnet") {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = directory,
        };

        foreach (var knob in IsaKnobs) {
            _ = start.Environment.Remove(key: knob);
        }
        if (!intrinsics) {
            start.Environment["DOTNET_EnableHWIntrinsic"] = "0";
        }
        foreach (var argument in new[] { CliPaths.Tool, "compile", document }) {
            start.ArgumentList.Add(item: argument);
        }
        using var process = Process.Start(startInfo: start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken: token);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken: token);

        await process.WaitForExitAsync(cancellationToken: token);
        Assert.True(condition: (process.ExitCode == 0), userMessage: $"puck compile exited {process.ExitCode} ({(intrinsics ? "host" : "scalar")}):\n{await output}\n{await errors}");
        _ = await output;
        _ = await errors;

        return await File.ReadAllBytesAsync(cancellationToken: token, path: Path.Combine(path1: directory, path2: Puck.World.WorldBakePack.FileName));
    }
}
