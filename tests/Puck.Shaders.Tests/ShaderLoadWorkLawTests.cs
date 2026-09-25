using Puck.Abstractions.Counting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for the shader loads counted in <c>Puck.Shaders</c>: a shader-set manifest load counts once, with exactly the
/// bytecode bytes it read off disk, into the counts it was handed; the process source carries its own name; and every
/// kind is classified as deterministic within one backend.
/// </summary>
public sealed class ShaderLoadWorkLawTests {
    private static string FilmGrainManifestPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Shaders",
            "Sdf",
            "sdf-film-grain.puck.shader.json"
        );

    private static long BytesOf(ShaderSetManifest manifest, params string[] extensions) =>
        new[] { manifest.Stages.Vertex!, manifest.Stages.Fragment! }
            .SelectMany(selector: stem => extensions.Select(selector: extension => manifest.BytecodePath(
                bytecodeExtension: extension,
                stem: stem
            )))
            .Where(predicate: File.Exists)
            .Sum(selector: static path => new FileInfo(fileName: path).Length);

    [Fact]
    public void AManifestLoadCountsOnceWithEveryBytecodeByteItValidated() {
        var work = new WorkCounterSet(
            kinds: [ShaderSetManifest.Loads, ShaderSetManifest.BytecodeBytes],
            name: ShaderSetManifest.LoadWorkSourceName
        );
        var manifest = ShaderSetManifest.Load(
            manifestPath: FilmGrainManifestPath,
            work: work
        );
        var bytes = BytesOf(manifest, ".spv", ".dxil");

        Assert.True(condition: (bytes > 0L));
        Assert.Equal(expected: 1L, actual: work.Read(kind: ShaderSetManifest.Loads));
        Assert.Equal(expected: bytes, actual: work.Read(kind: ShaderSetManifest.BytecodeBytes));

        _ = ShaderSetManifest.Load(
            manifestPath: FilmGrainManifestPath,
            work: work
        );

        Assert.Equal(expected: 2L, actual: work.Read(kind: ShaderSetManifest.Loads));
        Assert.Equal(expected: (2L * bytes), actual: work.Read(kind: ShaderSetManifest.BytecodeBytes));
    }
    [Fact]
    public void AMissingManifestIsNotALoad() {
        var work = new WorkCounterSet(
            kinds: [ShaderSetManifest.Loads, ShaderSetManifest.BytecodeBytes],
            name: ShaderSetManifest.LoadWorkSourceName
        );

        _ = Assert.Throws<FileNotFoundException>(testCode: () => ShaderSetManifest.Load(
            manifestPath: Path.Combine(
                path1: AppContext.BaseDirectory,
                path2: "absent.puck.shader.json"
            ),
            work: work
        ));
        Assert.Equal(expected: 0L, actual: work.Read(kind: ShaderSetManifest.Loads));
    }
    [Fact]
    public void AManifestLoadCarriesTheValidatedBytecodeForEveryStage() {
        var manifest = ShaderSetManifest.Load(manifestPath: FilmGrainManifestPath);

        foreach (var extension in new[] { ".spv", ".dxil" }) {
            var vertexKey = $"{manifest.Stages.Vertex}{extension}";
            var fragmentKey = $"{manifest.Stages.Fragment}{extension}";

            Assert.Equal(
                expected: File.ReadAllBytes(path: manifest.BytecodePath(
                    bytecodeExtension: extension,
                    stem: manifest.Stages.Vertex!
                )),
                actual: manifest.Bytecode[vertexKey].ToArray()
            );
            Assert.Equal(
                expected: File.ReadAllBytes(path: manifest.BytecodePath(
                    bytecodeExtension: extension,
                    stem: manifest.Stages.Fragment!
                )),
                actual: manifest.Bytecode[fragmentKey].ToArray()
            );
        }
    }
    [Fact]
    public void TheProcessLoadSourcesAreNamedAndClassified() {
        Assert.Equal(expected: "shaders.set-manifest", actual: ShaderSetManifest.LoadWork.Name);
        Assert.Equal(
            expected: ["shaders.set-manifest.loads", "shaders.set-manifest.bytecode-bytes"],
            actual: ShaderSetManifest.LoadWork.WorkKinds.ToArray().Select(selector: static kind => kind.Name)
        );
        Assert.All(
            action: static kind => Assert.Equal(expected: WorkClass.PerBackendDeterministic, actual: kind.Class),
            collection: [ShaderSetManifest.Loads, ShaderSetManifest.BytecodeBytes]
        );
    }
}
