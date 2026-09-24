using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Compiles the authored tetromino cartridge once and walks it from boot to its title, its mode menu and a first-mode game,
/// keeping a snapshot at each so a test starts from the position its claim is about. The walk is the cartridge's own
/// natural path; <see cref="TetrominoCartridgeTests.TheCartridgeOpensOnItsTitleAndWalksThroughTheModeMenuIntoAGame"/>
/// asserts it step by step.
/// </summary>
public sealed class TetrominoCartridgeFixture {
    public TetrominoCartridgeFixture() {
        Document = CartridgeDocuments.Parse(utf8: File.ReadAllBytes(path: RepositoryPaths.Resolve(relativePath: "src/Puck.World/Assets/cartridges/tetromino.cgb.cartridge.json")));
        Result = new HgbCartridgeCompiler().Compile(document: Document);

        using var machine = Boot(label: "tetromino-fixture");

        AwaitTitle(machine: machine);
        Title = machine.Snapshot();
        OpenMenu(machine: machine);
        Menu = machine.Snapshot();
        machine.RunFrames(
            buttons: JoypadButtons.Start,
            frames: PressFrames
        );
        AwaitGame(machine: machine);
        Game = machine.Snapshot();
        m_gravityLanding = new Lazy<TetrominoLanding>(valueFactory: () => Land(
            buttons: JoypadButtons.None,
            label: "tetromino-rest"
        ));
    }

    private readonly Lazy<TetrominoLanding> m_gravityLanding;

    /// <summary>Gets the first piece's landing under gravity alone, run once for every test that reads it.</summary>
    public TetrominoLanding GravityLanding => m_gravityLanding.Value;
    /// <summary>Gets the cartridge source.</summary>
    public CartridgeDocument Document { get; }
    /// <summary>Gets the first-mode game on the frame its first piece enters play.</summary>
    public MachineSnapshot Game { get; }
    /// <summary>Gets the mode menu on the frame it finishes painting and starts taking input.</summary>
    public MachineSnapshot Menu { get; }
    /// <summary>Gets the compiled cartridge.</summary>
    public CartridgeCompilation Result { get; }
    /// <summary>Gets the title on the frame it finishes arriving and starts waiting for a press.</summary>
    public MachineSnapshot Title { get; }

    /// <summary>Returns the bus address of an array's first element.</summary>
    public ushort Array(string name) => ((ushort)Result.Arrays[name]);
    /// <summary>Runs until a game is in play, which is the first piece entering the well.</summary>
    public void AwaitGame(VerifyMachineDriver machine) => machine.RunFramesUntil(
        awaited: "a piece in play",
        buttons: JoypadButtons.None,
        limit: 280,
        until: running => (Phase(machine: running) == 0)
    );
    /// <summary>Runs until the title has finished arriving; it takes no press before that.</summary>
    public void AwaitTitle(VerifyMachineDriver machine) => machine.RunFramesUntil(
        awaited: "the title waiting for a press",
        buttons: JoypadButtons.None,
        limit: 480,
        until: running => (Phase(machine: running) == TitleWaits)
    );
    /// <summary>Creates a machine over the cartridge, at power-on or at a snapshot of it.</summary>
    public VerifyMachineDriver Boot(string label, MachineSnapshot? from = null) {
        var machine = new VerifyMachineDriver(
            label: label,
            rom: Result.Rom
        );

        if (from is not null) {
            machine.Restore(snapshot: from);
        }

        return machine;
    }
    /// <summary>Presses start on the title and runs until the mode menu has painted; it takes no input before that.</summary>
    public void OpenMenu(VerifyMachineDriver machine) {
        machine.RunFrames(
            buttons: JoypadButtons.Start,
            frames: PressFrames
        );
        machine.RunFramesUntil(
            awaited: "the mode menu painted",
            buttons: JoypadButtons.None,
            limit: 220,
            until: running => ((Phase(machine: running) == MenuPhase) && (running.Read(address: Variable(name: "painted")) != 0))
        );
    }
    /// <summary>Runs the first-mode game from its first piece until that piece is at rest and the next is in play, holding
    /// the given buttons throughout.</summary>
    /// <param name="buttons">The buttons held on every frame.</param>
    /// <param name="label">The machine's diagnostic label.</param>
    /// <returns>The frames the landing took, the well it left, and the game-over flag.</returns>
    public TetrominoLanding Land(JoypadButtons buttons, string label) {
        using var machine = Boot(
            from: Game,
            label: label
        );
        var frames = machine.RunFramesUntil(
            awaited: "a piece at rest with the next in play",
            buttons: buttons,
            limit: (60 * 20),
            until: running => ((Settled(machine: running) >= 4) && (Phase(machine: running) == 0))
        );
        var well = new byte[WellCells];

        for (var index = 0; (index < WellCells); ++index) {
            well[index] = machine.Read(address: ((ushort)(Array(name: "field") + index)));
        }

        return new TetrominoLanding(
            Frames: frames,
            Over: machine.Read(address: Variable(name: "over")),
            Well: well
        );
    }
    /// <summary>Reads the phase the cartridge's rules dispatch on.</summary>
    public byte Phase(VerifyMachineDriver machine) => machine.Read(address: Variable(name: "ph"));
    /// <summary>Counts the well's settled cells.</summary>
    public int Settled(VerifyMachineDriver machine) {
        var settled = 0;

        for (var index = 0; (index < WellCells); ++index) {
            if (machine.Read(address: ((ushort)(Array(name: "field") + index))) != 0) { ++settled; }
        }

        return settled;
    }
    /// <summary>Returns the bus address of a byte variable.</summary>
    public ushort Variable(string name) => ((ushort)Result.Variables[name]);

