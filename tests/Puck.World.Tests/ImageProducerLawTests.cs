using System.Numerics;
using System.Text.Json;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Sources;
using Puck.Assets.Qr;
using Puck.Hosting;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for image producers as a world names them. The shipped producers are registered under their ids with the
/// content class and transport the source contract states. A deterministic producer states the exact image it shows,
/// and the exact verdict holds against that image and names the first pixel of one that differs. A third producer
/// registers its shape and its runtime with no change to the document model: a document naming it validates, a settings
/// member it does not declare is refused by name, and an unregistered id is refused by name. A capture of a world whose
/// screen shows a desktop capture shows the declared fill and never acquires the desktop's pixels.
/// </summary>
public sealed class ImageProducerLawTests {
    private const string ThirdId = "lawThird";

    private static readonly FakeGpuDevice Gpu = new(reportVersion: 1);
    // The third producer's shape registers once per process: the vocabulary is process-wide, as the document's other
    // vocabularies are, and refuses a second registration under the same id.
    private static readonly Lazy<bool> ThirdRegistered = new(valueFactory: static () => {
        WorldImageProducerVocabulary.Register(shape: new ThirdShape());

        return true;
    });

    private static WorldDefinition WithSource(WorldScreenSource source) {
        var definition = Fixtures.BuildDocument();

        return definition with {
            ScreensRaw = [definition.Screens[0] with { Source = source }],
        };
    }
    private static WorldScreenSource.Producer Desktop() => WorldImageProducerSettings.SourceOf(
        id: WorldImageProducerSettings.CaptureId,
        settings: new WorldCaptureSettings(
            Profile: WorldFeedProfile.Default,
            WindowTitle: "Desktop"
        )
    );

