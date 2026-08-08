using System.Text;

using Puck.Assets;
using Puck.Input;
using Puck.Scripting;

using Module = Wasmtime.Module;

namespace Puck.Post;

/// <summary>
/// The seventeen inline WAT addon fixtures the <see cref="ScriptingDeterminismStage"/> compiles through
/// <see cref="Module.FromText"/> — a zero-file, zero-tool corpus (the GlyphFixture posture) that exercises the
/// <c>puck.addon.v1</c> ABI. Each module lays its snapshot region at <see cref="SnapshotPtr"/>, its command region
/// at <see cref="CommandsPtr"/> with a capacity of <see cref="CommandsCapacity"/> records, and its source
/// declaration table at <see cref="SourcesPtr"/> — computed from the command region it follows, not hand-added —
/// matching the host's <see cref="AddonAbi"/> offsets. Every magic number the stage cross-checks in C# — the region
/// base pointers, every field offset a fixture writes into a command record or reads out of the snapshot, the
/// Q16.16 unit, the walker's target Z, and the NUL-padded source table slots — is interpolated from
/// <see cref="AddonAbi"/> (or one of the three base pointers declared once below), so the guest text and the host
/// expectation can never drift apart.
/// </summary>
internal static class ScriptingFixtures {
    /// <summary>The Q16.16 raw-bit target Z the walker fixture steers toward (≈ −6.6, the −Z cabinet wall).</summary>
    public const long WalkerTargetZ = -432538L;

    // The byte offset of the snapshot input region every fixture lays out. Guest-chosen (the host never assumes a
    // fixed location — it reads puck_snapshot_ptr back), but declared once here so every fixture's snapshot reads
    // interpolate the same base instead of repeating it.
    private const int SnapshotPtr = 0;

    // The byte offset of the command output region every fixture lays out — guest-chosen, placed past the
    // AddonAbi.SnapshotBytes-byte snapshot region starting at SnapshotPtr.
    private const int CommandsPtr = 64;

    // The number of 24-byte command slots every fixture reserves — guest-chosen, well under
    // AddonAbi.MaxCommandRecords.
    private const int CommandsCapacity = 8;

    // The byte offset of the source declaration table: past the CommandsCapacity x AddonAbi.CommandRecordBytes-byte
    // command region starting at CommandsPtr, computed rather than hand-added so it can never drift out of sync
    // with the region it follows.
    private const int SourcesPtr = (CommandsPtr + (CommandsCapacity * AddonAbi.CommandRecordBytes));

    /// <summary>Reports a version the host does not speak; the handshake must fault <see cref="AddonFaultKind.AbiMismatch"/>
    /// and the addon must never tick.</summary>
    public static readonly string AbiMismatch = $$"""
        (module
          (memory (export "memory") 1)
          (func (export "puck_abi_version") (result i32) (i32.const 999))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 0))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const {{CommandsCapacity}}))
          (func (export "puck_on_tick") (result i32) (i32.const 0)))
        """;

    /// <summary>Declares a zero command-slot capacity — but one valid source, so a phantom all-zero record would
    /// otherwise decode cleanly as a real activation on that source — and returns a record count of one,
    /// exceeding the declared cap; the tick-time count-vs-cap bound (checked directly by
    /// <see cref="AddonInstance"/>, before <see cref="AddonCommandReader"/> ever sees the records) must fault
    /// <see cref="AddonFaultKind.DecodeError"/>.</summary>
    public static readonly string BadCommandsCap = $$"""
        (module
          (memory (export "memory") 1)
          (data (i32.const {{SourcesPtr}}) "{{Slot(sourceId: InputSources.Gamepad.ButtonSouth)}}")
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const 0))
          (func (export "puck_on_tick") (result i32) (i32.const 1)))
        """;

    /// <summary>Declares <c>puck_on_tick</c> with the wrong arity (a spurious <c>i32</c> parameter); static validation
    /// must fault <see cref="AddonFaultKind.BadExport"/> before the module is instantiated.</summary>
    public static readonly string BadExport = $$"""
        (module
          (memory (export "memory") 1)
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 0))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const {{CommandsCapacity}}))
          (func (export "puck_on_tick") (param i32) (result i32) (i32.const 0)))
        """;

