using Puck.Abstractions.Sources;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: the names presentation generates beside names an author writes take the reserved
/// spelling <see cref="GeneratedName"/> defines, and the doors those authored names pass refuse it, so the two cannot
/// meet. A capture is named <c>&lt;station&gt;~&lt;tick&gt;</c> and splits back into its station and tick; a station
/// carrying <c>~</c>, or differing from another only in case, is refused. A session screen's view is <c>session$&lt;screen&gt;</c> and a seat-relative camera's
/// per-seat view <c>&lt;camera&gt;$seat$&lt;seat&gt;</c>; no camera name the validator admits equals either. A state
/// reference's key names the reading body only when it is exactly <c>$body</c>; a key that contains that text reads
/// the cell spelled that way. Each law's mutation proof is running it against the joiners and the substring rewrite
/// these sites used before (<c>-</c>, <c>:</c>, <c>@seat:</c>).</summary>
public sealed class WorldPresentationNameLawTests {
    private const string SeatCamera = "cam";

    private static WorldCamera Camera(string name, WorldAnchor? anchor = null) => new(
        Name: name,
        Anchor: anchor,
        Rig: new WorldCameraProgram(
            Name: $"{name}-rig",
            Version: WorldCameraProgram.CurrentVersion,
            Operations: [new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 0.9f))]
        ),
        RenderWidth: 320U,
        RenderHeight: 240U
    );
    private static WorldDefinition WithCaptureStation(string station) => Fixtures.BuildDocument() with {
        Captures = new WorldCapturesSection(
            Directory: null,
            Rows: [new WorldCaptureRow(
                Palette: [new WorldCapturePaletteEntry(
                    Color: "#000000",
                    Material: 0
                )],
                Station: CellName.Parse(candidate: station),
                Ticks: [5UL]
            )]
        ),
    };
    private static string Refusal(WorldDefinition definition) => (WorldDefinitionValidator.TryValidateLocally(
        definition: definition,
        reason: out var reason
    )
        ? string.Empty
        : reason
    );

    [InlineData("lattice", 860UL)]
    [InlineData("lattice-860", 5UL)]
    [InlineData("a-1", 2UL)]
    [InlineData("$status", 1UL)]
    [Theory]
    public void ACaptureNameSplitsAtTheFileJoinerIntoItsStationAndTick(string station, ulong tick) {
        var name = WorldCaptureRow.CaptureName(
            station: station,
            tick: tick
        );

        Assert.True(condition: GeneratedName.IsGeneratedFile(name: name), userMessage: name);

        var parts = name.Split(separator: GeneratedName.FileJoiner);

        Assert.Equal(
            expected: [station, tick.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)],
            actual: parts
        );
    }
    [Fact]
    public void ACaptureStationCarryingTheFileJoinerIsRefusedAndOneWithoutItIsNot() {
        Assert.Contains(
            actualString: Refusal(definition: WithCaptureStation(station: "lattice~860")),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "captures.rows[0].station 'lattice~860' carries '~'"
        );
        Assert.Equal(
            actual: Refusal(definition: WithCaptureStation(station: "lattice-860")),
            expected: string.Empty
        );
    }
    // Two stations that differ only in case name the same capture files on a case-insensitive file system.
    [Fact]
    public void CaptureStationsThatDifferOnlyInCaseAreRefusedNamingBoth() {
        var definition = WithCaptureStation(station: "Lattice");

        definition = definition with {
            Captures = definition.Captures! with {
                Rows = [.. definition.Captures.Rows, definition.Captures.Rows[0] with { Station = CellName.Parse(candidate: "lattice") }],
            },
        };

        Assert.Contains(
            actualString: Refusal(definition: definition),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "captures.rows[1].station 'lattice' differs from station 'Lattice' only in case"
        );
    }
    // A row reads the root when it names no instance, and the world beneath the root's passes when it names it; every
    // other name, the root's own included, is refused naming the row.
    [Fact]
    public void ACaptureRowNamesTheWorldInstanceOrNoneAndAnyOtherNameIsRefused() {
        var definition = WithCaptureStation(station: "lattice");

        foreach (var instance in new string?[] { null, WorldViewGraphs.WorldInstance }) {
            Assert.Equal(
                actual: Refusal(definition: definition with {
                    Captures = definition.Captures! with {
                        Rows = [definition.Captures.Rows[0] with { Instance = instance }],
                    },
                }),
                expected: string.Empty
            );
        }

        foreach (var instance in new[] { WorldViewGraphs.MainInstance, "elsewhere" }) {
            Assert.Contains(
                actualString: Refusal(definition: definition with {
                    Captures = definition.Captures! with {
                        Rows = [definition.Captures.Rows[0] with { Instance = instance }],
                    },
                }),
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: $"captures.rows[0].instance '{instance}' names no render-graph instance a capture can read"
            );
        }
    }
    // The control: stations that differ in more than case are admitted together.
    [Fact]
    public void CaptureStationsThatDifferInMoreThanCaseAreAdmitted() {
        var definition = WithCaptureStation(station: "lattice");

        definition = definition with {
            Captures = definition.Captures! with {
                Rows = [.. definition.Captures.Rows, definition.Captures.Rows[0] with { Station = CellName.Parse(candidate: "lattices") }],
            },
        };

        Assert.Equal(
            actual: Refusal(definition: definition),
            expected: string.Empty
        );
    }
    [Fact]
    public void EveryViewNameTheEngineMintsIsInTheReservedForm() {
        var seat = WorldSeatAnchors.RegistrationName(
            camera: Camera(
                anchor: new WorldAnchor.Seat(),
                name: SeatCamera
            ),
            seat: 2
        );

        Assert.Equal(
            actual: WorldViewNames.Session(screen: 3),
            expected: "session$3"
        );
        Assert.Equal(
            actual: seat,
            expected: "cam$seat$2"
        );
        Assert.Equal(
            actual: seat.Split(separator: GeneratedName.Joiner),
            expected: [SeatCamera, WorldViewNames.SeatPart, "2"]
        );

        var source = WorldViewNames.Source(
            producer: "qr",
            settings: null
        );

        Assert.Equal(
            actual: source.Split(separator: GeneratedName.Joiner),
            expected: [WorldViewNames.SourceHead, "qr", ImageSourceSettings.Digest(settings: null)]
        );
        Assert.Matches(
            actualString: ImageSourceSettings.Digest(settings: null),
            expectedRegexPattern: "^[0-9a-f]{16}$"
        );

        foreach (var name in ((string[])[WorldViewNames.Session(screen: 0), seat, source])) {
            Assert.True(condition: GeneratedName.IsGenerated(name: name), userMessage: name);
            Assert.False(condition: GeneratedName.TryValidateAuthored(
                name: name,
                reason: out _
            ), userMessage: name);
        }
    }
    // A camera shares the view registration namespace with the views the engine mints. Every candidate spells one of
    // those minted names in either the reserved form or the author characters the sites once joined with; whichever
    // the validator admits must register under a name no minted view has.
    [InlineData("session:0")]
    [InlineData("session$0")]
    [InlineData("cam@seat:1")]
    [InlineData("cam$seat$1")]
    [Theory]
    public void NoCameraNameTheValidatorAdmitsEqualsAViewNameTheEngineMints(string name) {
        var seatRelative = Camera(
            anchor: new WorldAnchor.Seat(),
            name: SeatCamera
        );
        var authored = Camera(name: name);
        var admitted = (Refusal(definition: Fixtures.BuildDocument() with { CamerasRaw = [seatRelative, authored] }) == string.Empty);
        var minted = new HashSet<string>(comparer: StringComparer.Ordinal) {
            WorldViewNames.Session(screen: 0),
            WorldSeatAnchors.RegistrationName(
                camera: seatRelative,
                seat: 1
            ),
        };

        Assert.False(
            condition: (admitted && minted.Contains(item: WorldSeatAnchors.RegistrationName(
            camera: authored,
            seat: 1
        ))),
            userMessage: $"camera '{name}' was admitted and registers under a name the engine mints for another view"
        );
    }
    // The control: a camera name outside every minted form is admitted.
    [Fact]
    public void AnOrdinaryCameraNameIsAdmitted() => Assert.Equal(
        actual: Refusal(definition: Fixtures.BuildDocument() with { CamerasRaw = [Camera(name: "overview")] }),
        expected: string.Empty
    );
    [InlineData("$bodyguard")]
    [InlineData("air$body")]
    [InlineData("$body$body")]
    [Theory]
    public void AKeyThatOnlyContainsTheBodyTokenReadsTheCellSpelledThatWay(string key) {
        Assert.True(condition: WorldGaitDrivers.TryResolveBodyKey(
            bodyIndex: 3,
            key: key,
            resolved: out var resolved
        ));
        Assert.Equal(
            actual: resolved,
            expected: key
        );

        // A row whose cells hold both the literal key and what a substring rewrite would turn it into: the reference
        // reads the literal one.
        var rewritten = key.Replace(
            comparisonType: StringComparison.Ordinal,
            newValue: "3",
            oldValue: StateBinding.BodyKey
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: CellName.Parse(candidate: "air"),
                Kind: CellKind.Int,
                Cells: [
                    new StateCell(
                        Key: CellName.Parse(candidate: key),
                        Value: CellValue.Int(value: 7L)
                    ),
                    new StateCell(
                        Key: CellName.Parse(candidate: rewritten),
                        Value: CellValue.Int(value: 5L)
                    ),
                ]
            )]),
        };

        Assert.True(condition: ClientFixtures.StateReads(
            bodyIndex: 3,
            definition: definition
        ).TryNumber(
            reference: $"state.air.{key}",
            truth: true,
            value: out var value
        ));
        Assert.Equal(
            actual: value,
            expected: 7f
        );
    }
    // The control: a key that is exactly the token names the reading body, and refuses with no body reading.
    [Fact]
    public void AKeyThatIsTheBodyTokenNamesTheReadingBody() {
        Assert.True(condition: WorldGaitDrivers.TryResolveBodyKey(
            bodyIndex: 3,
            key: StateBinding.BodyKey,
            resolved: out var resolved
        ));
        Assert.Equal(
            actual: resolved,
            expected: "3"
        );
        Assert.False(condition: WorldGaitDrivers.TryResolveBodyKey(
            bodyIndex: -1,
            key: StateBinding.BodyKey,
            resolved: out _
        ));
    }
}
