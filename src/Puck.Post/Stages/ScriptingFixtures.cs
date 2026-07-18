using System.Text;

using Puck.Assets;
using Puck.Scripting;

using Module = Wasmtime.Module;

namespace Puck.Post;

/// <summary>
/// The nine inline WAT addon fixtures the <see cref="ScriptingDeterminismStage"/> compiles through
/// <see cref="Module.FromText"/> — a zero-file, zero-tool corpus (the GlyphFixture posture) that exercises the
/// frozen <c>puck.addon.v1</c> ABI. Each module lays its snapshot region at byte <c>0</c> and its command region at
/// byte <c>64</c> with a capacity of eight records, matching the host's <see cref="AddonAbi"/> offsets. The
/// magic numbers that the stage cross-checks in C# — the Q16.16 unit and the walker's target Z — are interpolated
/// from a single source so the guest text and the host expectation can never drift apart.
/// </summary>
internal static class ScriptingFixtures {
    /// <summary>The Q16.16 raw-bit target Z the walker fixture steers toward (≈ −6.6, the −Z cabinet wall).</summary>
    public const long WalkerTargetZ = -432538L;

    /// <summary>Reports a version the host does not speak; the handshake must fault <see cref="AddonFaultKind.AbiMismatch"/>
    /// and the addon must never tick.</summary>
    public const string AbiMismatch = """
        (module
          (memory (export "memory") 1)
          (func (export "puck_abi_version") (result i32) (i32.const 999))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const 0))
          (func (export "puck_commands_ptr") (result i32) (i32.const 64))
          (func (export "puck_commands_cap") (result i32) (i32.const 8))
          (func (export "puck_on_tick") (result i32) (i32.const 0)))
        """;

    /// <summary>Writes an unknown padId (42) into the one record it returns; the reader must fault
    /// <see cref="AddonFaultKind.DecodeError"/>.</summary>
    public const string BadDecode = """
        (module
          (memory (export "memory") 1)
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const 0))
          (func (export "puck_commands_ptr") (result i32) (i32.const 64))
          (func (export "puck_commands_cap") (result i32) (i32.const 8))
          (func (export "puck_on_tick") (result i32)
            (i32.store16 (i32.const 64) (i32.const 42))
            (i32.const 1)))
        """;

    /// <summary>Declares <c>puck_on_tick</c> with the wrong arity (a spurious <c>i32</c> parameter); static validation
    /// must fault <see cref="AddonFaultKind.BadExport"/> before the module is instantiated.</summary>
    public const string BadExport = """
        (module
          (memory (export "memory") 1)
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const 0))
          (func (export "puck_commands_ptr") (result i32) (i32.const 64))
          (func (export "puck_commands_cap") (result i32) (i32.const 8))
          (func (export "puck_on_tick") (param i32) (result i32) (i32.const 0)))
        """;

    /// <summary>Returns one otherwise-valid record whose <c>reserved1</c> field (record byte 4) is nonzero; the
    /// reserved-must-be-zero guard must fault <see cref="AddonFaultKind.DecodeError"/>.</summary>
    public const string BadReserved = """
        (module
          (memory (export "memory") 1)
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const 0))
          (func (export "puck_commands_ptr") (result i32) (i32.const 64))
          (func (export "puck_commands_cap") (result i32) (i32.const 8))
          (func (export "puck_on_tick") (result i32)
            (i32.store16 (i32.const 64) (i32.const 0))
            (i32.store8 (i32.const 66) (i32.const 1))
            (i32.store (i32.const 68) (i32.const 1))
            (i32.const 1)))
        """;

    /// <summary>A no-op that returns zero records — the baseline-diff target proving the guest's output actually
    /// reaches the command trace.</summary>
    public const string Silent = """
        (module
          (memory (export "memory") 1)
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const 0))
          (func (export "puck_commands_ptr") (result i32) (i32.const 64))
          (func (export "puck_commands_cap") (result i32) (i32.const 8))
          (func (export "puck_on_tick") (result i32) (i32.const 0)))
        """;

    /// <summary>An unbounded loop that never returns; its fuel budget must halt it at the identical derived point on
    /// every run, trapping <see cref="AddonFaultKind.OutOfFuel"/>.</summary>
    public const string Runaway = """
        (module
          (memory (export "memory") 1)
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const 0))
          (func (export "puck_commands_ptr") (result i32) (i32.const 64))
          (func (export "puck_commands_cap") (result i32) (i32.const 8))
          (func (export "puck_on_tick") (result i32)
            (loop $l (br $l))
            (unreachable)))
        """;

    /// <summary>A counted loop of exactly one thousand iterations returning zero records — a fixed instruction stream
    /// whose fuel cost is stable, so the fuel-boundary leg can straddle the exact budget where it halts.</summary>
    public const string FuelBoundary = """
        (module
          (memory (export "memory") 1)
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const 0))
          (func (export "puck_commands_ptr") (result i32) (i32.const 64))
          (func (export "puck_commands_cap") (result i32) (i32.const 8))
          (func (export "puck_on_tick") (result i32)
            (local $i i32)
            (local.set $i (i32.const 1000))
            (loop $l
              (local.set $i (i32.sub (local.get $i) (i32.const 1)))
              (br_if $l (local.get $i)))
            (i32.const 0)))
        """;

