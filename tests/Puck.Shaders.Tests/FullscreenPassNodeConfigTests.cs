using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed class FullscreenPassNodeConfigTests {
    private static string FilmGrainManifestPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Shaders",
            "Sdf",
            "sdf-film-grain.puck.shader.json"
        );

    private static FullscreenPassNode CreateNode(IRenderNode? inner = null) {
        var manifest = ShaderSetManifest.Load(manifestPath: FilmGrainManifestPath);
        var config = manifest.BindConfig(config: null);

        return new FullscreenPassNode(
            inner: (inner ?? new StubRenderNode()),
            manifest: manifest,
            config: config,
            deviceContext: new FakePipelineGpu(),
            hostsOnDirectX: false,
            width: 64,
            height: 64
        );
    }

    [Fact]
    public async Task CaptureIsNotReplacedAndDisposalCompletesAnUnservedRequest() {
        var node = CreateNode();
        var first = new FrameCaptureRequest(path: "first.png");

        node.RequestCapture(request: first);
        Assert.Equal(
            "first.png",
            node.PendingCapturePath
        );
        Assert.Throws<InvalidOperationException>(testCode: () => node.RequestCapture(request: new FrameCaptureRequest(path: "second.png")));
        Assert.False(condition: first.Completion.IsCompleted);
        node.Dispose();
        Assert.IsType<ObjectDisposedException>(@object: (await first.Completion).Error);
        Assert.Throws<ObjectDisposedException>(testCode: () => node.RequestCapture(request: new FrameCaptureRequest(path: "closed.png")));
    }
    [Fact]
    public async Task PassThroughForwardsTheSameRequestAndReportsInnerShutdown() {
        var inner = new CaptureStub();
        var node = CreateNode(inner: inner);
        var request = new FrameCaptureRequest(path: "forwarded.png");

        node.RequestCapture(request: request);
        _ = node.ProduceFrame(context: default);
        Assert.Same(
            request,
            inner.Pending
        );
        Assert.Equal(
            request.Path,
            node.PendingCapturePath
        );
        Assert.False(condition: request.Completion.IsCompleted);
        Assert.Throws<InvalidOperationException>(testCode: () => node.RequestCapture(request: new FrameCaptureRequest(path: "busy.png")));
        node.Dispose();
        Assert.IsType<ObjectDisposedException>(@object: (await request.Completion).Error);
    }
    [Fact]
    public void TrySetConfig_refuses_a_uint_field() {
        using var node = CreateNode();

        Assert.False(condition: node.TrySetConfig(
            field: "seed",
            value: 1f
        ));
        Assert.Equal(
            expected: 0u,
            actual: node.Config["seed"].ComponentBits(index: 0)
        );
    }
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [Theory]
    public void TrySetConfig_refuses_a_value_the_field_schema_would_refuse_at_bind_time(float value) {
        using var node = CreateNode();

        Assert.False(condition: node.TrySetConfig(
            field: "intensity",
            value: value
        ));
        Assert.Equal(
            expected: 0.05f,
            actual: BitConverter.UInt32BitsToSingle(value: node.Config["intensity"].ComponentBits(index: 0))
        );
    }
    [Fact]
    public void TrySetConfig_refuses_an_unknown_field() {
        using var node = CreateNode();

        Assert.False(condition: node.TrySetConfig(
            field: "no-such-field",
            value: 1f
        ));
        Assert.Equal(
            expected: 0.05f,
            actual: BitConverter.UInt32BitsToSingle(value: node.Config["intensity"].ComponentBits(index: 0))
        );
    }
    [Fact]
    public void TrySetConfig_rewrites_a_field_in_place_after_its_first_write() {
        using var node = CreateNode();

        Assert.True(condition: node.TrySetConfig(
            field: "intensity",
            value: 0.25f
        ));

        var live = node.Config;
        var liveValue = live["intensity"];

        Assert.True(condition: node.TrySetConfig(
            field: "intensity",
            value: 0.75f
        ));
        Assert.Same(
            expected: live,
            actual: node.Config
        );
        Assert.Same(
            expected: liveValue,
            actual: node.Config["intensity"]
        );
        Assert.Equal(
            expected: 0.75f,
            actual: BitConverter.UInt32BitsToSingle(value: liveValue.ComponentBits(index: 0))
        );
    }
    [Fact]
    public void TrySetConfig_round_trips_through_config_and_its_json() {
        using var node = CreateNode();

        Assert.Equal(
            expected: 0.05f,
            actual: BitConverter.UInt32BitsToSingle(value: node.Config["intensity"].ComponentBits(index: 0))
        );
        Assert.True(condition: node.TrySetConfig(
            field: "intensity",
            value: 0.42f
        ));
        Assert.Equal(
            expected: 0.42f,
            actual: BitConverter.UInt32BitsToSingle(value: node.Config["intensity"].ComponentBits(index: 0))
        );

        var json = node.Config.ToJson();

        Assert.Equal(
            expected: 0.42f,
            actual: json.GetProperty(propertyName: "intensity").GetSingle()
        );
        Assert.Equal(
            expected: 24u,
            actual: json.GetProperty(propertyName: "flickerHz").GetUInt32()
        );
    }

    private sealed class CaptureStub : IRenderNode, ICaptureRequestTarget {
        public NodeDescriptor Descriptor { get; } = new(
            Name: "capture-stub",
            SurfaceId: SurfaceId.New()
        );
        public FrameCaptureRequest? Pending { get; private set; }
        public string? PendingCapturePath => Pending?.Path;

        public void Dispose() => Pending?.TryFail(error: new ObjectDisposedException(objectName: nameof(CaptureStub)));
        public Surface ProduceFrame(in FrameContext context) => default;
        public void RequestCapture(FrameCaptureRequest request) => Pending = request;
    }
    // A render node this test never drives past construction — FullscreenPassNode's constructor reads the
    // manifest and its config, but calls ProduceFrame on nothing.
    private sealed class StubRenderNode : IRenderNode {
        public NodeDescriptor Descriptor { get; } = new NodeDescriptor(
            Name: "stub",
            SurfaceId: SurfaceId.New()
        );

        public void Dispose() { }
        public Surface ProduceFrame(in FrameContext context) => throw new NotSupportedException();
    }
}
