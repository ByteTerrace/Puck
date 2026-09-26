using Puck.Abstractions.Machines;
using Puck.Assets.Documents;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Covers the light sensor a cartridge reads through <see cref="CartridgeExpressions.Light"/>: the Color machine's
/// compiled read sees a light gun aimed at a lit pixel and nothing else, the advanced machine has no receiver and
/// refuses the read, and the read prices at its own measured weight.
/// </summary>
public sealed class CartridgeLightTests {
    private const int PictureHeight = 144;
    private const int PictureWidth = 160;
    // Tile columns 0-9 of the map are lit, so the dark half begins at picture column 80.
    private const int LitColumns = 10;
    private const int SettleFrames = 4;

    private static readonly CartridgeRefusal[] Refusals = [
        new(
            Name: "a light read on the advanced machine",
            Document: Sensing(target: "agb"),
            Path: "rules[0].body[0].value",
            Fragment: "agb target has no light sensor"
        ),
        new(
            Name: "a light read carrying a mode",
            Document: Sensing(
                operand: "$light:held",
                target: "cgb"
            ),
            Path: "rules[0].body[0].value",
            Fragment: "not a channel a cartridge answers"
        ),
        new(
            Name: "a write to the light sensor",
            Document: Sensing(target: "cgb") with { Rules = [new CartridgeRule(
                    Name: "sense",
                    Body: [new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: CartridgeExpressions.Light),
                        Operation: null,
                        Value: CartridgeExpressions.Of(constant: 1)
                    )]
                )] },
            Path: "rules[0].body[0].target.state",
            Fragment: "Unknown state variable '$light'"
        ),
    ];

    public static TheoryData<string> RefusalNames => CartridgeRefusal.Names(table: Refusals);

    // An aim at a picture column of the middle row, as the fraction of the picture a host records.
    private static MachinePointer At(int column) => new(
        x: ((ushort)((column * MachinePointer.FractionUnits) / PictureWidth)),
        y: ((ushort)(((PictureHeight / 2) * MachinePointer.FractionUnits) / PictureHeight))
    );
    // A picture whose left half is lit and right half dark, and one rule publishing the light sensor into 'seen'
    // every frame.
    private static CartridgeDocument Sensing(string target, string operand = CartridgeExpressions.Light) {
        var document = CartridgeDocuments.Create(
            target: target,
            title: "LIGHT"
        );
        var colours = document.Palettes.Background[0].Length;
        var palette = new int[colours];

        palette[0] = 0x7FFF;

        return document with {
            Palettes = new CartridgePalettes(
                Background: [palette],
                Object: [palette]
            ),
            Tiles = [
                new CartridgeTile(
                    Name: "lit",
                    Pixels: [.. Enumerable.Repeat(
                        count: 8,
                        element: "00000000"
                    )]
                ),
                new CartridgeTile(
                    Name: "dark",
                    Pixels: [.. Enumerable.Repeat(
                        count: 8,
                        element: "11111111"
                    )]
                ),
            ],
            Map = [.. Enumerable.Range(
                count: 1024,
                start: 0
            ).Select(selector: static cell => (((cell % 32) < LitColumns)
                ? 0
                : 1))],
            Variables = [new CartridgeVariable(
                Name: "seen",
                Initial: 7
            )],
            Rules = [new CartridgeRule(
                Name: "sense",
                Body: [new CartridgeStatement(
                    Kind: "set",
                    Target: new CartridgeTarget(State: "seen"),
                    Operation: null,
                    Value: CartridgeExpressions.Of(state: operand)
                )]
            )],
        };
    }

    [MemberData(memberName: nameof(RefusalNames))]
    [Theory]
    public void ValidationGatesTheLightReadOnTheReceiverAndItsOneSpelling(string refusal) => CartridgeRefusal.Holds(
        name: refusal,
        table: Refusals
    );
    [Fact]
    public void TheColourMachinesReadSeesTheGunOnALitPixelAndOnlyThere() {
        using var probe = CartridgeProbe.Boot(
            document: Sensing(target: "cgb"),
            frames: SettleFrames,
            label: "light"
        );
        var aims = new (MachinePointer Aim, int Expected)[] {
            (MachinePointer.Off, 0),
            (At(column: 40), 1),
            (At(column: 120), 0),
            (At(column: 8), 1),
            (MachinePointer.Off, 0),
        };

        foreach (var (aim, expected) in aims) {
            probe.Aim(pointer: aim);
            probe.Run(frames: 2);
            Assert.Equal(
                actual: probe.Read(variable: "seen"),
                expected: ((byte)expected)
            );
        }
    }
    [Fact]
    public void TheAdvancedCompilerRefusesTheReadItHasNoReceiverFor() {
        var refusal = Assert.Throws<DocumentValidationException>(testCode: () => new AgbCartridgeCompiler().Compile(document: Sensing(target: "agb")));

        Assert.Contains(
            collection: refusal.Errors,
            filter: static error => error.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "agb target has no light sensor"
            )
        );
        // The same document on the Color machine compiles.
        Assert.NotEmpty(collection: new HgbCartridgeCompiler().Compile(document: Sensing(target: "cgb")).Rom);
    }
    [Fact]
    public void ALightReadPricesAtItsMeasuredWeightAndIsUnmodeledWithoutAReceiver() {
        var light = CartridgeCost.Frame(
            document: Sensing(target: "cgb"),
            profile: CartridgeCostProfile.Humble
        );
        var variable = CartridgeCost.Frame(
            document: Sensing(
                operand: "seen",
                target: "cgb"
            ),
            profile: CartridgeCostProfile.Humble
        );

        Assert.True(condition: (light.IsKnown && variable.IsKnown));
        Assert.Equal(
            actual: (light.Cycles - variable.Cycles),
            expected: (CartridgeCostProfile.Humble.OperandLight!.Value - CartridgeCostProfile.Humble.OperandVariable)
        );
        Assert.Null(@object: CartridgeCostProfile.Advanced.OperandLight);
        Assert.True(condition: CartridgeCost.Frame(
            document: Sensing(target: "cgb"),
            profile: CartridgeCostProfile.Advanced
        ).IsUnmodeled);
    }
}