    /// <summary>Emits one <c>PadMove</c> record whose X flips between ±1 with the tick's parity — a pure function of the
    /// marshalled-in tick, so two runs must agree and the trace must differ from <see cref="Silent"/>.</summary>
    public static readonly string Echo = $$"""
        (module
          (memory (export "memory") 1)
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const 0))
          (func (export "puck_commands_ptr") (result i32) (i32.const 64))
          (func (export "puck_commands_cap") (result i32) (i32.const 8))
          (func (export "puck_on_tick") (result i32)
            (i32.store16 (i32.const 64) (i32.const 0))
            (i32.store8 (i32.const 66) (i32.const 1))
            (i64.store (i32.const 72)
              (select
                (i64.const -{{AddonAbi.One}})
                (i64.const {{AddonAbi.One}})
                (i32.wrap_i64 (i64.and (i64.load (i32.const 0)) (i64.const 1)))))
            (i64.store (i32.const 80) (i64.const 0))
            (i32.const 1)))
        """;

    /// <summary>Reads the marshalled-in local Z and emits one <c>PadMove</c> record whose Y is
    /// <c>clamp(target − posZ, ±1)</c> — proving a position marshals in and a derived value marshals out. The stage
    /// recomputes the exact clamp with the same <see cref="WalkerTargetZ"/> and unit.</summary>
    public static readonly string Walker = $$"""
        (module
          (memory (export "memory") 1)
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const 0))
          (func (export "puck_commands_ptr") (result i32) (i32.const 64))
          (func (export "puck_commands_cap") (result i32) (i32.const 8))
          (func (export "puck_on_tick") (result i32)
            (local $d i64)
            (local.set $d (i64.sub (i64.const {{WalkerTargetZ}}) (i64.load (i32.const 24))))
            (local.set $d (select (i64.const {{AddonAbi.One}}) (local.get $d) (i64.gt_s (local.get $d) (i64.const {{AddonAbi.One}}))))
            (local.set $d (select (i64.const -{{AddonAbi.One}}) (local.get $d) (i64.lt_s (local.get $d) (i64.const -{{AddonAbi.One}}))))
            (i32.store16 (i32.const 64) (i32.const 0))
            (i32.store8 (i32.const 66) (i32.const 1))
            (i64.store (i32.const 72) (i64.const 0))
            (i64.store (i32.const 80) (local.get $d))
            (i32.const 1)))
        """;

    /// <summary>Compiles all nine fixtures against the engine, computing each module's content identity from its WAT
    /// bytes.</summary>
    /// <param name="engine">The deterministic engine the modules bind to.</param>
    /// <returns>The compiled fixture modules.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is <see langword="null"/>.</exception>
    public static ScriptingFixtureModules Compile(ScriptingEngine engine) {
        ArgumentNullException.ThrowIfNull(argument: engine);

        return new ScriptingFixtureModules(
            AbiMismatch: CompileText(engine: engine, name: "abi-mismatch", wat: AbiMismatch),
            BadDecode: CompileText(engine: engine, name: "bad-decode", wat: BadDecode),
            BadExport: CompileText(engine: engine, name: "bad-export", wat: BadExport),
            BadReserved: CompileText(engine: engine, name: "bad-reserved", wat: BadReserved),
            Echo: CompileText(engine: engine, name: "echo", wat: Echo),
            FuelBoundary: CompileText(engine: engine, name: "fuel-boundary", wat: FuelBoundary),
            Runaway: CompileText(engine: engine, name: "runaway", wat: Runaway),
            Silent: CompileText(engine: engine, name: "silent", wat: Silent),
            Walker: CompileText(engine: engine, name: "walker", wat: Walker)
        );
    }

    private static ScriptingModuleInfo CompileText(ScriptingEngine engine, string name, string wat) {
        var bytes = Encoding.UTF8.GetBytes(s: wat);

        return new ScriptingModuleInfo(
            ByteLength: bytes.Length,
            ContentHash: AssetContentHash.Compute(content: bytes),
            Module: Module.FromText(engine: engine.Engine, name: name, text: wat),
            Path: name
        );
    }
}

/// <summary>The nine compiled <see cref="ScriptingDeterminismStage"/> fixture modules, in one immutable bundle.</summary>
/// <param name="AbiMismatch">The version-mismatch guard fixture.</param>
/// <param name="BadDecode">The unknown-padId decode-guard fixture.</param>
/// <param name="BadExport">The wrong-arity static-validation fixture.</param>
/// <param name="BadReserved">The reserved-must-be-zero decode-guard fixture.</param>
/// <param name="Echo">The tick-parity round-trip fixture.</param>
/// <param name="FuelBoundary">The counted-loop fuel-accounting fixture.</param>
/// <param name="Runaway">The unbounded-loop fuel-halt fixture.</param>
/// <param name="Silent">The no-op baseline fixture.</param>
/// <param name="Walker">The position-in / derived-value-out fixture.</param>
internal sealed record ScriptingFixtureModules(
    ScriptingModuleInfo AbiMismatch,
    ScriptingModuleInfo BadDecode,
    ScriptingModuleInfo BadExport,
    ScriptingModuleInfo BadReserved,
    ScriptingModuleInfo Echo,
    ScriptingModuleInfo FuelBoundary,
    ScriptingModuleInfo Runaway,
    ScriptingModuleInfo Silent,
    ScriptingModuleInfo Walker);
