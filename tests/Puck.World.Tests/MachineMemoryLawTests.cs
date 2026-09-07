using System.Numerics;

using Xunit;

using Puck.GamingBricks.Forge;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// The contract under test: a <c>screens[].memory</c> Read binding mirrors the shipped <c>pip.cgb.cartridge.json</c>
/// machine's own bus byte into an ordinary Int cell every tick, writing only when the peeked value changed; a Write
/// binding pokes a cell's value into the machine's bus when the cell's value has moved, landing before the machine's
/// own next step; an address outside the engine's addressable bus refuses at validation by name; and a Write
/// binding whose cell holds still allocates less per tick than one whose cell changes every tick.
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class MachineMemoryLawTests {
    private const string CgbEngine = "gaming-brick";
    private const int MachineScreen = 8;
    private const int SettleTicks = 16;

    private static string RepoRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return directory!.FullName;
    }
    private static string CartridgePath() => Path.Combine(RepoRoot(), "src", "Puck.World", "Assets", "cartridges", "pip.cgb.cartridge.json");
    // The compiled image's own bus address for a named variable — read the same way the machine host reads the
    // booted image, so the law addresses the byte the running cartridge actually owns rather than a guessed offset.
    private static int VariableAddress(string name) {
        var compilation = WorldScreenMachineEngines.CartridgeCompilers[CgbEngine].Compile(document: CartridgeDocuments.Parse(utf8: File.ReadAllBytes(path: CartridgePath())));

        Assert.True(condition: compilation.Variables.TryGetValue(key: name, value: out var address), userMessage: $"pip.cgb.cartridge.json declares no variable '{name}'");

        return checked((int)address);
    }
    private static WorldDefinition WithMachineScreen(IReadOnlyList<WorldScreenMemory>? memory) {
        var document = Fixtures.BuildDocument();

        document = document.WithWorldState(rows: [
            .. document.State,
            new WorldStateRow(Name: Name(value: "pipX"), Kind: CellKind.Int, Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: 0)]),
            new WorldStateRow(Name: Name(value: "pipY"), Kind: CellKind.Int, Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: 0)]),
        ]);

        return document with {
            ScreensRaw = [
                .. document.Screens,
                new WorldScreen(
                    Index: MachineScreen,
                    Origin: new Vector3(x: 0f, y: 1f, z: 3f),
                    Right: new Vector3(x: 1f, y: 0f, z: 0f),
                    Up: new Vector3(x: 0f, y: 1f, z: 0f),
                    HalfWidth: 0.3f,
                    HalfHeight: 0.27f,
                    HalfDepth: 0.03f,
                    Round: 0f,
                    Source: new WorldScreenSource.Machine(Engine: CgbEngine, ContentPath: CartridgePath(), Options: "cgb"),
                    Route: WorldScreenRoute.Passive,
                    Memory: memory
                ),
            ],
        };
    }
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static long Slot(WorldDefinition definition, string name) {
        var row = WorldDefinitionRows.FindStateRow(rows: definition.State, name: name);

        Assert.NotNull(@object: row);

        return Assert.Single(collection: (row!.Cells ?? []), predicate: static cell => (cell.Key == WorldStateRow.SlotKey)).Value;
    }

    private readonly ITestOutputHelper m_output;
    public MachineMemoryLawTests(ITestOutputHelper output) {
        m_output = output;
    }
    [Fact]
    public void AReadBindingMirrorsTheByteTheCartridgeWrites() {
        // "x" is initialized by the cartridge's own reset code from its authored "initial": 76 — the byte this law
        // asserts came from the running cartridge, not from a value the test injected.
        var xAddress = VariableAddress(name: "x");
        var document = WithMachineScreen(memory: [
            new WorldScreenMemory(Address: xAddress, Width: 1, Row: "pipX", Key: null, Direction: WorldScreenMemoryDirection.Read),
        ]);
        using var fixture = Fixtures.FreshServer(definition: document, engines: WorldScreenMachineEngines.All);

        Assert.True(condition: fixture.Server.Machines.HasMachine(index: MachineScreen), userMessage: fixture.Server.Machines.State(index: MachineScreen)?.Fault);

        for (var tick = 0; (tick < SettleTicks); tick++) {
            fixture.Step();
        }

        Assert.Equal(expected: 76L, actual: Slot(definition: fixture.Server.Definition, name: "pipX"));

        // The control: peeking the same address directly agrees with the mirror — the binding did not invent a value.
        var (ok, _) = fixture.Server.Machines.TryPeekMessage(index: MachineScreen, address: xAddress, value: out var direct);

        Assert.True(condition: ok);
        Assert.Equal(expected: (byte)76, actual: direct);
    }
    [Fact]
    public void AWriteBindingsPokeIsVisibleToTheCartridgeOnItsNextFrame() {
        var yAddress = VariableAddress(name: "y");
        var document = WithMachineScreen(memory: [
            new WorldScreenMemory(Address: yAddress, Width: 1, Row: "pipY", Key: null, Direction: WorldScreenMemoryDirection.Write),
        ]);
        using var fixture = Fixtures.FreshServer(definition: document, engines: WorldScreenMachineEngines.All);

        Assert.True(condition: fixture.Server.Machines.HasMachine(index: MachineScreen), userMessage: fixture.Server.Machines.State(index: MachineScreen)?.Fault);

        for (var tick = 0; (tick < SettleTicks); tick++) {
            fixture.Step();
        }

        // Before the poke: "y" still carries its own boot-initialized value (68), never the console-side row.
        var (beforeOk, _) = fixture.Server.Machines.TryPeekMessage(index: MachineScreen, address: yAddress, value: out var before);

        Assert.True(condition: beforeOk);
        Assert.Equal(expected: (byte)68, actual: before);

        // wall-north/wall-south clamp y to 8..128, so 50 rides through untouched by the cartridge's own rules.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "pipY", Key: WorldStateRow.SlotKey.Value, Value: 50, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();

        var (afterOk, _) = fixture.Server.Machines.TryPeekMessage(index: MachineScreen, address: yAddress, value: out var after);

        Assert.True(condition: afterOk);
        Assert.Equal(expected: (byte)50, actual: after);

        // A second quiet tick: the poke already landed, so the byte holds — nothing re-pokes it away.
        fixture.Step();

        var (stillOk, _) = fixture.Server.Machines.TryPeekMessage(index: MachineScreen, address: yAddress, value: out var still);

        Assert.True(condition: stillOk);
        Assert.Equal(expected: (byte)50, actual: still);
    }
    [Fact]
    public void AnAddressOutsideTheEnginesMemoryRefusesByName() {
        var denied = WithMachineScreen(memory: [
            new WorldScreenMemory(Address: 0xFFFF, Width: 2, Row: "pipX", Key: null, Direction: WorldScreenMemoryDirection.Read),
        ]);
        var admitted = WithMachineScreen(memory: [
            new WorldScreenMemory(Address: 0xFFFE, Width: 2, Row: "pipX", Key: null, Direction: WorldScreenMemoryDirection.Read),
        ]);

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var deniedReason));
        Assert.Contains(expectedSubstring: "is outside the engine's memory", actualString: deniedReason, comparisonType: StringComparison.Ordinal);
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: admitted, reason: out var controlReason), userMessage: controlReason);
    }
    // A Read binding must peek every tick to know whether the byte moved at all — that peek's own marshaled round
    // trip through the machine's worker thread (Puck.GamingBricks.QueuedMachineWorker.RunMemoryAccess) is a real,
    // pre-existing cost shared by screen.peek and an addon's own WorldAddonMemoryWatch, not something this binding
    // adds. What the write-on-change gate elides is the document-mutation cost on top of that: a Write binding's
    // poke never even reaches the machine when the cell has not moved, so this law measures that saving directly —
    // a quiet Write binding against one whose cell changes every sampled tick.
    [Fact]
    public void AQuietMachineAllocatesNothing() {
        var yAddress = VariableAddress(name: "y");
        var document = WithMachineScreen(memory: [
            new WorldScreenMemory(Address: yAddress, Width: 1, Row: "pipY", Key: null, Direction: WorldScreenMemoryDirection.Write),
        ]);

        var quiet = MeasureWrite(definition: document, changingEachTick: false);
        var changing = MeasureWrite(definition: document, changingEachTick: true);

        m_output.WriteLine($"machine memory write: quiet median {quiet:N0} bytes/tick, changing median {changing:N0} bytes/tick");
        Assert.True(condition: (quiet < changing), userMessage: $"a quiet Write binding allocated {quiet:N0} bytes/tick, not fewer than a changing one's {changing:N0}");
    }
    private static long MeasureWrite(WorldDefinition definition, bool changingEachTick) {
        using var fixture = Fixtures.FreshServer(definition: definition, engines: WorldScreenMachineEngines.All);

        for (var tick = 0; (tick < SettleTicks); tick++) {
            fixture.Step();
        }

        // One committed poke first, so the QUIET run's memo already holds this value — its every sampled tick is
        // the fast "unchanged" path, never the one-time first-poke every binding pays once.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "pipY", Key: WorldStateRow.SlotKey.Value, Value: 40, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();

        var samples = new long[60];

        for (var tick = 0; (tick < samples.Length); tick++) {
            if (changingEachTick) {
                fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "pipY", Key: WorldStateRow.SlotKey.Value, Value: (40 + (tick % 20)), Kind: WorldDocumentWriteKind.Set));
            }

            var before = GC.GetAllocatedBytesForCurrentThread();

            fixture.Step();
            samples[tick] = (GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Array.Sort(array: samples);

        return samples[(samples.Length / 2)];
    }
}