    /// <summary>Declares one import (a plausible-looking <c>sin</c> function) though a <c>puck.addon.v1</c> module
    /// must be self-contained; static validation must fault <see cref="AddonFaultKind.BadExport"/> before the module
    /// is instantiated, and the addon must never tick. This is the proof that the zero-import contract is actually
    /// enforced — not merely documented — regardless of how plausible the import looks; any import at all is
    /// refused, not just a wrong module name, unknown name, or wrong signature.</summary>
    public static readonly string BadImport = $$"""
        (module
          (import "env" "sin" (func $sin (param i64) (result i64)))
          (memory (export "memory") 1)
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 0))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const {{CommandsCapacity}}))
          (func (export "puck_on_tick") (result i32) (i32.const 0)))
        """;

    /// <summary>Declares one valid source and writes a record whose phase byte (<c>99</c>) names no
    /// <see cref="Puck.Commands.CommandPhase"/> member; <c>TryMapPhase</c>'s range guard must fault
    /// <see cref="AddonFaultKind.DecodeError"/>.</summary>
    public static readonly string BadPhase = $$"""
        (module
          (memory (export "memory") 1)
          (data (i32.const {{SourcesPtr}}) "{{Slot(sourceId: InputSources.Gamepad.ButtonSouth)}}")
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const {{CommandsCapacity}}))
          (func (export "puck_on_tick") (result i32)
            (i32.store16 (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.SourceIndex}}) (i32.const 0))
            (i32.store8 (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.Phase}}) (i32.const 99))
            (i32.const 1)))
        """;

    /// <summary>Declares one valid source and returns one otherwise-valid record whose <c>reserved1</c> field
    /// (record byte 4) is nonzero; the reserved-must-be-zero guard must fault <see cref="AddonFaultKind.DecodeError"/>.</summary>
    public static readonly string BadReserved = $$"""
        (module
          (memory (export "memory") 1)
          (data (i32.const {{SourcesPtr}}) "{{Slot(sourceId: InputSources.Gamepad.ButtonSouth)}}")
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const {{CommandsCapacity}}))
          (func (export "puck_on_tick") (result i32)
            (i32.store16 (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.SourceIndex}}) (i32.const 0))
            (i32.store8 (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.Phase}}) (i32.const 1))
            (i32.store (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.Reserved1}}) (i32.const 1))
            (i32.const 1)))
        """;

    /// <summary>Declares one valid source and returns one otherwise-valid record whose <c>reserved0</c> byte
    /// (record byte 3) is nonzero; the reserved-must-be-zero guard must fault <see cref="AddonFaultKind.DecodeError"/>.
    /// Distinct from <see cref="BadReserved"/>, which exercises <c>reserved1</c> at byte 4 — the two guards check
    /// different bytes and neither fixture can stand in for the other.</summary>
    public static readonly string BadReserved0 = $$"""
        (module
          (memory (export "memory") 1)
          (data (i32.const {{SourcesPtr}}) "{{Slot(sourceId: InputSources.Gamepad.ButtonSouth)}}")
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const {{CommandsCapacity}}))
          (func (export "puck_on_tick") (result i32)
            (i32.store16 (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.SourceIndex}}) (i32.const 0))
            (i32.store8 (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.Phase}}) (i32.const 1))
            (i32.store8 (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.Reserved0}}) (i32.const 1))
            (i32.const 1)))
        """;

    /// <summary>Declares the same recognized source id twice; the load-time duplicate-id guard must fault
    /// <see cref="AddonFaultKind.BadExport"/> naming the earlier slot, and the addon must never tick.</summary>
    public static readonly string BadSourceDuplicate = $$"""
        (module
          (memory (export "memory") 1)
          (data (i32.const {{SourcesPtr}}) "{{Slot(sourceId: InputSources.Gamepad.ButtonSouth)}}{{Slot(sourceId: InputSources.Gamepad.ButtonSouth)}}")
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 2))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const {{CommandsCapacity}}))
          (func (export "puck_on_tick") (result i32) (i32.const 0)))
        """;

