using Puck.Abstractions.Machines;

namespace Puck.Abstractions.Tests;

public sealed class MachineContentCarrierTests {
    [Fact]
    public void AssetRetainsExactSourceSeparatelyFromExecutableImage() {
        byte[] source = [1, 2, 3];
        byte[] image = [9, 8];

        var asset = new PreparedMachineAsset(
            Path: "game.cartridge.json",
            Image: image,
            ContentHash: "source-hash",
            SourceBytes: source);

        Assert.Equal(source, asset.SourceBytes.ToArray());
        Assert.Equal(image, asset.Image.ToArray());
        Assert.False(asset.SourceBytes.Span.SequenceEqual(asset.Image.Span));
    }

    [Fact]
    public void UnassertedPreparedContentHasNoFormatProvenance() {
        var content = new PreparedMachineContent(Image: [1], SourceHash: "hash", Symbols: new Dictionary<string, MachineContentSymbol>());

        Assert.Null(content.VerifiedSourceFormat);
    }
}
