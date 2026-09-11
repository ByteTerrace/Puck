using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers images larger than one bank: the switchable window pages correctly and the fixed window survives it.</summary>
public sealed class CartridgeBankingTests {
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void AnImageBeyondOneBankStillBootsAndRenders(string target) {
        // A full tile bank pushes the video payload past a single switchable window on the humble machine.
        var tiles = new CartridgeTile[256];
        tiles[0] = new CartridgeTile(Name: "blank", Pixels: [.. Enumerable.Repeat(element: "00000000", count: 8)]);
        for (var index = 1; index < tiles.Length; ++index) {
            tiles[index] = new CartridgeTile(Name: $"t{index}", Pixels: [.. Enumerable.Repeat(element: "11111111", count: 8)]);
        }

        var cells = new int[1024];
        for (var index = 0; index < cells.Length; ++index) {
            cells[index] = 1;
        }

        var document = CartridgeDocuments.Create(target: target, title: "BANKED") with {
            Tiles = tiles,
            Map = cells,
            Variables = [new CartridgeVariable(Name: "beat", Initial: 0)],
            Arrays = [new CartridgeArray(Name: "seed", Initial: [3, 1, 4])],
            // Music and array seeds live in the fixed window; a stranded bank would leave both unreadable.
            Sounds = [new CartridgeSound(Name: "theme", Music: [CartridgeCostMeasurement.Lead(part: CartridgeCostMeasurement.Track())])],
            Rules = [new CartridgeRule(Name: "run", When: [], Body: [
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "beat"), Operation: "set", Value: new CartridgeValue(Array: "seed", Index: new CartridgeValue(Constant: 2))),
                new CartridgeStatement(Kind: "play", Sound: "theme"),
            ])],
        };
        ICartridgeCompiler compiler = target == "agb" ? new AgbCartridgeCompiler() : new HgbCartridgeCompiler();
        var result = compiler.Compile(document: document);

        using var machine = new BankProbe(result: result);
        machine.Run(frames: 20);

        // The banked tiles reached video memory, and the fixed window's array seed and music both still work.
        Assert.NotEqual(expected: 0u, actual: machine.Pixel(x: 4, y: 4));
        Assert.Equal(expected: 4, actual: machine.Read(address: result.Variables["beat"]));
        Assert.NotEqual(expected: 0u, actual: machine.Status() & 2u);
    }

    [Fact]
    public void TheHumbleImageGrowsInWholeBanksAndTheHeaderSaysSo() {
        var small = new HgbCartridgeCompiler().Compile(document: CartridgeDocuments.Create(target: "cgb", title: "SMALL"));
        Assert.Equal(expected: 0x8000, actual: small.Rom.Length);

        // Bank count is a power of two and the header's size code is its logarithm less one.
        Assert.Equal(expected: 0x1B, actual: small.Rom[0x0147]);
        Assert.Equal(expected: 0x00, actual: small.Rom[0x0148]);
    }

    private static Puck.Assets.Documents.AudioDocument Track() => new(
        Schema: Puck.Assets.Documents.AudioDocument.CurrentSchema, Name: "t", Tempo: 4,
        Patterns: [[new Puck.Assets.Documents.AudioRowDocument(Note: "C4", Duty: null, Envelope: null)]],
        Order: [0], Effects: null);

    private sealed class BankProbe : IDisposable {
        private readonly AgbVerifyMachineDriver? m_agb;
        private readonly VerifyMachineDriver? m_hgb;
        public BankProbe(CartridgeCompilation result) {
            if (result.Target == "agb") { m_agb = new AgbVerifyMachineDriver(rom: result.Rom, label: "bank"); }
            else { m_hgb = new VerifyMachineDriver(rom: result.Rom, label: "bank"); }
        }
        public void Run(int frames) {
            m_agb?.RunFrames(keys: AgbKeys.None, frames: frames);
            m_hgb?.RunFrames(buttons: JoypadButtons.None, frames: frames);
        }
        public byte Read(uint address) => m_agb?.ReadByte(address: address) ?? m_hgb!.Read(address: (ushort)address);
        public uint Pixel(int x, int y) => m_agb?.ReadPixel(x: x, y: y) ?? m_hgb!.ReadPixel(x: x, y: y);
        public uint Status() => m_agb is { } agb ? agb.ReadHalf(address: 0x04000084u) : m_hgb!.Read(address: 0xFF26);
        public void Dispose() { m_agb?.Dispose(); m_hgb?.Dispose(); }
    }
}