    /// <summary>Declares one valid source but writes a command record whose <c>sourceIndex</c> (<c>42</c>) is
    /// past the declared table's end; the tick-time range guard must fault <see cref="AddonFaultKind.DecodeError"/>.</summary>
    public static readonly string BadSourceIndex = $$"""
        (module
          (memory (export "memory") 1)
          (data (i32.const {{SourcesPtr}}) "{{Slot(sourceId: InputSources.Gamepad.ButtonSouth)}}")
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const {{CommandsCapacity}}))
          (func (export "puck_on_tick") (result i32)
            (i32.store16 (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.SourceIndex}}) (i32.const 42))
            (i32.const 1)))
        """;

    /// <summary>Declares <c>gamepad.gyro</c> — a real, recognized <see cref="Puck.Input.InputSources"/> control this
    /// ABI cannot address (a three-component motion source, per <see cref="AddonSourceCatalog"/>'s remarks); the
    /// load-time unrecognized-id guard must fault <see cref="AddonFaultKind.BadExport"/>, and the addon must never
    /// tick.</summary>
    public static readonly string BadSourceUnknown = $$"""
        (module
          (memory (export "memory") 1)
          (data (i32.const {{SourcesPtr}}) "{{Slot(sourceId: InputSources.Gamepad.Gyro)}}")
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const {{CommandsCapacity}}))
          (func (export "puck_on_tick") (result i32) (i32.const 0)))
        """;

    /// <summary>Declares one source slot filled with 64 non-NUL bytes; the load-time terminator guard must fault
    /// <see cref="AddonFaultKind.BadExport"/>, and the addon must never tick.</summary>
    public static readonly string BadSourceUnterminated = $$"""
        (module
          (memory (export "memory") 1)
          (data (i32.const {{SourcesPtr}}) "{{UnterminatedSlot()}}")
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const {{CommandsCapacity}}))
          (func (export "puck_on_tick") (result i32) (i32.const 0)))
        """;

    /// <summary>Declares one Digital source (<c>gamepad.buttonSouth</c>) and returns one otherwise-valid record
    /// against it with a nonzero <c>valueY</c> — legal only for an Axis2D source; the shape guard must fault
    /// <see cref="AddonFaultKind.DecodeError"/>.</summary>
    public static readonly string BadValueY = $$"""
        (module
          (memory (export "memory") 1)
          (data (i32.const {{SourcesPtr}}) "{{Slot(sourceId: InputSources.Gamepad.ButtonSouth)}}")
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const {{CommandsCapacity}}))
          (func (export "puck_on_tick") (result i32)
            (i32.store16 (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.SourceIndex}}) (i32.const 0))
            (i32.store8 (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.Phase}}) (i32.const 1))
            (i64.store (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.ValueY}}) (i64.const {{AddonAbi.One}}))
            (i32.const 1)))
        """;

    /// <summary>Declares one Axis2D source (<c>gamepad.leftStick</c>) and emits one record against it each tick
    /// whose X flips between ±1 with the tick's parity — a pure function of the marshalled-in tick, so two runs
    /// must agree and the trace must differ from <see cref="Silent"/>.</summary>
    public static readonly string Echo = $$"""
        (module
          (memory (export "memory") 1)
          (data (i32.const {{SourcesPtr}}) "{{Slot(sourceId: InputSources.Gamepad.LeftStick)}}")
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const {{CommandsCapacity}}))
          (func (export "puck_on_tick") (result i32)
            (i32.store16 (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.SourceIndex}}) (i32.const 0))
            (i32.store8 (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.Phase}}) (i32.const 1))
            (i64.store (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.ValueX}})
              (select
                (i64.const -{{AddonAbi.One}})
                (i64.const {{AddonAbi.One}})
                (i32.wrap_i64 (i64.and (i64.load (i32.const {{SnapshotPtr + AddonAbi.SnapshotOffsets.Tick}})) (i64.const 1)))))
            (i64.store (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.ValueY}}) (i64.const 0))
            (i32.const 1)))
        """;

    /// <summary>A counted loop of exactly one thousand iterations returning zero records — a fixed instruction stream
    /// whose fuel cost is stable, so the fuel-boundary leg can straddle the exact budget where it halts.</summary>
    public static readonly string FuelBoundary = $$"""
        (module
          (memory (export "memory") 1)
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 0))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const {{CommandsCapacity}}))
          (func (export "puck_on_tick") (result i32)
            (local $i i32)
            (local.set $i (i32.const 1000))
            (loop $l
              (local.set $i (i32.sub (local.get $i) (i32.const 1)))
              (br_if $l (local.get $i)))
            (i32.const 0)))
        """;

