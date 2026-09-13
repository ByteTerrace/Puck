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
    private static CartridgeDocument Base(string target) =>
        CartridgeDocuments.Create(
            target: target,
            title: "WIDE"
        ) with {
            Variables = [new CartridgeVariable(
                Initial: 0,
                Max: 65535,
                Name: "score"
            )],
        };
    private static ICartridgeCompiler Compiler(string target) => ((target == "agb")
        ? new AgbCartridgeCompiler()
        : new HgbCartridgeCompiler()
    );

    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void AWideComparisonReadsBothBytes(string target) {
        // 0x0200 against 0x00FF: the low bytes alone would answer the wrong way round.
        var result = Compiler(target: target).Compile(document: Base(target: target) with {
            Variables = [
                new CartridgeVariable(
                Initial: 512,
                Max: 65535,
                Name: "score"
            ),
                new CartridgeVariable(
                Initial: 255,
                Max: 65535,
                Name: "bar"
            ),
                new CartridgeVariable(
                Name: "greater",
                Initial: 0
            ),
            ],
            Rules = [new CartridgeRule(
                Name: "rank",
                When: CartridgeExpressions.Gate(
                    left: CartridgeExpressions.Of(state: "score"),
                    comparison: ActionStateComparison.Greater,
                    right: CartridgeExpressions.Of(state: "bar")
                ),
                Body: [
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "greater"),
                        Value: CartridgeExpressions.Of(constant: 1)
                    ),
            ]
            )],
        });

        using var machine = new WideProbe(result: result);

        machine.Run(frames: 20);

        Assert.Equal(
            expected: 1,
            actual: machine.Read(address: result.Variables["greater"])
        );
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void AWideSlotCountsPastAByteAndReadsBackLittleEndian(string target) {
        // 300 additions of one: a byte slot would have wrapped to 44 long before the end.
        var result = Compiler(target: target).Compile(document: Base(target: target) with {
            Rules = [new CartridgeRule(
                Name: "count",
                When: CartridgeExpressions.Gate(
                    left: CartridgeExpressions.Of(state: "score"),
                    comparison: ActionStateComparison.Less,
                    right: CartridgeExpressions.Of(constant: 300)
                ),
                Body: [
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "score"),
                        Operation: ExpressionOp.Add,
                        Value: CartridgeExpressions.Of(constant: 7)
                    ),
            ]
            )],
        });

        using var machine = new WideProbe(result: result);

        machine.Run(frames: 60);

        var address = result.Variables["score"];
        var value = machine.Read(address: address) | (machine.Read(address: (address + 1)) << 8);

        // The gate stops it in 0..306: the last admitted add starts below 300 and carries at most six past it.
        Assert.InRange(
            actual: value,
            high: 306,
            low: 300
        );
    }
    [Fact]
    public void AWideSlotInAByteFieldIsRefused() {
        var document = Base(target: "cgb") with {
            Sprites = [new CartridgeSprite(
                Name: "cursor",
                Tile: CartridgeExpressions.Of(constant: 0),
                X: CartridgeExpressions.Of(state: "score"),
                Y: CartridgeExpressions.Of(constant: 0),
                Visible: CartridgeExpressions.Of(constant: 1)
            )],
        };

        Assert.Contains(
            collection: CartridgeDocuments.Validate(document: document),
            filter: error => error.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "wide slot"
            )
        );
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void AWideSubtractBorrowsAcrossTheLowByte(string target) {
        // 0x0100 - 1 is the case a byte-at-a-time subtract gets wrong without the borrow.
        var result = Compiler(target: target).Compile(document: Base(target: target) with {
            Variables = [new CartridgeVariable(
                Initial: 256,
                Max: 65535,
                Name: "score"
            ), new CartridgeVariable(
                Name: "done",
                Initial: 0
            )],
            Rules = [new CartridgeRule(
                Name: "spend",
                When: CartridgeExpressions.Gate(
                    left: CartridgeExpressions.Of(state: "done"),
                    comparison: ActionStateComparison.Equal,
                    right: CartridgeExpressions.Of(constant: 0)
                ),
                Body: [
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "score"),
                        Operation: ExpressionOp.Subtract,
                        Value: CartridgeExpressions.Of(constant: 1)
                    ),
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "done"),
                        Value: CartridgeExpressions.Of(constant: 1)
                    ),
            ]
            )],
        });

        using var machine = new WideProbe(result: result);

        machine.Run(frames: 30);

        var address = result.Variables["score"];

        Assert.Equal(
            expected: 0xFF,
            actual: machine.Read(address: address)
        );
        Assert.Equal(
            expected: 0x00,
            actual: machine.Read(address: (address + 1))
        );
    }
    [Fact]
    public void AnOperationWithNoSixteenBitFormIsRefused() {
        var document = Base(target: "cgb") with {
            Rules = [new CartridgeRule(
                Name: "scale",
                Body: [
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "score"),
                        Operation: ExpressionOp.Multiply,
                        Value: CartridgeExpressions.Of(constant: 2)
                    ),
            ]
            )],
        };

        Assert.Contains(
            collection: CartridgeDocuments.Validate(document: document),
            filter: error => error.Path.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: ".operation"
            )
        );
    }
    [Fact]
    public void TheWindowBoundsTheDeclaredSlotsByBytesRatherThanByCount() {
        var document = Base(target: "cgb") with {
            Variables = [.. Enumerable.Range(
                count: ((CartridgeLimits.VariableCount / 2) + 1),
                start: 0
            )
                .Select(selector: index => new CartridgeVariable(
                Initial: 0,
                Max: CartridgeLimits.WideMaximum,
                Name: $"w{index}"
            ))],
        };

        Assert.Contains(
            collection: CartridgeDocuments.Validate(document: document),
            filter: error => (error.Path == "variables")
        );
    }

    private sealed class WideProbe : IDisposable {
        private readonly AgbVerifyMachineDriver? m_agb;
        private readonly VerifyMachineDriver? m_hgb;

        public WideProbe(CartridgeCompilation result) {
            if (result.Target == "agb") { m_agb = new AgbVerifyMachineDriver(
                rom: result.Rom,
                label: "wide"
            ); } else { m_hgb = new VerifyMachineDriver(
                rom: result.Rom,
                label: "wide"
            ); }
        }

        public void Dispose() { m_agb?.Dispose(); m_hgb?.Dispose(); }
        public byte Read(uint address) => (m_agb?.ReadByte(address: address) ?? m_hgb!.Read(address: ((ushort)address)));
        public void Run(int frames) {
            m_agb?.RunFrames(
                frames: frames,
                keys: AgbKeys.None
            );
            m_hgb?.RunFrames(
                buttons: JoypadButtons.None,
                frames: frames
            );
        }
    }
}
