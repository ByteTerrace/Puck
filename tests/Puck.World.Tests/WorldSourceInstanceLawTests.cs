using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Commands;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for a world's screen sources as render-graph instances. A producer, machine or probe source is an external
/// instance of <c>source.&lt;producer id&gt;</c> that carries its settings, screens showing equal sources read one
/// instance, and the instance's handle is its identity. Each registered producer registers one external-producer factory
/// under its source package, which opens the instance's feed from the instance's settings and refuses by name a feed that
/// disagrees with its registration. The typed arms' producer ids are closed to document producers.
/// </summary>
public sealed class WorldSourceInstanceLawTests {
    private static readonly FakeGpuDevice Gpu = new(reportVersion: 1);

    private static WorldScreenSource.Producer Pattern(int width) => WorldImageProducerSettings.SourceOf(
        id: WorldImageProducerSettings.TestPatternId,
        settings: new WorldTestPatternSettings(
            Height: 6,
            Width: width
        )
    );

    [Fact]
    public void ScreensShowingEqualSourcesReadOneInstanceThatCarriesItsSettings() {
        var machine = new WorldScreenSource.Machine(
            Instance: "cabinet",
            Output: "lcd"
        );
        var probe = new WorldScreenSource.Probe(Id: "relight");
        var sources = WorldSourceInstances.Of(shown: [
            Pattern(width: 128),
            null,
            new WorldScreenSource.None(),
            machine,
            Pattern(width: 128),
            Pattern(width: 64),
            probe,
            machine with { },
            new WorldScreenSource.View(CameraName: "security"),
        ]);

        Assert.Equal(
            actual: sources.Instances.Select(selector: static instance => (instance.Name, instance.ExternalPackage)),
            expected: new (string, string?)[] {
                ("source$0", "source.testPattern"),
                ("source$3", "source.machine"),
                ("source$5", "source.testPattern"),
                ("source$6", "source.probe"),
            }
        );
        Assert.Equal(
            actual: Enumerable.Range(count: 10, start: 0).Select(selector: sources.InstanceOf),
            expected: new string?[] { "source$0", null, null, "source$3", "source$0", "source$5", "source$6", "source$3", null, null }
        );
        Assert.Equal(expected: SourceHandle.Producer(name: "source$0"), actual: sources.HandleOf(screen: 4));
        Assert.All(
            action: static instance => {
                Assert.True(condition: instance.IsSource);
                Assert.Equal(expected: instance.Handle, actual: SourceHandle.Producer(name: instance.Name));
            },
            collection: sources.Instances
        );

        // Each instance rebuilds the source it was derived from, settings and all, and the set validates.
        Assert.Equal(
            actual: sources.Instances.Select(selector: WorldSourceInstances.SourceOf),
            expected: new WorldScreenSource?[] { Pattern(width: 128), machine, Pattern(width: 64), probe }
        );
        Assert.True(condition: RenderGraphInstanceSet.TryCreate(
            instances: sources.Instances,
            refusal: out var refusal,
            set: out _
        ), userMessage: refusal?.Message);
    }
    [Fact]
    public void EachProducerRegistersOneFactoryThatOpensItsInstanceFromTheInstanceSettings() {
        var producers = new WorldImageProducers();

        producers.Register(producer: new WorldTestPatternProducer());
        producers.Register(producer: new WorldQrProducer());

        var packages = new RenderGraphPackageRecorders();
        var openings = new List<WorldImageSourceOpening>();

        producers.RegisterPackages(
            adapt: opening => {
                openings.Add(item: opening);

                return new OpenedSource(opening: opening);
            },
            packages: packages
        );

        Assert.Equal(expected: ["source.qr", "source.testPattern"], actual: packages.ProducerIds);

        var sources = WorldSourceInstances.Of(shown: [Pattern(width: 96), Pattern(width: 96)]);

        Assert.True(condition: RenderGraphInstanceSet.TryCreate(
            instances: sources.Instances,
            refusal: out var setRefusal,
            set: out var set
        ), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(
            deviceContext: Gpu,
            graphs: [null],
            hostsOnDirectX: false,
            packages: packages,
            refusal: out var refusal,
            root: "source$0",
            runtime: out var runtime,
            set: set
        ), userMessage: refusal?.Message);

        using (runtime) {
            // One factory call for the one instance both screens read, opened from its settings.
            var opening = Assert.Single(collection: openings);

            Assert.Equal(expected: ("source$0", "source.testPattern"), actual: (opening.Context.Instance, opening.Context.Package));
            Assert.Null(@object: opening.Fault);
            Assert.Equal(
                expected: (WorldImageProducerSettings.TestPatternId, 96U, 6U),
                actual: (opening.Feed!.Descriptor.Producer, opening.Feed.Descriptor.Width, opening.Feed.Descriptor.Height)
            );
            Assert.Same(expected: opening.Feed, actual: ((OpenedSource)runtime.Producer(instance: 0)!).Opening.Feed);
        }
    }
    [Fact]
    public void AnInstanceWhoseFeedDisagreesWithItsRegistrationIsRefusedByName() {
        var producers = new WorldImageProducers();
        var disagreeing = new Disagreeing();

        producers.Register(producer: disagreeing);

        var packages = new RenderGraphPackageRecorders();
        WorldImageSourceOpening? opened = null;

        producers.RegisterPackages(
            adapt: opening => {
                opened = opening;

                return new OpenedSource(opening: opening);
            },
            packages: packages
        );

        Assert.True(condition: RenderGraphInstanceSet.TryCreate(
            instances: WorldSourceInstances.Of(shown: [Pattern(width: 32)]).Instances,
            refusal: out _,
            set: out var set
        ));
        Assert.True(condition: RenderGraphRuntime.TryCreate(
            deviceContext: Gpu,
            graphs: [null],
            hostsOnDirectX: false,
            packages: packages,
            refusal: out var refusal,
            root: "source$0",
            runtime: out var runtime,
            set: set
        ), userMessage: refusal?.Message);

        using (runtime) {
            Assert.Null(@object: opened!.Value.Feed);
            Assert.True(condition: disagreeing.Opened!.Disposed);
            Assert.Contains(actualString: opened.Value.Fault, comparisonType: StringComparison.Ordinal, expectedSubstring: "image producer 'testPattern' opened a feed declaring producer 'testPattern' with Deterministic over Imported, but it is registered as Deterministic over Uploaded");
            Assert.EndsWith(actualString: runtime.UnservedCaptureReason, expectedEndString: opened.Value.Fault);
        }
    }
    [Fact]
    public void TheTypedArmsProducerIdsAreClosedToDocumentProducers() {
        foreach (var id in ((string[])[WorldImageProducerSettings.MachineId, WorldImageProducerSettings.ProbeId])) {
            var exception = Assert.Throws<ArgumentException>(testCode: () => WorldImageProducerVocabulary.Register(shape: new ReservedShape(id: id)));

            Assert.Contains(actualString: exception.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: $"Image producer id '{id}' is the source package a typed '{id}' screen source's instance names");
            Assert.False(condition: WorldImageProducerVocabulary.TryGet(id: id, shape: out _));
        }
    }

