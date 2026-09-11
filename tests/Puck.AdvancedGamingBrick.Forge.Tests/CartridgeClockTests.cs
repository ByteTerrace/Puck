using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers the battery-backed clock reaching authored state.</summary>
public sealed class CartridgeClockTests {
    [Fact]
    public void AClockStepFillsTheNamedSlots() {
        var document = CartridgeDocuments.Create(target: "cgb", title: "CLOCK") with {
            Variables = [
                new CartridgeVariable(Name: "sec", Initial: 200),
                new CartridgeVariable(Name: "min", Initial: 200),
                new CartridgeVariable(Name: "hour", Initial: 200),
                new CartridgeVariable(Name: "day", Initial: 200),
            ],
            Clock = new CartridgeClock(Seconds: "sec", Minutes: "min", Hours: "hour", Days: "day"),
            Rules = [new CartridgeRule(Name: "read", When: [], Body: [new CartridgeStatement(Kind: "clock")])],
        };
        var result = new HgbCartridgeCompiler().Compile(document: document);

        // The header must advertise the timer or the machine builds a cartridge with no clock to read.
        Assert.Equal(expected: 0x10, actual: result.Rom[0x0147]);

        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "clock");
        machine.RunFrames(buttons: JoypadButtons.None, frames: 12);

        // Each slot now holds a real reading rather than its authored placeholder.
        Assert.InRange(actual: machine.Read(address: (ushort)result.Variables["sec"]), low: 0, high: 59);
        Assert.InRange(actual: machine.Read(address: (ushort)result.Variables["min"]), low: 0, high: 59);
        Assert.InRange(actual: machine.Read(address: (ushort)result.Variables["hour"]), low: 0, high: 23);
        Assert.NotEqual(expected: 200, actual: machine.Read(address: (ushort)result.Variables["day"]));
    }

    [Fact]
    public void ValidationGatesTheClockOnDeclarationAndTarget() {
        var document = CartridgeDocuments.Create(target: "cgb", title: "CLOCKBAD") with {
            Variables = [new CartridgeVariable(Name: "x", Initial: 0)],
        };
        Refuses(document: document with { Rules = [new CartridgeRule(Name: "r", When: [], Body: [new CartridgeStatement(Kind: "clock")])] }, fragment: "requires a declared clock");
        Refuses(document: document with { Clock = new CartridgeClock(Seconds: "missing", Minutes: null, Hours: null, Days: null) }, fragment: "Unknown state variable");
        Refuses(document: document with { Clock = new CartridgeClock(Seconds: null, Minutes: null, Hours: null, Days: null) }, fragment: "at least one state slot");

        var advanced = CartridgeDocuments.Create(target: "agb", title: "CLOCKAGB") with {
            Variables = [new CartridgeVariable(Name: "x", Initial: 0)],
            Clock = new CartridgeClock(Seconds: "x", Minutes: null, Hours: null, Days: null),
        };
        Refuses(document: advanced, fragment: "cgb capability today");
    }

    private static void Refuses(CartridgeDocument document, string fragment) {
        var errors = CartridgeDocuments.Validate(document: document);
        Assert.Contains(collection: errors, filter: error => error.Message.Contains(value: fragment, comparisonType: StringComparison.Ordinal));
    }
}