    /// <summary>An unbounded loop that never returns; its fuel budget must halt it at the identical derived point on
    /// every run, trapping <see cref="AddonFaultKind.OutOfFuel"/>.</summary>
    public static readonly string Runaway = $$"""
        (module
          (memory (export "memory") 1)
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 0))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const {{CommandsCapacity}}))
          (func (export "puck_on_tick") (result i32)
            (loop $l (br $l))
            (unreachable)))
        """;

    /// <summary>A no-op that declares zero sources and returns zero records — the baseline-diff target proving the
    /// guest's output actually reaches the command trace.</summary>
    public static readonly string Silent = $$"""
        (module
          (memory (export "memory") 1)
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 0))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const {{CommandsCapacity}}))
          (func (export "puck_on_tick") (result i32) (i32.const 0)))
        """;

    /// <summary>Declares one Axis2D source (<c>gamepad.leftStick</c>), reads the marshalled-in local Z, and emits
    /// one record against it whose Y is <c>clamp(target − posZ, ±1)</c> — proving a position marshals in and a
    /// derived value marshals out. The stage recomputes the exact clamp with the same <see cref="WalkerTargetZ"/>
    /// and unit.</summary>
    public static readonly string Walker = $$"""
        (module
          (memory (export "memory") 1)
          (data (i32.const {{SourcesPtr}}) "{{Slot(sourceId: InputSources.Gamepad.LeftStick)}}")
          (func (export "puck_abi_version") (result i32) (i32.const 1))
          (func (export "puck_sources_ptr") (result i32) (i32.const {{SourcesPtr}}))
          (func (export "puck_sources_count") (result i32) (i32.const 1))
          (func (export "puck_snapshot_ptr") (result i32) (i32.const {{SnapshotPtr}}))
          (func (export "puck_commands_ptr") (result i32) (i32.const {{CommandsPtr}}))
          (func (export "puck_commands_cap") (result i32) (i32.const {{CommandsCapacity}}))
          (func (export "puck_on_tick") (result i32)
            (local $d i64)
            (local.set $d (i64.sub (i64.const {{WalkerTargetZ}}) (i64.load (i32.const {{SnapshotPtr + AddonAbi.SnapshotOffsets.PosLocalZ}}))))
            (local.set $d (select (i64.const {{AddonAbi.One}}) (local.get $d) (i64.gt_s (local.get $d) (i64.const {{AddonAbi.One}}))))
            (local.set $d (select (i64.const -{{AddonAbi.One}}) (local.get $d) (i64.lt_s (local.get $d) (i64.const -{{AddonAbi.One}}))))
            (i32.store16 (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.SourceIndex}}) (i32.const 0))
            (i32.store8 (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.Phase}}) (i32.const 1))
            (i64.store (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.ValueX}}) (i64.const 0))
            (i64.store (i32.const {{CommandsPtr + AddonAbi.RecordOffsets.ValueY}}) (local.get $d))
            (i32.const 1)))
        """;

    /// <summary>Compiles all seventeen fixtures against the engine, computing each module's content identity from
    /// its WAT bytes.</summary>
    /// <param name="engine">The deterministic engine the modules bind to.</param>
    /// <returns>The compiled fixture modules.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is <see langword="null"/>.</exception>
    public static ScriptingFixtureModules Compile(ScriptingEngine engine) {
        ArgumentNullException.ThrowIfNull(argument: engine);

        return new ScriptingFixtureModules(
            AbiMismatch: CompileText(engine: engine, name: "abi-mismatch", wat: AbiMismatch),
            BadCommandsCap: CompileText(engine: engine, name: "bad-commands-cap", wat: BadCommandsCap),
            BadExport: CompileText(engine: engine, name: "bad-export", wat: BadExport),
            BadImport: CompileText(engine: engine, name: "bad-import", wat: BadImport),
            BadPhase: CompileText(engine: engine, name: "bad-phase", wat: BadPhase),
            BadReserved: CompileText(engine: engine, name: "bad-reserved", wat: BadReserved),
            BadReserved0: CompileText(engine: engine, name: "bad-reserved0", wat: BadReserved0),
            BadSourceDuplicate: CompileText(engine: engine, name: "bad-source-duplicate", wat: BadSourceDuplicate),
            BadSourceIndex: CompileText(engine: engine, name: "bad-source-index", wat: BadSourceIndex),
            BadSourceUnknown: CompileText(engine: engine, name: "bad-source-unknown", wat: BadSourceUnknown),
            BadSourceUnterminated: CompileText(engine: engine, name: "bad-source-unterminated", wat: BadSourceUnterminated),
            BadValueY: CompileText(engine: engine, name: "bad-value-y", wat: BadValueY),
            Echo: CompileText(engine: engine, name: "echo", wat: Echo),
            FuelBoundary: CompileText(engine: engine, name: "fuel-boundary", wat: FuelBoundary),
            Runaway: CompileText(engine: engine, name: "runaway", wat: Runaway),
            Silent: CompileText(engine: engine, name: "silent", wat: Silent),
            Walker: CompileText(engine: engine, name: "walker", wat: Walker)
        );
    }

