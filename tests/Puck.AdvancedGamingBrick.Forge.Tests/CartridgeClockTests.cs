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
    public void TheAdvancedMachineFillsTheSameSlotsOverItsSerialClock() {
        var document = CartridgeDocuments.Create(target: "agb", title: "CLOCK") with {
            Variables = [
                new CartridgeVariable(Name: "sec", Initial: 200),
                new CartridgeVariable(Name: "min", Initial: 200),
                new CartridgeVariable(Name: "hour", Initial: 200),
                new CartridgeVariable(Name: "day", Initial: 200),
                new CartridgeVariable(Name: "month", Initial: 200),
                new CartridgeVariable(Name: "year", Initial: 200),
            ],
            Clock = new CartridgeClock(Seconds: "sec", Minutes: "min", Hours: "hour", Days: null, Day: "day", Month: "month", Year: "year"),
            Rules = [new CartridgeRule(Name: "read", When: [], Body: [new CartridgeStatement(Kind: "clock")])],
        };
        var result = new AgbCartridgeCompiler().Compile(document: document);

        // The host decides the cartridge carries a clock by scanning the image for this identifier.
        Assert.True(condition: System.Text.Encoding.ASCII.GetString(bytes: result.Rom).Contains(value: AgbRealTimeClock.SignatureText, comparisonType: StringComparison.Ordinal));

        using var machine = new AgbVerifyMachineDriver(rom: result.Rom, label: "clock-agb");
        machine.RunFrames(keys: AgbKeys.None, frames: 12);

        // Readings in binary, as the Color machine reports them, rather than the device's own decimal-coded nibbles.
        Assert.InRange(actual: machine.ReadByte(address: result.Variables["sec"]), low: 0, high: 59);
        Assert.InRange(actual: machine.ReadByte(address: result.Variables["min"]), low: 0, high: 59);
        Assert.InRange(actual: machine.ReadByte(address: result.Variables["hour"]), low: 0, high: 23);
        Assert.InRange(actual: machine.ReadByte(address: result.Variables["day"]), low: 1, high: 31);
        Assert.InRange(actual: machine.ReadByte(address: result.Variables["month"]), low: 1, high: 12);
        Assert.InRange(actual: machine.ReadByte(address: result.Variables["year"]), low: 0, high: 99);
    }

    [Fact]
    public void TheClockAdvancesAsTheMachineRuns() {
        var document = CartridgeDocuments.Create(target: "agb", title: "CLOCKRUN") with {
            Variables = [new CartridgeVariable(Name: "sec", Initial: 200)],
            Clock = new CartridgeClock(Seconds: "sec", Minutes: null, Hours: null, Days: null),
            Rules = [new CartridgeRule(Name: "read", When: [], Body: [new CartridgeStatement(Kind: "clock")])],
        };
        var result = new AgbCartridgeCompiler().Compile(document: document);
        using var machine = new AgbVerifyMachineDriver(rom: result.Rom, label: "clock-run");

        machine.RunFrames(keys: AgbKeys.None, frames: 8);
        var first = machine.ReadByte(address: result.Variables["sec"]);
        machine.RunFrames(keys: AgbKeys.None, frames: 140);

        // Over two seconds of frames the reading must have moved, or the read is returning a frozen value.
        Assert.NotEqual(expected: first, actual: machine.ReadByte(address: result.Variables["sec"]));
    }

    [Fact]
    public void NeitherTheEntryStubNorTheRoutineSitsUnderTheDeviceOverlay() {
        // A cartridge carrying a clock overlays its registers on ROM at 0x0C4..0x0C9. An instruction fetched from
        // there reads pin state rather than code, so anything executable placed in that window never runs.
        Assert.True(condition: (AgbForgeCartridge.EntryStubOffset > 0xC9) || ((AgbForgeCartridge.EntryStubOffset + 8) <= 0xC4));
        Assert.True(condition: AgbForgeCartridge.CodeOffset > 0xC9);
    }

    [Fact]
    public void ValidationGatesTheClockOnDeclarationAndTarget() {
        var document = CartridgeDocuments.Create(target: "cgb", title: "CLOCKBAD") with {
            Variables = [new CartridgeVariable(Name: "x", Initial: 0)],
        };
        Refuses(document: document with { Rules = [new CartridgeRule(Name: "r", When: [], Body: [new CartridgeStatement(Kind: "clock")])] }, fragment: "requires a declared clock");
        Refuses(document: document with { Clock = new CartridgeClock(Seconds: "missing", Minutes: null, Hours: null, Days: null) }, fragment: "Unknown state variable");
        Refuses(document: document with { Clock = new CartridgeClock(Seconds: null, Minutes: null, Hours: null, Days: null) }, fragment: "at least one state slot");

        // The two machines carry different clocks, so each refuses the other's fields rather than inventing a value.
        var advanced = CartridgeDocuments.Create(target: "agb", title: "CLOCKAGB") with {
            Variables = [new CartridgeVariable(Name: "x", Initial: 0)],
        };
        Refuses(
            document: advanced with { Clock = new CartridgeClock(Seconds: "x", Minutes: null, Hours: null, Days: "x") },
            fragment: "calendar date rather than a day count");
        Refuses(
            document: document with { Clock = new CartridgeClock(Seconds: "x", Minutes: null, Hours: null, Days: null, Month: "x") },
            fragment: "calendar month needs the advanced machine");
    }

    private static void Refuses(CartridgeDocument document, string fragment) {
        var errors = CartridgeDocuments.Validate(document: document);
        Assert.Contains(collection: errors, filter: error => error.Message.Contains(value: fragment, comparisonType: StringComparison.Ordinal));
    }
}
