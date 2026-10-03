using System.Text;
using Puck.Assets;
using Puck.Scripting;
using Wasmtime;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a real WebAssembly guest, compiled by the pinned Wasmtime, mounts through the addon handshake
/// and ticks; its store holds linear memory to 256 pages, refusing a guest that grows past them and one that declares
/// more at instantiation; and a guest that never returns exhausts its per-tick fuel and faults as OutOfFuel, spending
/// the same fuel on every run. Each guest is WAT text the engine compiles here, so no prebuilt binary stands between
/// the law and the runtime.
/// </summary>
public sealed class AddonGuestLawTests {
    // The smallest handshake a guest can pass: a request channel speaking one verb and its response channel (the pair
    // is one facility), an empty out ring, and an in ring of one cell, each in its own region of the first page.
    private const string Handshake = """
          (data (i32.const 0) "\02\00\01\00\00\00\00\00\00\00\00\00\00\00\00\00\03\00\00\00\00\00\00\00\00\00\00\00\00\00\00\00")
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_channels_ptr") (result i32) (i32.const 0))
          (func (export "puck_channels_count") (result i32) (i32.const 2))
          (func (export "puck_out_ptr") (result i32) (i32.const 64))
          (func (export "puck_out_cap") (result i32) (i32.const 0))
          (func (export "puck_in_ptr") (result i32) (i32.const 128))
          (func (export "puck_in_cap") (result i32) (i32.const 1))
        """;
    // Grows to exactly the ceiling on its first tick and one page past it on its second, trapping as Unreachable when
    // a grow is refused, so a refused grow is visible as a fault rather than a silent -1.
    private const string Grower = (("""
        (module
          (memory (export "memory") 1)
          (global $ticks (mut i32) (i32.const 0))
        """ + Handshake) + """
          (func (export "puck_on_tick") (param i32) (result i32)
            (global.set $ticks (i32.add (global.get $ticks) (i32.const 1)))
            (if (i32.eq
                  (memory.grow (select (i32.const 255) (i32.const 1) (i32.eq (global.get $ticks) (i32.const 1))))
                  (i32.const -1))
              (then unreachable))
            (i32.const 0))
        )
        """);
    private const string Quiet = (("""
        (module
          (memory (export "memory") 1)
        """ + Handshake) + """
          (func (export "puck_on_tick") (param i32) (result i32) (i32.const 0))
        )
        """);
    private const string Spinner = (("""
        (module
          (memory (export "memory") 1)
        """ + Handshake) + """
          (func (export "puck_on_tick") (param i32) (result i32)
            (loop $forever (br $forever))
            (i32.const 0))
        )
        """);

    private sealed class NoChannels : IAddonChannelResolver {
        public bool TryResolve(string name, out int ordinal, out AddonChannelValueShape shape) {
            ordinal = -1;
            shape = default;

            return false;
        }
    }

    private static AddonInstance Mount(ScriptingEngine engine, string wat, long fuelPerTick = AddonAbi.DefaultFuelPerTick) {
        var bytes = Encoding.UTF8.GetBytes(s: wat);
        var module = Module.FromText(
            engine: engine.Engine,
            name: "guest",
            text: wat
        );
        var instance = new AddonInstance(
            channelResolver: new NoChannels(),
            descriptor: new AddonDescriptor(
                Enabled: true,
                FuelPerTick: fuelPerTick,
                ModuleHash: null,
                ModulePath: "guest.wat",
                Name: "guest"
            ),
            engine: engine,
            moduleInfo: new ScriptingModuleInfo(
                ByteLength: bytes.Length,
                ContentHash: AssetContentHash.Compute(content: bytes),
                Module: module,
                Path: "guest.wat"
            )
        );

        if (instance.State == AddonState.Enabled) {
            instance.Admit();
        }

        return instance;
    }
    private static string MemoryOf(int pages) => $$"""
        (module
          (memory (export "memory") {{pages}})
        {{Handshake}}
          (func (export "puck_on_tick") (param i32) (result i32) (i32.const 0))
        )
        """;

    [Fact]
    public void AGuestThatPassesTheHandshakeMountsAndTicks() {
        using var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);
        using var guest = Mount(engine: engine, wat: Quiet);

        Assert.Equal(expected: AddonState.Enabled, actual: guest.State);

        var tick = guest.Tick(input: []);

        Assert.Equal(expected: AddonTickStatus.Ok, actual: tick.Status);
        Assert.Equal(expected: 0, actual: tick.CellCount);
        Assert.Equal(expected: AddonState.Enabled, actual: guest.State);
    }
    [Fact]
    public void AGuestGrowsToTheCeilingAndIsRefusedOnePagePastIt() {
        using var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);
        using var guest = Mount(engine: engine, wat: Grower);

        Assert.Equal(expected: AddonTickStatus.Ok, actual: guest.Tick(input: []).Status);

        var past = guest.Tick(input: []);

        Assert.Equal(expected: AddonTickStatus.Faulted, actual: past.Status);
        Assert.Equal(expected: AddonFaultKind.Unreachable, actual: past.Fault.Kind);
    }
    [Fact]
    public void AGuestDeclaringMoreThanTheCeilingIsRefusedAtInstantiation() {
        using var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);
        using var atCeiling = Mount(engine: engine, wat: MemoryOf(pages: 256));
        using var pastCeiling = Mount(engine: engine, wat: MemoryOf(pages: 257));

        Assert.Equal(expected: AddonState.Enabled, actual: atCeiling.State);
        Assert.Equal(expected: AddonState.Faulted, actual: pastCeiling.State);
        Assert.Equal(expected: AddonFaultKind.MemoryLimit, actual: pastCeiling.Fault.Kind);
    }
    [Fact]
    public void AGuestThatNeverReturnsRunsOutOfFuelAndSpendsTheSameFuelEveryRun() {
        using var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);
        var runs = new List<(AddonTickStatus Status, AddonFaultKind Kind, ulong Fuel)>();

        for (var run = 0; (run < 2); run++) {
            using var guest = Mount(engine: engine, fuelPerTick: 50_000L, wat: Spinner);
            var tick = guest.Tick(input: []);

            runs.Add(item: (tick.Status, tick.Fault.Kind, guest.LastFuelConsumed));
        }

        Assert.Equal(expected: (AddonTickStatus.Faulted, AddonFaultKind.OutOfFuel), actual: (runs[0].Status, runs[0].Kind));
        Assert.Equal(expected: runs[0], actual: runs[1]);
        Assert.Equal(expected: 50_000UL, actual: runs[0].Fuel);
    }
}