    // Renders one 64-byte NUL-padded source-declaration slot as WAT data-segment text: sourceId's ASCII bytes
    // followed by explicit "\00" escapes out to AddonAbi.SourceSlotBytes, computed from the id's actual length so
    // the guest table and the host's AddonSourceTableReader can never drift apart.
    private static string Slot(string sourceId) {
        var text = new StringBuilder(value: sourceId);

        for (var index = sourceId.Length; (index < AddonAbi.SourceSlotBytes); ++index) {
            text.Append(value: "\\00");
        }

        return text.ToString();
    }

    // A full 64-byte slot with no NUL byte anywhere — the "no NUL terminator" load-time guard fixture.
    private static string UnterminatedSlot() {
        return new string(c: 'X', count: AddonAbi.SourceSlotBytes);
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

/// <summary>The seventeen compiled <see cref="ScriptingDeterminismStage"/> fixture modules, in one immutable bundle.</summary>
/// <param name="AbiMismatch">The version-mismatch guard fixture.</param>
/// <param name="BadCommandsCap">The count-exceeds-cap tick-guard fixture.</param>
/// <param name="BadExport">The wrong-arity static-validation fixture.</param>
/// <param name="BadImport">The any-import-is-refused static-validation fixture.</param>
/// <param name="BadPhase">The out-of-range-phase decode-guard fixture.</param>
/// <param name="BadReserved">The reserved1-must-be-zero decode-guard fixture.</param>
/// <param name="BadReserved0">The reserved0-must-be-zero decode-guard fixture.</param>
/// <param name="BadSourceDuplicate">The duplicate-source-id load-guard fixture.</param>
/// <param name="BadSourceIndex">The out-of-range-sourceIndex tick-guard fixture.</param>
/// <param name="BadSourceUnknown">The unrecognized-source-id load-guard fixture.</param>
/// <param name="BadSourceUnterminated">The missing-NUL-terminator load-guard fixture.</param>
/// <param name="BadValueY">The nonzero-valueY-on-non-Axis2D decode-guard fixture.</param>
/// <param name="Echo">The tick-parity round-trip fixture.</param>
/// <param name="FuelBoundary">The counted-loop fuel-accounting fixture.</param>
/// <param name="Runaway">The unbounded-loop fuel-halt fixture.</param>
/// <param name="Silent">The no-op baseline fixture.</param>
/// <param name="Walker">The position-in / derived-value-out fixture.</param>
internal sealed record ScriptingFixtureModules(
    ScriptingModuleInfo AbiMismatch,
    ScriptingModuleInfo BadCommandsCap,
    ScriptingModuleInfo BadExport,
    ScriptingModuleInfo BadImport,
    ScriptingModuleInfo BadPhase,
    ScriptingModuleInfo BadReserved,
    ScriptingModuleInfo BadReserved0,
    ScriptingModuleInfo BadSourceDuplicate,
    ScriptingModuleInfo BadSourceIndex,
    ScriptingModuleInfo BadSourceUnknown,
    ScriptingModuleInfo BadSourceUnterminated,
    ScriptingModuleInfo BadValueY,
    ScriptingModuleInfo Echo,
    ScriptingModuleInfo FuelBoundary,
    ScriptingModuleInfo Runaway,
    ScriptingModuleInfo Silent,
    ScriptingModuleInfo Walker);
