using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// THE LAW: a fullscreen pass whose input changes size replaces its executor without draining the device. The
/// replaced executor keeps every object while a downstream reader may still sample its last image, and releases them
/// once its successor's second submission after the replacement has
/// completed.
/// </summary>
public sealed class FullscreenPassNodeRetirementLawTests {
    private static string FilmGrainManifestPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Shaders",
            "Sdf",
            "sdf-film-grain.puck.shader.json"
        );

    // Produces until the pass presents an image from an executor built for the inner node's current size.
    private static void ProduceUntilPresented(FullscreenPassNode node, FakePipelineGpu gpu) {
        var submissions = gpu.Submissions;

        Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => (!node.ProduceFrame(context: default).IsEmpty && (gpu.Submissions > submissions)),
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: "The executor never presented."
        );
    }

    [Fact]
    public void AReplacedExecutorRetiresOnItsSuccessorsSubmissionsWithoutADrain() {
        var manifest = ShaderSetManifest.Load(manifestPath: FilmGrainManifestPath);
        var gpu = new FakePipelineGpu();
        var inner = new ResizableImageNode();
        using var node = new FullscreenPassNode(
            config: manifest.BindConfig(config: null),
            height: 64,
            hostsOnDirectX: false,
            inner: inner,
            manifest: manifest,
            deviceContext: gpu,
            width: 64
        );

        ProduceUntilPresented(gpu: gpu, node: node);

        var first = gpu.CreatedObjects.ToArray();
        var drains = gpu.WaitIdleCount;

        // A new input size builds a successor; the queue is held so no submission completes.
        gpu.QueueHeld = true;
        inner.Width = 96;
        ProduceUntilPresented(gpu: gpu, node: node);
        for (var frame = 0; (frame < 4); frame++) {
            _ = node.ProduceFrame(context: default);
        }

        Assert.Equal(expected: drains, actual: gpu.WaitIdleCount);
        Assert.All(collection: first, action: static created => Assert.Equal(expected: 0, actual: created.DisposeCount));

        // Once the queue finishes, the next frame releases every object of the replaced executor, still without a drain.
        gpu.QueueHeld = false;
        _ = node.ProduceFrame(context: default);

        Assert.Equal(expected: drains, actual: gpu.WaitIdleCount);
        Assert.All(collection: first, action: static created => Assert.Equal(expected: 1, actual: created.DisposeCount));
    }

    private sealed class ResizableImageNode : IRenderNode {
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
}