    [Fact]
    public void TheShippedProducersAreRegisteredWithTheirContentClassAndTransport() {
        (string Id, ImageContentClass Content, ImageSourceTransport Transport)[] expected = [
            (WorldImageProducerSettings.TestPatternId, ImageContentClass.Deterministic, ImageSourceTransport.Uploaded),
            (WorldImageProducerSettings.QrId, ImageContentClass.Deterministic, ImageSourceTransport.Uploaded),
            (WorldImageProducerSettings.CameraId, ImageContentClass.External, ImageSourceTransport.Imported),
            (WorldImageProducerSettings.CaptureId, ImageContentClass.External, ImageSourceTransport.Imported),
        ];

        foreach (var (id, content, transport) in expected) {
            Assert.True(condition: WorldImageProducerVocabulary.TryGet(
                id: id,
                shape: out var shape
            ));
            Assert.Equal(expected: (content, transport), actual: (shape.Content, shape.Transport));
        }

        var runtime = new WorldImageProducers();

        runtime.Register(producer: new WorldTestPatternProducer());
        runtime.Register(producer: new WorldQrProducer());

        Assert.Equal(
            expected: [WorldImageProducerSettings.TestPatternId, WorldImageProducerSettings.QrId],
            actual: runtime.Producers.Select(selector: static producer => producer.Id)
        );
    }
    [Fact]
    public void ATestPatternFeedStatesTheExactPatternItShowsAndTheVerdictHoldsIt() {
        var producers = new WorldImageProducers();

        producers.Register(producer: new WorldTestPatternProducer());

        Assert.True(condition: producers.TryOpen(
            fault: out var fault,
            feed: out var feed,
            screenIndex: 0,
            source: WorldImageProducerSettings.SourceOf(
                id: WorldImageProducerSettings.TestPatternId,
                settings: new WorldTestPatternSettings(
                    Height: 6,
                    Width: 128
                )
            )
        ), userMessage: fault);

        using var opened = feed!;
        var reference = Assert.IsAssignableFrom<IImageSourceReference>(@object: opened);
        var rgba = new byte[((128 * 6) * 4)];

        Assert.False(condition: reference.TryWriteReference(rgba: rgba, stamp: out _));

        opened.Publish(
            deviceContext: Gpu,
            gpu: Gpu,
            tick: 640UL
        );

        Assert.True(condition: reference.TryWriteReference(rgba: rgba, stamp: out var stamp));
        Assert.Equal(expected: new ImageSourceStamp(Sequence: 1UL, Tick: 640UL), actual: stamp);
        Assert.NotEqual(expected: 0, actual: ((int)opened.Handle()));

        var bgra = new byte[rgba.Length];

        WorldTestPatternProducer.Render(
            bgra: bgra,
            height: 6,
            tick: 640UL,
            width: 128
        );

        for (var offset = 0; (offset < rgba.Length); offset += 4) {
            Assert.Equal(expected: (bgra[(offset + 2)], bgra[(offset + 1)], bgra[offset], bgra[(offset + 3)]), actual: (rgba[offset], rgba[(offset + 1)], rgba[(offset + 2)], rgba[(offset + 3)]));
        }

        Assert.True(condition: ImageSourceVerdict.Compare(actual: rgba, descriptor: opened.Descriptor, expected: rgba).Holds);

        var readBack = rgba.ToArray();

        readBack[(((2 * 128) + 5) * 4)] ^= 0x01;

        var verdict = ImageSourceVerdict.Compare(
            actual: readBack,
            descriptor: opened.Descriptor,
            expected: rgba
        );

        Assert.Equal(expected: (1L, 5, 2), actual: (verdict.Mismatches, verdict.FirstX, verdict.FirstY));
    }
    [Fact]
    public void AQrFeedStatesTheCodeItRasterized() {
        Assert.True(condition: WorldQrFeed.TryBuild(
            ecLevel: "Q",
            fault: out var fault,
            feed: out var feed,
            payload: "puck",
            quietZoneModules: 4
        ), userMessage: fault);

        using var qr = feed!;

        Assert.True(condition: QrEncoder.TryEncode(
            error: out _,
            level: qr.Level,
            matrix: out var matrix,
            payload: "puck"
        ));

        var modulePixels = ((int)(qr.Descriptor.Width / ((uint)(matrix!.Size + 8))));
        var bgra = matrix.RenderPixels(
            height: out _,
            modulePixels: modulePixels,
            quietZoneModules: 4,
            width: out _
        );
        var rgba = new byte[bgra.Length];

        Assert.True(condition: qr.TryWriteReference(rgba: rgba, stamp: out _));

        for (var offset = 0; (offset < rgba.Length); offset += 4) {
            Assert.Equal(expected: bgra[(offset + 2)], actual: rgba[offset]);
            Assert.Equal(expected: bgra[offset], actual: rgba[(offset + 2)]);
        }

        Assert.Equal(expected: ImageContentClass.Deterministic, actual: qr.Descriptor.Content);
        Assert.Equal(expected: ImageSourceCadence.Static, actual: qr.Descriptor.Cadence);
    }
    [Fact]
    public void AThirdProducerRegistersWithNoChangeToTheDocumentModel() {
        _ = ThirdRegistered.Value;

        var source = new WorldScreenSource.Producer(
            Id: ThirdId,
            Settings: new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal) { ["level"] = JsonSerializer.SerializeToElement(value: 3) }
        );

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: WithSource(source: source),
                reason: out var reason
            ),
            userMessage: reason
        );

        // The document round-trips the source through the world serializer unchanged.
        var screen = WithSource(source: source).Screens[0];
        var roundTripped = JsonSerializer.Deserialize(
            jsonTypeInfo: WorldJsonContext.Default.WorldScreen,
            utf8Json: JsonSerializer.SerializeToUtf8Bytes(
                jsonTypeInfo: WorldJsonContext.Default.WorldScreen,
                value: screen
            )
        )!;

        Assert.Equal(expected: source, actual: roundTripped.Source);

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: WithSource(source: new WorldScreenSource.Producer(
                Id: ThirdId,
                Settings: new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal) { ["levle"] = JsonSerializer.SerializeToElement(value: 3) }
            )),
            reason: out var unknownMember
        ));
        Assert.Contains(actualString: unknownMember, comparisonType: StringComparison.Ordinal, expectedSubstring: "levle");
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: WithSource(source: new WorldScreenSource.Producer(Id: "neverRegistered")),
            reason: out var unknownId
        ));
        Assert.Contains(actualString: unknownId, comparisonType: StringComparison.Ordinal, expectedSubstring: "'neverRegistered' names no registered image producer");

        var producers = new WorldImageProducers();

        producers.Register(producer: new FakeProducer(
            content: ImageContentClass.Presentation,
            id: ThirdId,
            transport: ImageSourceTransport.Uploaded
        ));

        Assert.True(condition: producers.TryOpen(
            fault: out _,
            feed: out var feed,
            screenIndex: 0,
            source: source
        ));
        Assert.Equal(expected: ThirdId, actual: feed!.Descriptor.Producer);
        _ = Assert.Throws<ArgumentException>(testCode: () => producers.Register(producer: new FakeProducer(
            content: ImageContentClass.External,
            id: "neverRegistered",
            transport: ImageSourceTransport.Uploaded
        )));
    }
    [Fact]
    public void ACaptureOfADesktopCaptureSourceShowsTheFillAndNeverTheDesktopPixels() {
        var source = Desktop();

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: WithSource(source: source),
                reason: out var reason
            ),
            userMessage: reason
        );

        var producers = new WorldImageProducers();
        var desktop = new FakeProducer(
            content: ImageContentClass.External,
            id: WorldImageProducerSettings.CaptureId,
            transport: ImageSourceTransport.Imported
        );

        producers.Register(producer: desktop);

        Assert.True(condition: producers.TryOpen(
            fault: out _,
            feed: out var feed,
            screenIndex: 0,
            source: source
        ));

        var armed = false;
        var gate = new WorldCaptureGate(
            alwaysFills: false,
            captureArmed: () => armed
        );
        var filled = new List<uint>();

        GpuImageLease Fill(uint rgba) {
            filled.Add(item: rgba);

            return ((nint)0xF111);
        }

        gate.BeginFrame();
        Assert.Equal(expected: FakeFeed.DesktopHandle, actual: gate.Resolve(feed: feed!, fill: Fill).ImageViewHandle);
        Assert.Equal(expected: 1, actual: desktop.Feed!.Acquisitions);

        armed = true;
        gate.BeginFrame();
        Assert.Equal(expected: ((nint)0xF111), actual: gate.Resolve(feed: feed!, fill: Fill).ImageViewHandle);
        Assert.Equal(actual: filled, expected: [ImageSourceDescriptor.DefaultCaptureFill]);

        // The capture is served: the gate keeps filling for its hold, then shows the desktop again.
        armed = false;

        for (var frame = 0; (frame < WorldCaptureGate.HoldFrames); frame++) {
            gate.BeginFrame();
            Assert.Equal(expected: ((nint)0xF111), actual: gate.Resolve(feed: feed!, fill: Fill).ImageViewHandle);
        }

        Assert.Equal(expected: 1, actual: desktop.Feed.Acquisitions);

        gate.BeginFrame();
        Assert.Equal(expected: FakeFeed.DesktopHandle, actual: gate.Resolve(feed: feed!, fill: Fill).ImageViewHandle);

        // An offscreen host fills every frame, and deterministic content is never filled.
        var offscreen = new WorldCaptureGate(
            alwaysFills: true,
            captureArmed: static () => false
        );

        Assert.Equal(expected: ((nint)0xF111), actual: offscreen.Resolve(feed: feed!, fill: Fill).ImageViewHandle);
        Assert.False(condition: offscreen.Fills(content: ImageContentClass.Deterministic));
        Assert.False(condition: offscreen.Fills(content: ImageContentClass.Presentation));
        Assert.Equal(expected: 2, actual: desktop.Feed.Acquisitions);
    }

    // A producer standing in for a real one: every feed it opens answers one fixed handle and counts acquisitions.
    private sealed class FakeProducer(string id, ImageContentClass content, ImageSourceTransport transport) : IWorldImageProducer {
        public ImageContentClass Content { get; } = content;

        public FakeFeed? Feed { get; private set; }

        public string Id { get; } = id;
        public ImageSourceTransport Transport { get; } = transport;

        public bool TryOpen(WorldScreenSource.Producer source, int screenIndex, out IWorldImageFeed? feed, out string? fault) {
            Feed = new FakeFeed(descriptor: new ImageSourceDescriptor(
                Cadence: ImageSourceCadence.Rate(rateHz: 30U),
                Color: ImageColorEncoding.Srgb,
                Content: Content,
                Format: ImagePixelFormat.B8G8R8A8Unorm,
                Height: 1U,
                Producer: Id,
                Transport: Transport,
                Width: 1U
            ));
            feed = Feed;
            fault = null;

            return true;
        }
    }
    private sealed class FakeFeed(ImageSourceDescriptor descriptor) : IWorldImageFeed {
        public static readonly nint DesktopHandle = 0xDE5C;

        public int Acquisitions { get; private set; }
        public ImageSourceDescriptor Descriptor { get; } = descriptor;
        public string? Fault => null;
        public Vector3 Light => Vector3.One;

        public GpuImageLease AcquireFrame() {
            Acquisitions++;

            return DesktopHandle;
        }
        public void Dispose() { }
        public nint Handle() => DesktopHandle;
        public void NotifyDeviceLost() { }
        public void Publish(ulong tick, IGpuDeviceContext deviceContext, IGpuComputeServices gpu) { }
    }
    // The third producer's shape reads its settings without the world serializer's shipped shapes: it names the one
    // member it declares and refuses any other by name.
    private sealed class ThirdShape : WorldImageProducerShape {
        public override ImageContentClass Content => ImageContentClass.Presentation;
        public override string Id => ThirdId;
        public override ImageSourceTransport Transport => ImageSourceTransport.Uploaded;

        public override void Validate(WorldScreenSource.Producer source, WorldDefinition definition, string path, List<string> errors) {
            foreach (var (name, value) in (source.Settings ?? new Dictionary<string, JsonElement>())) {
                if (name != "level") {
                    errors.Add(item: $"{path}.settings: '{name}' is not a member of producer '{ThirdId}'.");
                } else if (value.ValueKind != JsonValueKind.Number) {
                    errors.Add(item: $"{path}.settings.level must be a number.");
                }
            }
        }
    }
}
