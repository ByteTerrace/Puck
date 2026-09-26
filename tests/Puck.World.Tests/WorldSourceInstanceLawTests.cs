using System.Text.Json;
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
/// instance of <c>source.&lt;producer id&gt;</c> that carries its settings and is named by its content, so screens showing
/// equal sources read one instance however its settings are spelled, removing or reordering screens keeps every other
/// source's name and producer, and no name is ever given to other content; the instance's handle is its identity. Each
/// registered producer registers one factory under its source package, an upload for an uploaded producer and an external
/// producer for any other, which opens the instance's feed from the instance's settings and refuses by name a feed that
/// disagrees with its registration. The typed arms' producer
/// ids are closed to document producers.
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
    // Each instance's upload in a runtime, by instance name.
    private static Dictionary<string, IRenderGraphSourceUpload> ProducersByName(RenderGraphRuntime runtime, WorldSourceInstances sources) => sources.Instances.Select(selector: (instance, index) => (instance.Name, Producer: runtime.Source(instance: index)!)).ToDictionary(
        comparer: StringComparer.Ordinal,
        elementSelector: static entry => entry.Producer,
        keySelector: static entry => entry.Name
    );
    private static void Reconfigure(RenderGraphRuntime runtime, WorldSourceInstances sources) {
        Assert.True(condition: RenderGraphInstanceSet.TryCreate(
            instances: sources.Instances,
            refusal: out var setRefusal,
            set: out var set
        ), userMessage: setRefusal?.Message);
        Assert.True(condition: runtime.TryReconfigure(
            graphs: new RenderGraphRuntimeGraph?[set.Instances.Count],
            refusal: out var refusal,
            root: set.Instances[0].Name,
            set: set
        ), userMessage: refusal?.Message);
    }
    // A runtime over the test-pattern producer's source instances, and every feed its factory opened.
    private static (RenderGraphRuntime Runtime, List<IWorldImageFeed> Openings) Run(WorldSourceInstances sources) {
        var producers = new WorldImageProducers();
        var packages = new RenderGraphPackageRecorders();
        var pattern = new Counting(inner: new WorldTestPatternProducer());

        producers.Register(producer: pattern);
        SourceConversionPackage.RegisterAll(packages: packages);
        producers.RegisterPackages(
            adapt: static opening => new OpenedSource(opening: opening),
            packages: packages
        );
        Assert.True(condition: RenderGraphInstanceSet.TryCreate(
            instances: sources.Instances,
            refusal: out var setRefusal,
            set: out var set
        ), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(
            pipelines: new GpuPassPipelineCache(),
            deviceContext: Gpu,
            graphs: new RenderGraphRuntimeGraph?[set.Instances.Count],
            hostsOnDirectX: false,
            packages: packages,
            refusal: out var refusal,
            root: set.Instances[0].Name,
            runtime: out var runtime,
            set: set
        ), userMessage: refusal?.Message);

        return (runtime, pattern.Opened);
    }
    // A test-pattern source whose settings are spelled as the given JSON object, members in its order.
    private static WorldScreenSource.Producer Spelled(string json) {
        using var document = JsonDocument.Parse(json: json);

        var settings = new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal);

        foreach (var member in document.RootElement.EnumerateObject()) {
            settings.Add(
                key: member.Name,
                value: member.Value.Clone()
            );
        }

        return new WorldScreenSource.Producer(
            Id: WorldImageProducerSettings.TestPatternId,
            Settings: settings
        );
    }

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

        var names = sources.Instances.Select(selector: static instance => instance.Name).ToArray();

        Assert.Equal(
            actual: sources.Instances.Select(selector: static instance => instance.ExternalPackage),
            expected: ["source.testPattern", "source.machine", "source.testPattern", "source.probe"]
        );
        Assert.Equal(
            actual: Enumerable.Range(count: 10, start: 0).Select(selector: sources.InstanceOf),
            expected: new string?[] { names[0], null, null, names[1], names[0], names[2], names[3], names[1], null, null }
        );
        Assert.Equal(expected: SourceHandle.Producer(name: names[0]), actual: sources.HandleOf(screen: 4));
        Assert.All(
            action: static instance => {
                Assert.True(condition: instance.IsSource);
                Assert.Equal(expected: WorldViewNames.Source(producer: instance.SourceProducer!, settings: instance.Settings), actual: instance.Name);
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
        producers.Register(producer: new Imported());

        var packages = new RenderGraphPackageRecorders();
        var openings = new List<WorldImageSourceOpening>();

        SourceConversionPackage.RegisterAll(packages: packages);
        producers.RegisterPackages(
            adapt: opening => {
                openings.Add(item: opening);

                return new OpenedSource(opening: opening);
            },
            packages: packages
        );

        // The uploaded producers register uploads, which the runtime converts; the imported one is adapted.
        Assert.Equal(expected: ["source.qr", "source.testPattern"], actual: packages.SourceIds);
        Assert.Equal(expected: ["source.capture"], actual: packages.ProducerIds);

        var sources = WorldSourceInstances.Of(shown: [Pattern(width: 96), Pattern(width: 96)]);

        Assert.True(condition: RenderGraphInstanceSet.TryCreate(
            instances: sources.Instances,
            refusal: out var setRefusal,
            set: out var set
        ), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(
            pipelines: new GpuPassPipelineCache(),
            deviceContext: Gpu,
            graphs: [null],
            hostsOnDirectX: false,
            packages: packages,
            refusal: out var refusal,
            root: set.Instances[0].Name,
            runtime: out var runtime,
            set: set
        ), userMessage: refusal?.Message);

        using (runtime) {
            // One factory call for the one instance both screens read, opened from its settings.
            var opening = Assert.IsType<WorldImageSourceUpload>(@object: runtime.Source(instance: 0)).Opening;

            Assert.Empty(collection: openings);
            Assert.Equal(expected: (set.Instances[0].Name, "source.testPattern"), actual: (opening.Context.Instance, opening.Context.Package));
            Assert.Null(@object: opening.Fault);
            Assert.Equal(
                expected: (WorldImageProducerSettings.TestPatternId, 96U, 6U),
                actual: (opening.Feed!.Descriptor.Producer, opening.Feed.Descriptor.Width, opening.Feed.Descriptor.Height)
            );
            Assert.Null(@object: runtime.Producer(instance: 0));
        }
    }
    [Fact]
    public void AnInstanceWhoseFeedDisagreesWithItsRegistrationIsRefusedByName() {
        var producers = new WorldImageProducers();
        var disagreeing = new Disagreeing();

        producers.Register(producer: disagreeing);

        var packages = new RenderGraphPackageRecorders();

        SourceConversionPackage.RegisterAll(packages: packages);
        producers.RegisterPackages(
            adapt: static opening => new OpenedSource(opening: opening),
            packages: packages
        );

        Assert.True(condition: RenderGraphInstanceSet.TryCreate(
            instances: WorldSourceInstances.Of(shown: [Pattern(width: 32)]).Instances,
            refusal: out _,
            set: out var set
        ));
        Assert.True(condition: RenderGraphRuntime.TryCreate(
            pipelines: new GpuPassPipelineCache(),
            deviceContext: Gpu,
            graphs: [null],
            hostsOnDirectX: false,
            packages: packages,
            refusal: out var refusal,
            root: set.Instances[0].Name,
            runtime: out var runtime,
            set: set
        ), userMessage: refusal?.Message);

        using (runtime) {
            var opened = Assert.IsType<WorldImageSourceUpload>(@object: runtime.Source(instance: 0)).Opening;

            Assert.Null(@object: opened.Feed);
            Assert.True(condition: disagreeing.Opened!.Disposed);
            Assert.Contains(actualString: opened.Fault, comparisonType: StringComparison.Ordinal, expectedSubstring: "image producer 'testPattern' opened a feed declaring producer 'testPattern' with Deterministic over Imported, but it is registered as Deterministic over Uploaded");
            Assert.EndsWith(actualString: runtime.UnservedCaptureReason, expectedEndString: opened.Fault);
        }
    }
    [Fact]
    public void RemovingTheFirstScreenKeepsTheOtherSourcesNameAndProducer() {
        var a = Pattern(width: 128);
        var b = Pattern(width: 64);
        var before = WorldSourceInstances.Of(shown: [a, b]);

        var (runtime, openings) = Run(sources: before);

        using (runtime) {
            var kept = ProducersByName(runtime: runtime, sources: before)[before.InstanceOf(screen: 1)!];
            var after = WorldSourceInstances.Of(shown: [b]);

            Reconfigure(runtime: runtime, sources: after);

            Assert.Equal(expected: before.InstanceOf(screen: 1), actual: after.InstanceOf(screen: 0));
            Assert.Same(expected: kept, actual: ProducersByName(runtime: runtime, sources: after)[after.InstanceOf(screen: 0)!]);
            Assert.Equal(expected: 2, actual: openings.Count);
        }
    }
    [Fact]
    public void ReorderingScreensKeepsEverySourcesNameAndProducer() {
        var a = Pattern(width: 128);
        var b = Pattern(width: 64);
        var before = WorldSourceInstances.Of(shown: [a, b]);

        var (runtime, openings) = Run(sources: before);

        using (runtime) {
            var producers = ProducersByName(runtime: runtime, sources: before);
            var after = WorldSourceInstances.Of(shown: [b, a]);

            Reconfigure(runtime: runtime, sources: after);

            Assert.Equal(expected: (before.InstanceOf(screen: 0), before.InstanceOf(screen: 1)), actual: (after.InstanceOf(screen: 1), after.InstanceOf(screen: 0)));
            var reordered = ProducersByName(runtime: runtime, sources: after);

            Assert.Equal(expected: producers.Count, actual: reordered.Count);
            Assert.All(
                action: entry => Assert.Same(expected: entry.Value, actual: reordered[entry.Key]),
                collection: producers
            );
            Assert.Equal(expected: 2, actual: openings.Count);
        }
    }
    [Fact]
    public void NoNameIsEverGivenToOtherContent() {
        WorldScreenSource[] catalog = [
            Pattern(width: 128),
            Pattern(width: 64),
            Spelled(json: """{ "width": 64, "height": 7 }"""),
            new WorldScreenSource.Machine(Instance: "cabinet", Output: "lcd"),
            new WorldScreenSource.Machine(Instance: "cabinet", Output: "top"),
            new WorldScreenSource.Machine(Instance: "lcd", Output: "cabinet"),
            new WorldScreenSource.Probe(Id: "relight"),
            new WorldScreenSource.Probe(Id: "shadow"),
            WorldImageProducerSettings.SourceOf(id: WorldImageProducerSettings.QrId, settings: new WorldQrSettings(Payload: "relight")),
        ];
        var contentOf = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var nameOf = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        // Every rotation of the catalog, forwards and backwards, with each screen in turn removed.
        for (var rotation = 0; (rotation < catalog.Length); rotation++) {
            WorldScreenSource[] rotated = [.. catalog[rotation..], .. catalog[..rotation]];

            foreach (var order in ((WorldScreenSource[][])[rotated, [.. rotated.Reverse()]])) {
                for (var removed = -1; (removed < order.Length); removed++) {
                    var sources = WorldSourceInstances.Of(shown: [.. order.Where(predicate: (_, index) => (index != removed))]);

                    foreach (var instance in sources.Instances) {
                        var content = $"{instance.SourceProducer} {ImageSourceSettings.Canonical(settings: instance.Settings)}";

                        Assert.Equal(expected: content, actual: contentOf.GetValueOrDefault(key: instance.Name, defaultValue: content));
                        Assert.Equal(expected: instance.Name, actual: nameOf.GetValueOrDefault(key: content, defaultValue: instance.Name));
                        contentOf[instance.Name] = content;
                        nameOf[content] = instance.Name;
                    }
                }
            }
        }

        Assert.Equal(expected: catalog.Length, actual: contentOf.Count);
    }
    [Fact]
    public void EqualSettingsInAnotherMemberOrderOrNumberSpellingReadOneInstance() {
        var sources = WorldSourceInstances.Of(shown: [
            Spelled(json: """{ "width": 96, "height": 6 }"""),
            Spelled(json: """{ "height": 6.0, "width": 9.6e1 }"""),
            Spelled(json: """{ "width": 960e-1, "height": 0.6E+1 }"""),
            Spelled(json: """{ "width": 97, "height": 6 }"""),
        ]);

        var name = sources.InstanceOf(screen: 0);

        Assert.Equal(expected: 2, actual: sources.Instances.Count);
        Assert.Equal(expected: new string?[] { name, name, name }, actual: Enumerable.Range(count: 3, start: 0).Select(selector: sources.InstanceOf));
        Assert.NotEqual(expected: name, actual: sources.InstanceOf(screen: 3));
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
        public void OnDeviceLost() { }
        public bool Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) => false;
        public void RequestCapture(FrameCaptureRequest request) => _ = request.TryFail(error: new InvalidOperationException(message: "the test source serves no capture"));
        public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
            output = default;

            return false;
        }
    }
    // The test pattern's producer, counting every feed it opens.
    private sealed class Counting(IWorldImageProducer inner) : IWorldImageProducer {
        public ImageContentClass Content => inner.Content;
        public string Id => inner.Id;
        public List<IWorldImageFeed> Opened { get; } = [];
        public ImageSourceTransport Transport => inner.Transport;

        public bool TryOpen(WorldScreenSource.Producer source, out IWorldImageFeed? feed, out string? fault) {
            var opened = inner.TryOpen(
                fault: out fault,
                feed: out feed,
                source: source
            );

            if (feed is not null) {
                Opened.Add(item: feed);
            }

            return opened;
        }
    }
    // An imported producer under the capture shape, which a host adapts to an external producer.
    private sealed class Imported : IWorldImageProducer {
        public ImageContentClass Content => ImageContentClass.External;
        public string Id => WorldImageProducerSettings.CaptureId;
        public ImageSourceTransport Transport => ImageSourceTransport.Imported;

        public bool TryOpen(WorldScreenSource.Producer source, out IWorldImageFeed? feed, out string? fault) {
            feed = null;
            fault = "the imported test producer opens nothing";

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

        public void Dispose() => Disposed = true;
    }
    private sealed class ReservedShape(string id) : WorldImageProducerShape {
        public override ImageContentClass Content => ImageContentClass.Deterministic;
        public override string Id { get; } = id;
        public override ImageSourceTransport Transport => ImageSourceTransport.Uploaded;

        public override void Validate(WorldScreenSource.Producer source, WorldDefinition definition, string path, List<string> errors) { }
    }
}
