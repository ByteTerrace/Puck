using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers the battery-backed clock reaching authored state.</summary>
public sealed class CartridgeClockTests {
    // The two machines carry different clocks, so each refuses the other's fields rather than inventing a value.
    private static readonly CartridgeRefusal[] Refusals = [
        new(
            Name: "a clock step without a clock",
            Document: Bad(target: "cgb") with { Rules = [new CartridgeRule(
                    Name: "r",
                    Body: [new CartridgeStatement(Kind: "clock")]
                )] },
            Path: "rules[0].body[0]",
            Fragment: "requires a declared clock"
        ),
        new(
            Name: "an unknown slot",
            Document: Bad(target: "cgb") with { Clock = new CartridgeClock(
                Seconds: "missing",
                Minutes: null,
                Hours: null,
                Days: null
            ) },
            Path: "clock.seconds",
            Fragment: "Unknown state variable"
        ),
        new(
            Name: "a clock filling nothing",
            Document: Bad(target: "cgb") with { Clock = new CartridgeClock(
                Seconds: null,
                Minutes: null,
                Hours: null,
                Days: null
            ) },
            Path: "clock",
            Fragment: "at least one state slot"
        ),
        new(
            Name: "a day count on the advanced machine",
            Document: Bad(target: "agb") with { Clock = new CartridgeClock(
                Seconds: "x",
                Minutes: null,
                Hours: null,
                Days: "x"
            ) },
            Path: "clock.days",
            Fragment: "calendar date rather than a day count"
        ),
        new(
            Name: "a calendar month on the colour machine",
            Document: Bad(target: "cgb") with { Clock = new CartridgeClock(
                Seconds: "x",
                Minutes: null,
                Hours: null,
                Days: null,
                Month: "x"
            ) },
            Path: "clock.month",
            Fragment: "calendar month needs the advanced machine"
        ),
    ];

    public static TheoryData<string> RefusalNames => CartridgeRefusal.Names(table: Refusals);

    private static CartridgeDocument Bad(string target) => CartridgeDocuments.Create(
        target: target,
        title: "CLOCKBAD"
    ) with {
        Variables = [new CartridgeVariable(
            Name: "x",
            Initial: 0
        )],
    };

