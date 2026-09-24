using Puck.Abstractions.Counting;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for the shader loads counted in <c>Puck.Shaders</c>: a shader-set manifest load and a fullscreen pass's executor
/// load each count once, with exactly the bytecode bytes they read off disk, into the counts they were handed; a load
/// that reuses what it already read counts nothing; the process sources carry their own names; and every kind is
/// classified as deterministic within one backend.
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
    private static string CopyShaderAssets() {
        var shaderDirectory = Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders");
        var scratch = Directory.CreateTempSubdirectory(prefix: "puck-fullscreen-pass-").FullName;

        foreach (var file in Directory.EnumerateFiles(
            path: shaderDirectory,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*"
        )) {
            var relative = Path.GetRelativePath(
                path: file,
                relativeTo: shaderDirectory
            );
            var destination = Path.Combine(path1: scratch, path2: relative);

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
            File.Copy(
                destFileName: destination,
                sourceFileName: file
            );
        }

        return scratch;
    }

    private sealed class ImageNode : IRenderNode {
        public NodeDescriptor Descriptor { get; } = new(
            Name: "image",
            SurfaceId: SurfaceId.New()
        );
        public uint Width { get; set; } = 64;

        public void Dispose() { }
        public Surface ProduceFrame(in FrameContext context) =>
            Surface.SameDeviceImage(
                format: SurfaceFormat.R8G8B8A8Unorm,
                height: 64,
                imageHandle: 0x21,
                imageViewHandle: 0x22,
                width: Width
            );
    }

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
    public void AFullscreenPassLoadsItsBytecodeOncePerInputExtent() {
        var manifest = ShaderSetManifest.Load(manifestPath: FilmGrainManifestPath);
        var work = new WorkCounterSet(
            kinds: [FullscreenPassNode.Loads, FullscreenPassNode.BytecodeBytes],
            name: FullscreenPassNode.LoadWorkSourceName
        );
        var inner = new ImageNode();
        var bytes = BytesOf(manifest, ".spv", ".dxil");

        using var node = new FullscreenPassNode(
            config: manifest.BindConfig(config: null),
            height: 64,
            hostsOnDirectX: false,
            inner: inner,
            loadWork: work,
            manifest: manifest,
            services: new FakePipelineGpu(),
            width: 64
        );

        Assert.Equal(expected: 0L, actual: work.Read(kind: FullscreenPassNode.Loads));

        _ = node.ProduceFrame(context: default);
        _ = node.ProduceFrame(context: default);

        Assert.Equal(expected: 1L, actual: work.Read(kind: FullscreenPassNode.Loads));
        Assert.Equal(expected: bytes, actual: work.Read(kind: FullscreenPassNode.BytecodeBytes));

        inner.Width = 32;
        _ = node.ProduceFrame(context: default);

        Assert.Equal(expected: 2L, actual: work.Read(kind: FullscreenPassNode.Loads));
        Assert.Equal(expected: (2L * bytes), actual: work.Read(kind: FullscreenPassNode.BytecodeBytes));
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
    public void AFullscreenPassExecutorReadsNoBytecodeFileTheManifestAlreadyValidated() {
        var scratch = CopyShaderAssets();

        try {
            var manifest = ShaderSetManifest.Load(manifestPath: Path.Combine(
                path1: scratch,
                path2: "Sdf",
                path3: "sdf-film-grain.puck.shader.json"
            ));

            // Every bytecode file the manifest validated is gone: a second on-disk read would now fail.
            foreach (var extension in new[] { ".spv", ".dxil" }) {
                File.Delete(path: manifest.BytecodePath(
                    bytecodeExtension: extension,
                    stem: manifest.Stages.Vertex!
                ));
                File.Delete(path: manifest.BytecodePath(
                    bytecodeExtension: extension,
                    stem: manifest.Stages.Fragment!
                ));
            }

            var work = new WorkCounterSet(
                kinds: [FullscreenPassNode.Loads, FullscreenPassNode.BytecodeBytes],
                name: FullscreenPassNode.LoadWorkSourceName
            );

            using var node = new FullscreenPassNode(
                config: manifest.BindConfig(config: null),
                height: 64,
                hostsOnDirectX: false,
                inner: new ImageNode(),
                loadWork: work,
                manifest: manifest,
                services: new FakePipelineGpu(),
                width: 64
            );

            _ = node.ProduceFrame(context: default);
            _ = node.ProduceFrame(context: default);

            Assert.Equal(expected: 1L, actual: work.Read(kind: FullscreenPassNode.Loads));
        } finally {
            Directory.Delete(
                path: scratch,
                recursive: true
            );
        }
    }
    [Fact]
    public void AFullscreenPassRefusesCountsThatLackItsKinds() {
        var manifest = ShaderSetManifest.Load(manifestPath: FilmGrainManifestPath);

        _ = Assert.Throws<ArgumentException>(testCode: () => new FullscreenPassNode(
            config: manifest.BindConfig(config: null),
            height: 64,
            hostsOnDirectX: false,
            inner: new ImageNode(),
            loadWork: new WorkCounterSet(
                kinds: [ShaderSetManifest.Loads, ShaderSetManifest.BytecodeBytes],
                name: ShaderSetManifest.LoadWorkSourceName
            ),
            manifest: manifest,
            services: new FakePipelineGpu(),
            width: 64
        ));
    }
    [Fact]
    public void TheProcessLoadSourcesAreNamedAndClassified() {
        Assert.Equal(expected: "shaders.set-manifest", actual: ShaderSetManifest.LoadWork.Name);
        Assert.Equal(expected: "shaders.fullscreen-pass", actual: FullscreenPassNode.LoadWork.Name);
        Assert.Equal(
            expected: ["shaders.set-manifest.loads", "shaders.set-manifest.bytecode-bytes"],
            actual: ShaderSetManifest.LoadWork.WorkKinds.ToArray().Select(selector: static kind => kind.Name)
        );
        Assert.Equal(
            expected: ["shaders.fullscreen-pass.loads", "shaders.fullscreen-pass.bytecode-bytes"],
            actual: FullscreenPassNode.LoadWork.WorkKinds.ToArray().Select(selector: static kind => kind.Name)
        );
        Assert.All(
            action: static kind => Assert.Equal(expected: WorkClass.PerBackendDeterministic, actual: kind.Class),
            collection: [ShaderSetManifest.Loads, ShaderSetManifest.BytecodeBytes, FullscreenPassNode.Loads, FullscreenPassNode.BytecodeBytes]
        );
    }
}
