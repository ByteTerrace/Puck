using Puck.Abstractions.Sources;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for producer registration and the deterministic verdict. A registry answers each id with exactly one producer and
/// refuses a malformed id, an undefined class or transport, and a second producer under a taken id. The verdict holds an
/// exact read-back and names the first differing pixel of one that is not, and refuses to judge content that is not
/// deterministic.
/// </summary>
public sealed class ImageSourceProducerLawTests {
    private sealed record Producer(string Id, ImageContentClass Content, ImageSourceTransport Transport) : IImageSourceProducer;

    private static ImageSourceDescriptor Descriptor(ImageContentClass content) => new(
        Cadence: ImageSourceCadence.Tick,
        Color: ImageColorEncoding.Srgb,
        Content: content,
        Format: ImagePixelFormat.R8G8B8A8Unorm,
        Height: 2U,
        Producer: "fake",
        Transport: ImageSourceTransport.Uploaded,
        Width: 2U
    );

    [Fact]
    public void ARegistryAnswersEachIdWithTheOneProducerRegisteredUnderIt() {
        var registry = new ImageSourceProducerRegistry<Producer>();
        var emulator = new Producer(Content: ImageContentClass.Deterministic, Id: "machine", Transport: ImageSourceTransport.Uploaded);
        var desktop = new Producer(Content: ImageContentClass.External, Id: "capture", Transport: ImageSourceTransport.Imported);

        registry.Register(producer: emulator);
        registry.Register(producer: desktop);

        Assert.True(condition: registry.TryGet(id: "capture", producer: out var found));
        Assert.Same(actual: found, expected: desktop);
        Assert.False(condition: registry.TryGet(id: "Capture", producer: out _));
        Assert.Equal(expected: [emulator, desktop], actual: registry.Producers);

        var duplicate = Assert.Throws<ArgumentException>(testCode: () => registry.Register(producer: new Producer(Content: ImageContentClass.External, Id: "capture", Transport: ImageSourceTransport.Uploaded)));

        Assert.Contains(expectedSubstring: "'capture'", actualString: duplicate.Message);
        Assert.Same(expected: desktop, actual: (registry.TryGet(id: "capture", producer: out var kept) ? kept : null));
    }
    [InlineData("")]
    [InlineData("Camera")]
    [InlineData("9lives")]
    [InlineData("test-pattern")]
    [InlineData("a b")]
    [Theory]
    public void ARegistryRefusesAProducerWhoseIdIsNotAnId(string id) {
        var registry = new ImageSourceProducerRegistry<Producer>();

        _ = Assert.Throws<ArgumentException>(testCode: () => registry.Register(producer: new Producer(Content: ImageContentClass.Presentation, Id: id, Transport: ImageSourceTransport.Rendered)));
        Assert.Empty(collection: registry.Producers);
    }
    [Fact]
    public void ARegistryRefusesAnUndefinedClassOrTransport() {
        var registry = new ImageSourceProducerRegistry<Producer>();

        _ = Assert.Throws<ArgumentException>(testCode: () => registry.Register(producer: new Producer(Content: 0, Id: "zero", Transport: ImageSourceTransport.Uploaded)));
        _ = Assert.Throws<ArgumentException>(testCode: () => registry.Register(producer: new Producer(Content: ImageContentClass.External, Id: "zero", Transport: 0)));
    }
    [Fact]
    public void AnExactReadBackHoldsAndAnyDifferenceIsNamedByItsFirstPixel() {
        var descriptor = Descriptor(content: ImageContentClass.Deterministic);
        var expected = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

        Assert.True(condition: ImageSourceVerdict.Compare(actual: expected, descriptor: descriptor, expected: expected).Holds);

        var actual = expected.ToArray();

        actual[9] = 0;
        actual[13] = 0;

        var verdict = ImageSourceVerdict.Compare(
            actual: actual,
            descriptor: descriptor,
            expected: expected
        );

        Assert.False(condition: verdict.Holds);
        Assert.Equal(expected: 2L, actual: verdict.Mismatches);
        Assert.Equal(expected: (0, 1), actual: (verdict.FirstX, verdict.FirstY));
        Assert.Equal(expected: 0x0C0B0A09U, actual: verdict.Expected);
        Assert.Equal(expected: 0x0C0B0009U, actual: verdict.Actual);
        Assert.Equal(expected: "fake 2x2 2 pixel(s) differ; first at (0, 1) expected #0C0B0A09 read #0C0B0009", actual: verdict.ToString());
    }
    [InlineData(ImageContentClass.External)]
    [InlineData(ImageContentClass.Presentation)]
    [Theory]
    public void OnlyADeterministicSourceIsJudgedExactly(ImageContentClass content) {
        var pixels = new byte[16];

        _ = Assert.Throws<ArgumentException>(testCode: () => ImageSourceVerdict.Compare(actual: pixels, descriptor: Descriptor(content: content), expected: pixels));
    }
    [Fact]
    public void OnlyExternalContentFillsCaptures() {
        Assert.True(condition: Descriptor(content: ImageContentClass.External).FillsCaptures);
        Assert.False(condition: Descriptor(content: ImageContentClass.Deterministic).FillsCaptures);
        Assert.False(condition: Descriptor(content: ImageContentClass.Presentation).FillsCaptures);
    }
}
