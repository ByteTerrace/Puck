using System.Diagnostics;
using System.Globalization;
using System.Text;
using Puck.Assets;
using Puck.Scripting;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a real WebAssembly guest, compiled by the pinned Wasmtime, mounts through the addon handshake
/// and ticks; its store holds each linear memory to 256 pages, refusing a guest that grows past them and, as
/// MemoryLimit, one that declares more at instantiation in any memory, exported or not; and a guest that never returns
/// exhausts its per-tick fuel and faults as OutOfFuel, spending the same fuel on every run. A guest's out-of-bounds
/// access faults as MemoryOutOfBounds, and once a guest has run, a hardware fault in the host's own managed code, on a
/// thread that never ran a guest, is still an ordinary managed exception: the engine installs no signal handlers. Each
/// guest is WAT text the engine compiles here, so no prebuilt binary stands between the law and the runtime. The class
/// measures allocation, so it runs alone.
/// </summary>
[Collection(name: AllocationCollection.Name)]
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
    // Serves one module's bytes at one path, so a guest reaches the host through the real loader.
    private sealed class OneModule(string path, byte[] bytes) : IAssetSource {
        public bool Exists(string path1) => string.Equals(a: path1, b: path, comparisonType: StringComparison.Ordinal);
        public ReadOnlyMemory<byte> Read(string path1) => (Exists(path1: path1) ? bytes : throw new FileNotFoundException(message: path1));
    }

    private static ScriptingModuleInfo Load(ScriptingEngine engine, byte[] bytes) {
        var path = Path.GetFullPath(path: "guest.wasm");

        return new WasmModuleLoader(
            assetSource: new OneModule(bytes: bytes, path: path),
            engine: engine
        ).Load(path: path);
    }
    private static AddonInstance Mount(ScriptingEngine engine, string wat, long fuelPerTick = AddonAbi.DefaultFuelPerTick) {
        var instance = new AddonInstance(
            channelResolver: new NoChannels(),
            descriptor: new AddonDescriptor(
                Enabled: true,
                FuelPerTick: fuelPerTick,
                ModuleHash: null,
                ModulePath: "guest.wasm",
                Name: "guest"
            ),
            engine: engine,
            moduleInfo: Load(bytes: Encoding.UTF8.GetBytes(s: wat), engine: engine)
        );

        if (instance.State == AddonState.Enabled) {
            instance.Admit();
        }

        return instance;
    }

    // Loads from the last bytes of the 32-bit address space, far past its one page.
    private const string OutOfBounds = (("""
        (module
          (memory (export "memory") 1)
        """ + Handshake) + """
          (func (export "puck_on_tick") (param i32) (result i32)
            (drop (i32.load (i32.const -4)))
            (i32.const 0))
        )
        """);

    // An integer division fault raised by the processor in managed code, through operands the compiler cannot fold.
    private static Exception? DivisionFault() {
        try {
            _ = checked((long.Parse(s: "-9223372036854775808", provider: CultureInfo.InvariantCulture) / long.Parse(s: "-1", provider: CultureInfo.InvariantCulture)));

            return null;
        } catch (Exception fault) {
            return fault;
        }
    }
    private static string MemoryOf(int pages) => $$"""
        (module
          (memory (export "memory") {{pages}})
        {{Handshake}}
          (func (export "puck_on_tick") (param i32) (result i32) (i32.const 0))
        )
        """;
    // Exports a one-page memory the handshake reads, and holds a second memory of the given size it never exports.
    private static string UnexportedMemoryOf(int pages) => $$"""
        (module
          (memory (export "memory") 1)
          (memory $held {{pages}})
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
    public void AGuestHoldingAnUnexportedMemoryPastTheCeilingIsRefusedAtInstantiation() {
        // The ceiling bounds each memory a guest holds, not only the one it exports, and its refusal is named as such.
        using var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);
        using var atCeiling = Mount(engine: engine, wat: UnexportedMemoryOf(pages: 256));
        using var pastCeiling = Mount(engine: engine, wat: UnexportedMemoryOf(pages: 257));

        Assert.Equal(expected: AddonState.Enabled, actual: atCeiling.State);
        Assert.Equal(expected: AddonState.Faulted, actual: pastCeiling.State);
        Assert.Equal(expected: AddonFaultKind.MemoryLimit, actual: pastCeiling.Fault.Kind);
    }
    [Fact]
    public void AGuestDeclaringTheWholeAddressSpaceIsRefusedWithoutAllocatingIt() {
        // 65,536 pages is every byte a 32-bit memory can address, four gibibytes. The refusal is read from the binary,
        // so neither the managed heap nor the process's committed memory grows by anything near that.
        using var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);
        using var process = Process.GetCurrentProcess();
        var managedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var privateBefore = process.PrivateMemorySize64;
        using var guest = Mount(engine: engine, wat: MemoryOf(pages: 65_536));

        process.Refresh();

        Assert.Equal(expected: AddonState.Faulted, actual: guest.State);
        Assert.Equal(expected: AddonFaultKind.MemoryLimit, actual: guest.Fault.Kind);
        Assert.InRange(actual: (GC.GetTotalAllocatedBytes(precise: true) - managedBefore), high: (16L << 20), low: 0L);
        Assert.InRange(actual: (process.PrivateMemorySize64 - privateBefore), high: (256L << 20), low: long.MinValue);
    }
    [Fact]
    public void AMalformedMemorySectionIsRefusedByNameBeforeCompiling() {
        // A memory section of one entry whose limits flags carry a bit no proposal declares.
        using var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);
        byte[] module = [0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00, 0x05, 0x03, 0x01, 0x10, 0x01];
        var refusal = Assert.Throws<InvalidDataException>(testCode: () => Load(bytes: module, engine: engine));

        Assert.Contains(expectedSubstring: "memory section entry 0: its limits flags 0x10 are not declared", actualString: refusal.Message);
    }
    [Fact]
    public void AGuestReadingPastItsMemoryFaultsAsMemoryOutOfBounds() {
        using var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);
        using var guest = Mount(engine: engine, wat: OutOfBounds);
        var tick = guest.Tick(input: []);

        Assert.Equal(expected: AddonTickStatus.Faulted, actual: tick.Status);
        Assert.Equal(expected: AddonFaultKind.MemoryOutOfBounds, actual: tick.Fault.Kind);
    }
    [Fact]
    public async Task AHostHardwareFaultAfterAGuestRanIsAManagedException() {
        // With Wasmtime's signal handlers installed this fault aborts the whole process, so it runs in a child test host
        // and the law reads how that host exits.
        var info = new ProcessStartInfo(fileName: "dotnet") {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        info.ArgumentList.Add(item: typeof(AddonGuestLawTests).Assembly.Location);
        info.ArgumentList.Add(item: "--filter-method");
        info.ArgumentList.Add(item: $"{typeof(AddonGuestLawTests).FullName}.{nameof(HostFaultChildAsync)}");
        info.ArgumentList.Add(item: "--explicit");
        info.ArgumentList.Add(item: "only");

        using var child = (Process.Start(startInfo: info) ?? throw new InvalidOperationException(message: "The child test host did not start."));
        var output = child.StandardOutput.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
        var errors = child.StandardError.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);

        await child.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken);

        var printed = $"{await output}{await errors}";

        Assert.True(
            condition: (child.ExitCode == 0),
            userMessage: $"the child test host exited {child.ExitCode}:{Environment.NewLine}{printed[Math.Max(val1: 0, val2: (printed.Length - 2000))..]}"
        );
        Assert.Contains(actualString: printed, expectedSubstring: "total: 1");
        Assert.Contains(actualString: printed, expectedSubstring: "succeeded: 1");
    }
    // Runs only as the child of AHostHardwareFaultAfterAGuestRanIsAManagedException, which selects it explicitly. A guest
    // runs on this thread, then the processor raises an integer division fault in managed code on a thread that never
    // ran one, and on pool threads, each of which must surface as the managed exception it is.
    [Fact(Explicit = true)]
    public async Task HostFaultChildAsync() {
        using (var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic)) {
            using var guest = Mount(engine: engine, wat: Quiet);

            Assert.Equal(expected: AddonTickStatus.Ok, actual: guest.Tick(input: []).Status);
        }

        Exception? onAFreshThread = null;
        var fresh = new Thread(start: () => onAFreshThread = DivisionFault());

        fresh.Start();
        fresh.Join();

        var onPoolThreads = await Task.WhenAll(tasks: Enumerable.Range(count: 8, start: 0).Select(selector: _ => Task.Run(function: DivisionFault)));

        _ = Assert.IsType<OverflowException>(@object: onAFreshThread);
        Assert.All(collection: onPoolThreads, action: fault => Assert.IsType<OverflowException>(@object: fault));
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