    /// <summary>The frames a press is held, enough for the cartridge to see one pressed edge.</summary>
    public const int PressFrames = 4;
    /// <summary>The phase of the mode menu.</summary>
    public const byte MenuPhase = 10;
    /// <summary>The phase the title settles on to wait for a press.</summary>
    public const byte TitleWaits = 18;
    /// <summary>The well's cells: eighteen rows of ten.</summary>
    public const int WellCells = 180;
}
/// <summary>One piece's landing: the frames it took, the well it left, and the game-over flag after it.</summary>
/// <param name="Frames">The frames from the piece entering play to its rest with the next in play.</param>
/// <param name="Over">The cartridge's game-over flag after the landing.</param>
/// <param name="Well">The well's cells after the landing, row by row.</param>
public sealed record TetrominoLanding(int Frames, byte Over, byte[] Well);
/// <summary>
/// Covers the authored tetromino cartridge: the document is the game, so these run the real image on the real machine and
/// assert on its state rather than on the document's shape.
/// </summary>
public sealed class TetrominoCartridgeTests(TetrominoCartridgeFixture cartridge) : IClassFixture<TetrominoCartridgeFixture> {
    private string Digits(VerifyMachineDriver machine, string array) {
        var address = cartridge.Array(name: array);
        var digits = new System.Text.StringBuilder();

        for (var index = 0; (index < 5); ++index) {
            digits.Append(value: machine.Read(address: ((ushort)(address + index))));
        }

        return digits.ToString();
    }
    private ushort Cell(int row, int column) => ((ushort)(cartridge.Array(name: "field") + ((row * 10) + column)));
    // Every row of the ceiling but a column short, so no row is complete: a full well would clear instead of topping out.
    private void FillCeiling(VerifyMachineDriver machine) {
        for (var row = 0; (row < 6); ++row) {
            for (var column = 0; (column < 9); ++column) {
                machine.Write(
                    address: Cell(
                        column: column,
                        row: row
                    ),
                    value: 1
                );
            }
        }
    }
    // The first piece out of the generator is the bar, spawning across columns three to six. Seeding every other column
    // of the floor once the game is running means it completes that row on landing and nothing else can. The seed goes
    // in after the start sequence because starting a game wipes the well, as it should.
    private void FillFloorAroundTheBar(VerifyMachineDriver machine) {
        foreach (var column in new[] { 0, 1, 2, 7, 8, 9 }) {
            machine.Write(
                address: Cell(
                    column: column,
                    row: 17
                ),
                value: 1
            );
        }
    }
    private int Settled(VerifyMachineDriver machine) => cartridge.Settled(machine: machine);
    private static int Voices(VerifyMachineDriver machine) => machine.Read(address: SoundStatus) & 0x07;
    // Lit pixels inside the well, which is where every screen puts its words.
    private static int WellInk(VerifyMachineDriver machine) {
        var backdrop = machine.ReadPixel(
            x: 158,
            y: 142
        );
        var lit = 0;

        for (var y = 0; (y < 144); ++y) {
            for (var x = WellLeft; (x < WellRight); ++x) {
                if (machine.ReadPixel(
                    x: x,
                    y: y
                ) != backdrop) { ++lit; }
            }
        }

        return lit;
    }

