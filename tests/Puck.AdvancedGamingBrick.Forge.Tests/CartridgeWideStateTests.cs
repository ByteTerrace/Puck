using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;


namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Covers a slot whose declared ceiling does not fit a byte: both machines spend two bytes on it, little-endian, and
/// its arithmetic wraps at the declared ceiling rather than at 256.
/// </summary>
public sealed class CartridgeWideStateTests {
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void AWideSlotCountsPastAByteAndReadsBackLittleEndian(string target) {
        // 300 additions of one: a byte slot would have wrapped to 44 long before the end.
        var result = Compiler(target: target).Compile(document: Base(target: target) with {
            Rules = [new CartridgeRule(Name: "count", When: CartridgeExpressions.Gate(left: CartridgeExpressions.Of(state: "score"), comparison: ActionStateComparison.Less, right: CartridgeExpressions.Of(constant: 300)), Body: [
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "score"), Operation: ExpressionOp.Add, Value: CartridgeExpressions.Of(constant: 7)),
            ])],
        });

        using var machine = new WideProbe(result: result);
        machine.Run(frames: 60);

        var address = result.Variables["score"];
        var value = (machine.Read(address: address) | (machine.Read(address: (address + 1)) << 8));

        // The gate stops it in 0..306: the last admitted add starts below 300 and carries at most six past it.
        Assert.InRange(actual: value, low: 300, high: 306);
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void AWideSubtractBorrowsAcrossTheLowByte(string target) {
        // 0x0100 - 1 is the case a byte-at-a-time subtract gets wrong without the borrow.
        var result = Compiler(target: target).Compile(document: Base(target: target) with {
            Variables = [new CartridgeVariable(Name: "score", Initial: 256, Max: 65535), new CartridgeVariable(Name: "done", Initial: 0)],
            Rules = [new CartridgeRule(Name: "spend", When: CartridgeExpressions.Gate(left: CartridgeExpressions.Of(state: "done"), comparison: ActionStateComparison.Equal, right: CartridgeExpressions.Of(constant: 0)), Body: [
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "score"), Operation: ExpressionOp.Subtract, Value: CartridgeExpressions.Of(constant: 1)),
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "done"), Value: CartridgeExpressions.Of(constant: 1)),
            ])],
        });

        using var machine = new WideProbe(result: result);
        machine.Run(frames: 30);

        var address = result.Variables["score"];

        Assert.Equal(expected: 0xFF, actual: machine.Read(address: address));
        Assert.Equal(expected: 0x00, actual: machine.Read(address: (address + 1)));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void AWideComparisonReadsBothBytes(string target) {
        // 0x0200 against 0x00FF: the low bytes alone would answer the wrong way round.
        var result = Compiler(target: target).Compile(document: Base(target: target) with {
            Variables = [
                new CartridgeVariable(Name: "score", Initial: 512, Max: 65535),
                new CartridgeVariable(Name: "bar", Initial: 255, Max: 65535),
                new CartridgeVariable(Name: "greater", Initial: 0),
            ],
            Rules = [new CartridgeRule(Name: "rank", When: CartridgeExpressions.Gate(left: CartridgeExpressions.Of(state: "score"), comparison: ActionStateComparison.Greater, right: CartridgeExpressions.Of(state: "bar")), Body: [
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "greater"), Value: CartridgeExpressions.Of(constant: 1)),
            ])],
        });

        using var machine = new WideProbe(result: result);
        machine.Run(frames: 20);

        Assert.Equal(expected: 1, actual: machine.Read(address: result.Variables["greater"]));
    }

    [Fact]
    public void AnOperationWithNoSixteenBitFormIsRefused() {
        var document = Base(target: "cgb") with {
            Rules = [new CartridgeRule(Name: "scale", Body: [
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "score"), Operation: ExpressionOp.Multiply, Value: CartridgeExpressions.Of(constant: 2)),
            ])],
        };

        Assert.Contains(collection: CartridgeDocuments.Validate(document: document), filter: error => error.Path.EndsWith(value: ".operation", comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public void AWideSlotInAByteFieldIsRefused() {
        var document = Base(target: "cgb") with {
            Sprites = [new CartridgeSprite(
                Name: "cursor",
                Tile: CartridgeExpressions.Of(constant: 0),
                X: CartridgeExpressions.Of(state: "score"),
                Y: CartridgeExpressions.Of(constant: 0),
                Visible: CartridgeExpressions.Of(constant: 1))],
        };

        Assert.Contains(collection: CartridgeDocuments.Validate(document: document), filter: error => error.Message.Contains(value: "wide slot", comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public void TheWindowBoundsTheDeclaredSlotsByBytesRatherThanByCount() {
        var document = Base(target: "cgb") with {
            Variables = [.. Enumerable.Range(start: 0, count: (CartridgeLimits.VariableCount / 2) + 1)
                .Select(selector: index => new CartridgeVariable(Name: $"w{index}", Initial: 0, Max: CartridgeLimits.WideMaximum))],
        };

        Assert.Contains(collection: CartridgeDocuments.Validate(document: document), filter: error => error.Path == "variables");
    }

    private static ICartridgeCompiler Compiler(string target) => ((target == "agb")
        ? new AgbCartridgeCompiler()
        : new HgbCartridgeCompiler());

    private static CartridgeDocument Base(string target) =>
        CartridgeDocuments.Create(target: target, title: "WIDE") with {
            Variables = [new CartridgeVariable(Name: "score", Initial: 0, Max: 65535)],
        };

    private sealed class WideProbe : IDisposable {
        private readonly AgbVerifyMachineDriver? m_agb;
        private readonly VerifyMachineDriver? m_hgb;

        public WideProbe(CartridgeCompilation result) {
            if (result.Target == "agb") { m_agb = new AgbVerifyMachineDriver(rom: result.Rom, label: "wide"); } else { m_hgb = new VerifyMachineDriver(rom: result.Rom, label: "wide"); }
        }

        public void Run(int frames) {
            m_agb?.RunFrames(keys: AgbKeys.None, frames: frames);
            m_hgb?.RunFrames(buttons: JoypadButtons.None, frames: frames);
        }
        public byte Read(uint address) => (m_agb?.ReadByte(address: address) ?? m_hgb!.Read(address: (ushort)address));
        public void Dispose() { m_agb?.Dispose(); m_hgb?.Dispose(); }
    }
}
