using System.Text;
using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

public sealed class CartridgeContentProviderTests {
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void SuccessfulPreparationStampsFormatAndKeepsSourceDistinctFromImage(string target) {
        var document = CartridgeDocuments.Create(target: target, title: "PROVENANCE");
        var source = CartridgeDocuments.Canonicalize(document: document).Bytes;
        ICartridgeCompiler compiler = target == "agb"
            ? new AgbCartridgeCompiler()
            : new HgbCartridgeCompiler();

        var prepared = compiler.Prepare(content: source);
        var asset = new PreparedMachineAsset(
            Path: "provenance.cartridge.json",
            Image: prepared.Image,
            ContentHash: prepared.SourceHash,
            SourceBytes: source,
            PreparedContent: prepared);

        Assert.Equal(CartridgeDocument.SchemaId, prepared.VerifiedSourceFormat);
        Assert.Equal(source, asset.SourceBytes.ToArray());
        Assert.False(source.AsSpan().SequenceEqual(prepared.Image));
    }

    [Fact]
    public void InvalidParseAndCompileAreRefusedBeforeAProvenanceCarrierIsProduced() {
        ICartridgeCompiler compiler = new HgbCartridgeCompiler();

        var malformed = Assert.Throws<MachineContentException>(
            () => compiler.Prepare(content: Encoding.UTF8.GetBytes("{")));
        Assert.IsType<JsonException>(malformed.InnerException);

        Assert.Throws<MachineContentException>(() => compiler.Prepare(content: Encoding.UTF8.GetBytes("{}")));

        var valid = CartridgeDocuments.Canonicalize(
            document: CartridgeDocuments.Create(target: "cgb", title: "VALID")).Bytes;
        var prepared = compiler.Prepare(content: valid);
        Assert.Equal(CartridgeDocument.SchemaId, prepared.VerifiedSourceFormat);

        var wrongTarget = CartridgeDocuments.Canonicalize(
            document: CartridgeDocuments.Create(target: "agb", title: "WRONGTARGET")).Bytes;
        var wrongTargetRefusal = Assert.Throws<MachineContentException>(() => compiler.Prepare(content: wrongTarget));
        Assert.Contains("requires target cgb", wrongTargetRefusal.Message, StringComparison.Ordinal);
    }
}