    // The host's adaptation of an opening, standing in for the producer that publishes the feed: it holds the opening,
    // disposes the feed with itself, and produces nothing.
    private sealed class OpenedSource(WorldImageSourceOpening opening) : IRenderGraphExternalProducer {
        public SurfaceFormat Format => SurfaceFormat.R8G8B8A8Unorm;
        public string? NotReadyReason => (Opening.Fault ?? "the test source produces nothing");

        public WorldImageSourceOpening Opening { get; } = opening;

        public string? PendingCapturePath => null;

        public IGpuWorkSource Work { get; } = new GpuWorkLedger(
            framesInFlight: 3,
            name: "test.source"
        );

        public void Dispose() => Opening.Feed?.Dispose();
        public void OnDeviceLost() => Opening.Feed?.NotifyDeviceLost();
        public bool Produce(in FrameContext context, uint width, uint height) => false;
        public void RequestCapture(FrameCaptureRequest request) => _ = request.TryFail(error: new InvalidOperationException(message: "the test source serves no capture"));
        public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
            output = default;

            return false;
        }
    }
    // The test pattern's registration whose feed claims the imported transport.
    private sealed class Disagreeing : IWorldImageProducer {
        public ImageContentClass Content => ImageContentClass.Deterministic;
        public string Id => WorldImageProducerSettings.TestPatternId;
        public DisagreeingFeed? Opened { get; private set; }
        public ImageSourceTransport Transport => ImageSourceTransport.Uploaded;

        public bool TryOpen(WorldScreenSource.Producer source, out IWorldImageFeed? feed, out string? fault) {
            Opened = new DisagreeingFeed();
            feed = Opened;
            fault = null;

            return true;
        }
    }
    private sealed class DisagreeingFeed : IWorldImageFeed {
        public ImageSourceDescriptor Descriptor { get; } = new(
            Cadence: ImageSourceCadence.Tick,
            Color: ImageColorEncoding.Srgb,
            Content: ImageContentClass.Deterministic,
            Format: ImagePixelFormat.B8G8R8A8Unorm,
            Height: 6U,
            Producer: WorldImageProducerSettings.TestPatternId,
            Transport: ImageSourceTransport.Imported,
            Width: 32U
        );
        public bool Disposed { get; private set; }
        public string? Fault => null;
        public System.Numerics.Vector3 Light => System.Numerics.Vector3.Zero;

        public GpuImageLease AcquireFrame() => 0;
        public void Dispose() => Disposed = true;
        public nint Handle() => 0;
        public void NotifyDeviceLost() { }
        public void Publish(ulong tick, IGpuDeviceContext deviceContext) { }
    }
    private sealed class ReservedShape(string id) : WorldImageProducerShape {
        public override ImageContentClass Content => ImageContentClass.Deterministic;
        public override string Id { get; } = id;
        public override ImageSourceTransport Transport => ImageSourceTransport.Uploaded;

        public override void Validate(WorldScreenSource.Producer source, WorldDefinition definition, string path, List<string> errors) { }
    }
}