    [Fact]
    public void AClockStepFillsTheNamedSlots() {
        var document = CartridgeDocuments.Create(
            target: "cgb",
            title: "CLOCK"
        ) with {
            Variables = [
                new CartridgeVariable(
                Name: "sec",
                Initial: 200
            ),
                new CartridgeVariable(
                Name: "min",
                Initial: 200
            ),
                new CartridgeVariable(
                Name: "hour",
                Initial: 200
            ),
                new CartridgeVariable(
                Name: "day",
                Initial: 200
            ),
            ],
            Clock = new CartridgeClock(
            Seconds: "sec",
            Minutes: "min",
            Hours: "hour",
            Days: "day"
        ),
            Rules = [new CartridgeRule(
                Name: "read",
                Body: [new CartridgeStatement(Kind: "clock")]
            )],
        };
        var result = new HgbCartridgeCompiler().Compile(document: document);

        // The header must advertise the timer or the machine builds a cartridge with no clock to read.
        Assert.Equal(
            expected: 0x10,
            actual: result.Rom[0x0147]
        );

        using var machine = new VerifyMachineDriver(
            rom: result.Rom,
            label: "clock"
        );

        machine.RunFrames(
            buttons: JoypadButtons.None,
            frames: 12
        );

        // Each slot now holds a real reading rather than its authored placeholder.
        Assert.InRange(
            actual: machine.Read(address: ((ushort)result.Variables["sec"])),
            low: 0,
            high: 59
        );
        Assert.InRange(
            actual: machine.Read(address: ((ushort)result.Variables["min"])),
            low: 0,
            high: 59
        );
        Assert.InRange(
            actual: machine.Read(address: ((ushort)result.Variables["hour"])),
            low: 0,
            high: 23
        );
        Assert.NotEqual(
            expected: 200,
            actual: machine.Read(address: ((ushort)result.Variables["day"]))
        );
    }
    [Fact]
    public void NeitherTheEntryStubNorTheRoutineSitsUnderTheDeviceOverlay() {
        // A cartridge carrying a clock overlays its registers on ROM at 0x0C4..0x0C9. An instruction fetched from
        // there reads pin state rather than code, so anything executable placed in that window never runs.
        Assert.True(condition: ((AgbForgeCartridge.EntryStubOffset > 0xC9) || ((AgbForgeCartridge.EntryStubOffset + 8) <= 0xC4)));
        Assert.True(condition: (AgbForgeCartridge.CodeOffset > 0xC9));
    }
    [Fact]
    public void TheAdvancedMachineFillsTheSameSlotsOverItsSerialClock() {
        var document = CartridgeDocuments.Create(
            target: "agb",
            title: "CLOCK"
        ) with {
            Variables = [
                new CartridgeVariable(
                Name: "sec",
                Initial: 200
            ),
                new CartridgeVariable(
                Name: "min",
                Initial: 200
            ),
                new CartridgeVariable(
                Name: "hour",
                Initial: 200
            ),
                new CartridgeVariable(
                Name: "day",
                Initial: 200
            ),
                new CartridgeVariable(
                Name: "month",
                Initial: 200
            ),
                new CartridgeVariable(
                Name: "year",
                Initial: 200
            ),
            ],
            Clock = new CartridgeClock(
            Day: "day",
            Days: null,
            Hours: "hour",
            Minutes: "min",
            Month: "month",
            Seconds: "sec",
            Year: "year"
        ),
            Rules = [new CartridgeRule(
                Name: "read",
                Body: [new CartridgeStatement(Kind: "clock")]
            )],
        };
        var result = new AgbCartridgeCompiler().Compile(document: document);

        // The host decides the cartridge carries a clock by scanning the image for this identifier.
        Assert.True(condition: System.Text.Encoding.ASCII.GetString(bytes: result.Rom).Contains(
            comparisonType: StringComparison.Ordinal,
            value: AgbRealTimeClock.SignatureText
        ));

        using var machine = new AgbVerifyMachineDriver(
            rom: result.Rom,
            label: "clock-agb"
        );

        machine.RunFrames(
            frames: 12,
            keys: AgbKeys.None
        );

        // Readings in binary, as the Color machine reports them, rather than the device's own decimal-coded nibbles.
        Assert.InRange(
            actual: machine.ReadByte(address: result.Variables["sec"]),
            low: 0,
            high: 59
        );
        Assert.InRange(
            actual: machine.ReadByte(address: result.Variables["min"]),
            low: 0,
            high: 59
        );
        Assert.InRange(
            actual: machine.ReadByte(address: result.Variables["hour"]),
            low: 0,
            high: 23
        );
        Assert.InRange(
            actual: machine.ReadByte(address: result.Variables["day"]),
            low: 1,
            high: 31
        );
        Assert.InRange(
            actual: machine.ReadByte(address: result.Variables["month"]),
            low: 1,
            high: 12
        );
        Assert.InRange(
            actual: machine.ReadByte(address: result.Variables["year"]),
            low: 0,
            high: 99
        );
    }
    [Fact]
    public void TheClockAdvancesAsTheMachineRuns() {
        var document = CartridgeDocuments.Create(
            target: "agb",
            title: "CLOCKRUN"
        ) with {
            Variables = [new CartridgeVariable(
                Name: "sec",
                Initial: 200
            )],
            Clock = new CartridgeClock(
            Seconds: "sec",
            Minutes: null,
            Hours: null,
            Days: null
        ),
            Rules = [new CartridgeRule(
                Name: "read",
                Body: [new CartridgeStatement(Kind: "clock")]
            )],
        };
        var result = new AgbCartridgeCompiler().Compile(document: document);
        using var machine = new AgbVerifyMachineDriver(
            rom: result.Rom,
            label: "clock-run"
        );

        machine.RunFrames(
            frames: 8,
            keys: AgbKeys.None
        );
        var first = machine.ReadByte(address: result.Variables["sec"]);

        // Within two seconds of frames the reading must move, or the read is returning a frozen value.
        machine.RunFramesUntil(
            awaited: "the seconds reading moving",
            keys: AgbKeys.None,
            limit: 140,
            until: running => (running.ReadByte(address: result.Variables["sec"]) != first)
        );
        Assert.NotEqual(
            expected: first,
            actual: machine.ReadByte(address: result.Variables["sec"])
        );
    }
    [MemberData(memberName: nameof(RefusalNames))]
    [Theory]
    public void ValidationGatesTheClockOnDeclarationAndTarget(string refusal) => CartridgeRefusal.Holds(
        name: refusal,
        table: Refusals
    );
}