    [Fact]
    public void ACompletedRowClearsAndCountsTowardTheLineTotal() {
        // Soft drop lands the bar sooner; the fall under gravity alone is what the resting test proves.
        using var machine = cartridge.Boot(
            from: cartridge.Game,
            label: "tetromino-clear"
        );

        FillFloorAroundTheBar(machine: machine);
        machine.RunFramesUntil(
            awaited: "a line counted",
            buttons: JoypadButtons.Down,
            limit: (60 * 25),
            until: running => (running.Read(address: cartridge.Variable(name: "lines")) != 0)
        );
        Assert.Equal(
            expected: 1,
            actual: machine.Read(address: cartridge.Variable(name: "lines"))
        );

        // The completed row is gone rather than merely marked: the floor holds only what fell after it.
        var floor = 0;

        for (var column = 0; (column < 10); ++column) {
            if (machine.Read(address: Cell(
                column: column,
                row: 17
            )) != 0) { ++floor; }
        }

        Assert.True(
            condition: (floor < 10),
            userMessage: $"{floor} cells still on the floor"
        );
    }
    [Fact]
    public void AFinishedRunIsMeasuredAgainstTheBestAndKeptWhenItWins() {
        using var machine = cartridge.Boot(
            from: cartridge.Game,
            label: "tetromino-best"
        );

        // A row short of one column, so the first piece completes it and the run is worth something. The award is paid
        // after the line is counted, so the score is read once the next piece is in play.
        FillFloorAroundTheBar(machine: machine);
        machine.RunFramesUntil(
            awaited: "a line counted",
            buttons: JoypadButtons.Down,
            limit: (60 * 25),
            until: running => (running.Read(address: cartridge.Variable(name: "lines")) != 0)
        );
        cartridge.AwaitGame(machine: machine);
        Assert.Equal(
            expected: 1,
            actual: machine.Read(address: cartridge.Variable(name: "lines"))
        );

        var scored = Digits(
            array: "score",
            machine: machine
        );

        Assert.NotEqual(
            actual: scored,
            expected: "00000"
        );

        // Top out: the run ends, and a run better than the stored best replaces it before the closing screen is up.
        FillCeiling(machine: machine);
        machine.RunFramesUntil(
            awaited: "the game-over screen painted",
            buttons: JoypadButtons.None,
            limit: (60 * 8),
            until: running => ((cartridge.Phase(machine: running) == GameOver) && (running.Read(address: cartridge.Variable(name: "painted")) != 0))
        );
        Assert.Equal(
            expected: GameOver,
            actual: cartridge.Phase(machine: machine)
        );
        Assert.Equal(
            expected: scored,
            actual: Digits(
                array: "best",
                machine: machine
            )
        );
    }
    [Fact]
    public void AGameSoundsOnThreeVoicesAndTheTitleOnThreeOfItsOwn() {
        // The status register's low nibble reports the channels that are sounding.
        using var machine = cartridge.Boot(
            from: cartridge.Title,
            label: "tetromino-sound"
        );

        Assert.Equal(
            expected: 0x07,
            actual: Voices(machine: machine)
        );

        machine.Restore(snapshot: cartridge.Game);
        machine.RunFramesUntil(
            awaited: "the game's music on three voices",
            buttons: JoypadButtons.None,
            limit: 30,
            until: running => (Voices(machine: running) == 0x07)
        );
        Assert.Equal(
            expected: 0x07,
            actual: Voices(machine: machine)
        );
    }
    [Fact]
    public void APieceComesToRestOnTheFloorAndAnotherFollowsIt() {
        // The landing left to gravity alone: something settles at the foot of the well and the next piece enters.
        var landing = cartridge.GravityLanding;
        var settled = landing.Well.Count(predicate: static cell => (cell != 0));

        Assert.True(
            condition: (settled >= 4),
            userMessage: $"{settled} cells settled"
        );
        Assert.Equal(
            expected: 0,
            actual: landing.Over
        );
    }
    [Fact]
    public void APieceHeldDownRestsWhereGravityLandsItAndSooner() {
        // Soft drop changes only how fast the piece falls: it settles into exactly the cells gravity alone leaves it in,
        // in fewer frames, and the game goes on.
        var gravity = cartridge.GravityLanding;
        var held = cartridge.Land(
            buttons: JoypadButtons.Down,
            label: "tetromino-held"
        );

        Assert.Equal(
            expected: gravity.Well,
            actual: held.Well
        );
        Assert.True(
            condition: (held.Frames < gravity.Frames),
            userMessage: $"held {held.Frames} frames, gravity {gravity.Frames}"
        );
        Assert.Equal(
            expected: 0,
            actual: held.Over
        );
    }
    [Fact]
    public void APieceSpawnsAndFallsUnderGravity() {
        using var machine = cartridge.Boot(
            from: cartridge.Game,
            label: "tetromino"
        );

        Assert.Equal(
            expected: 0,
            actual: cartridge.Phase(machine: machine)
        );
        var first = machine.Read(address: cartridge.Variable(name: "py"));

        machine.RunFramesUntil(
            awaited: "the piece moving",
            buttons: JoypadButtons.None,
            limit: 120,
            until: running => (running.Read(address: cartridge.Variable(name: "py")) != first)
        );
        var later = machine.Read(address: cartridge.Variable(name: "py"));

        Assert.True(
            condition: (later > first),
            userMessage: $"row {first} then {later}"
        );
    }
    [Fact]
    public void AStackThatReachesTheCeilingEndsTheGame() {
        using var machine = cartridge.Boot(
            from: cartridge.Game,
            label: "tetromino-over"
        );

        FillCeiling(machine: machine);
        machine.RunFramesUntil(
            awaited: "the game over",
            buttons: JoypadButtons.None,
            limit: (60 * 6),
            until: running => (cartridge.Phase(machine: running) == GameOver)
        );
        Assert.Equal(
            expected: 1,
            actual: machine.Read(address: cartridge.Variable(name: "over"))
        );
        Assert.Equal(
            expected: GameOver,
            actual: cartridge.Phase(machine: machine)
        );
    }
    [Fact]
    public void EachScreenActuallyPutsItsWordsOnTheWell() {
        // The painter walks a cursor; a screen that inherits a spent cursor paints nothing and looks empty, which is
        // indistinguishable from a screen that has no words. Counting ink once each screen says it has painted is what
        // tells them apart.
        using var machine = cartridge.Boot(
            from: cartridge.Title,
            label: "tetromino-paint"
        );

        machine.RunFramesUntil(
            awaited: "the title's words on the well",
            buttons: JoypadButtons.None,
            limit: 30,
            until: running => (WellInk(machine: running) > 40)
        );
        var title = WellInk(machine: machine);

        Assert.True(
            condition: (title > 40),
            userMessage: $"title drew {title} lit pixels"
        );

        machine.Restore(snapshot: cartridge.Menu);
        machine.RunFramesUntil(
            awaited: "the menu's words on the well",
            buttons: JoypadButtons.None,
            limit: 30,
            until: running => (WellInk(machine: running) > 40)
        );
        var menu = WellInk(machine: machine);

        Assert.True(
            condition: (menu > 40),
            userMessage: $"menu drew {menu} lit pixels"
        );
    }
    [Fact]
    public void TheCartridgeHoldsFrameCadenceThroughLocksAndLineClears() {
        // Soft drop over a floor a bar short of full puts a lock and a line clear inside the span. The span is one run of
        // exact frames, because cadence is a rate over it.
        using var machine = cartridge.Boot(
            from: cartridge.Game,
            label: "tetromino-cadence"
        );

        FillFloorAroundTheBar(machine: machine);
        var started = machine.Read(address: cartridge.Variable(name: "ticks"));
        const int Frames = 240;

        machine.RunFrames(
            buttons: JoypadButtons.Down,
            frames: Frames
        );
        Assert.Equal(
            expected: 1,
            actual: machine.Read(address: cartridge.Variable(name: "lines"))
        );

        // The counter is a byte, so it wraps; the comparison is against the run's own wrapped expectation.
        var expected = ((byte)(started + Frames));
        var ticks = machine.Read(address: cartridge.Variable(name: "ticks"));

        Assert.InRange(
            actual: ticks,
            high: expected,
            low: ((byte)(expected - 4))
        );
    }
    [Fact]
    public void TheCartridgeOpensOnItsTitleAndWalksThroughTheModeMenuIntoAGame() {
        using var machine = cartridge.Boot(label: "tetromino-screens");

        // Boot lands somewhere in the title sequence, not in a game.
        machine.RunFrames(
            buttons: JoypadButtons.None,
            frames: 20
        );
        Assert.Contains(
            collection: TitlePhases,
            expected: cartridge.Phase(machine: machine)
        );

        // The sequence finishes on the phase that waits for a press.
        cartridge.AwaitTitle(machine: machine);
        Assert.Equal(
            expected: TetrominoCartridgeFixture.TitleWaits,
            actual: cartridge.Phase(machine: machine)
        );

        // Start leaves it, and the well is wiped on the way to the menu.
        cartridge.OpenMenu(machine: machine);
        Assert.Equal(
            expected: TetrominoCartridgeFixture.MenuPhase,
            actual: cartridge.Phase(machine: machine)
        );

        // The chooser moves between the two modes and the second one is remembered.
        machine.RunFrames(
            buttons: JoypadButtons.Down,
            frames: TetrominoCartridgeFixture.PressFrames
        );
        machine.RunFrames(
            buttons: JoypadButtons.None,
            frames: TetrominoCartridgeFixture.PressFrames
        );
        machine.RunFrames(
            buttons: JoypadButtons.Start,
            frames: TetrominoCartridgeFixture.PressFrames
        );
        cartridge.AwaitGame(machine: machine);
        Assert.Equal(
            expected: 1,
            actual: machine.Read(address: cartridge.Variable(name: "gtype"))
        );
        Assert.Equal(
            expected: 0,
            actual: cartridge.Phase(machine: machine)
        );

        // That mode starts under rubble, which is the whole of its problem.
        Assert.InRange(
            actual: Settled(machine: machine),
            high: ((9 * 5) + 4),
            low: (9 * 5)
        );
    }
    [Fact]
    public void TheDocumentValidatesAndEveryPrimitiveItUsesIsMeasured() {
        Assert.Empty(collection: CartridgeDocuments.Validate(document: cartridge.Document));

        // The estimate is advice, so nothing here compares it against the reservation: a cartridge that overruns a
        // frame now and then still plays, and a line clear pauses the game anyway. What it must say is that
        // every primitive the document uses was measured, so the advice is about this machine rather than a guess.
        // Whether the game actually keeps up is asked of the machine, in the cadence test.
        var (frame, _) = CartridgeDocuments.Estimate(document: cartridge.Document);
        Assert.True(
            condition: frame.IsKnown,
            userMessage: frame.Reason
        );
    }
    [Fact]
    public void WinningTheSecondModeReleasesTheBalloonAndReturnsToTheTitle() {
        using var machine = cartridge.Boot(
            from: cartridge.Menu,
            label: "tetromino-ending"
        );

        // Into the second mode: down on the menu picks it.
        machine.RunFrames(
            buttons: JoypadButtons.Down,
            frames: TetrominoCartridgeFixture.PressFrames
        );
        machine.RunFrames(
            buttons: JoypadButtons.Start,
            frames: TetrominoCartridgeFixture.PressFrames
        );
        cartridge.AwaitGame(machine: machine);
        Assert.Equal(
            expected: 1,
            actual: machine.Read(address: cartridge.Variable(name: "gtype"))
        );

        // Reaching the target is what wins it, so the count is set to the target under the running game.
        machine.Write(
            address: cartridge.Variable(name: "lines"),
            value: 25
        );
        machine.RunFramesUntil(
            awaited: "the balloon released",
            buttons: JoypadButtons.None,
            limit: 10,
            until: running => (cartridge.Phase(machine: running) == Balloon)
        );
        Assert.Equal(
            expected: Balloon,
            actual: cartridge.Phase(machine: machine)
        );

        // The balloon rises off the top, and the title comes back behind it.
        cartridge.AwaitTitle(machine: machine);
        Assert.Equal(
            expected: TetrominoCartridgeFixture.TitleWaits,
            actual: cartridge.Phase(machine: machine)
        );
    }

    private const byte Balloon = 11;
    private const byte GameOver = 9;
    // The well stands right of the panel, columns eight through seventeen; WellRight is one pixel past its last.
    private const int WellLeft = (8 * 8);
    private const ushort SoundStatus = 0xFF26;
    private const int WellRight = (18 * 8);

    // The three passes and the wait they settle on.
    private static readonly byte[] TitlePhases = [8, 16, 17, TetrominoCartridgeFixture.TitleWaits];
}
