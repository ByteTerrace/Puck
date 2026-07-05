using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The driver that advances a machine along one shared timeline. Timed components are ticked one CPU T-cycle at a time
/// through the <see cref="ComponentClock"/>, in domain-aware lockstep. With a bus master (the CPU) present, that
/// processor produces the timeline — <see cref="Run(ulong)"/> steps whole instructions until a cumulative T-cycle
/// target is reached (overshoot carried, so host pacing is drift-free), and the CPU itself drives the per-dot component
/// ticks as it executes each memory access. With no bus master the driver advances the component clock directly;
/// <see cref="StepTick"/> is then one CPU T-cycle.
/// <para>
/// The driver holds no emulated state beyond the clock and a pacing accumulator; all state lives in the components,
/// which is what lets <see cref="Snapshot"/> capture the machine completely.
/// </para>
/// </summary>
public sealed class Machine {
    private readonly ICpu? m_busMaster;
    private readonly ComponentClock m_componentClock;
    private readonly IKey1 m_key1;
    private readonly SystemMemory m_memory;
    private readonly IModeSwitchable[] m_modeSwitchables;
    private readonly ModelState m_modelState;
    private readonly ISnapshotable[] m_snapshotables;

    private ulong m_runTargetCycles;

    /// <summary>Assembles a machine from the clock and the services resolved for its scope.</summary>
    /// <param name="componentClock">The domain-aware per-T-cycle component clock.</param>
    /// <param name="modelState">The owner of the machine's live-swappable emulated model.</param>
    /// <param name="snapshotables">The state-bearing components to snapshot, in registration order.</param>
    /// <param name="modeSwitchables">The components whose model-capability gates a live swap re-pushes.</param>
    /// <param name="memory">The internal RAM, for the demote-to-monochrome bank fixup and the mode-recipe pokes.</param>
    /// <param name="key1">The speed switch, for the demote-to-monochrome double-speed drop.</param>
    /// <param name="busMasters">The bus master (CPU), if one is registered; the first is used.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public Machine(
        ComponentClock componentClock,
        ModelState modelState,
        IEnumerable<ISnapshotable> snapshotables,
        IEnumerable<IModeSwitchable> modeSwitchables,
        SystemMemory memory,
        IKey1 key1,
        IEnumerable<ICpu> busMasters
    ) {
        ArgumentNullException.ThrowIfNull(argument: componentClock);
        ArgumentNullException.ThrowIfNull(argument: modelState);
        ArgumentNullException.ThrowIfNull(argument: snapshotables);
        ArgumentNullException.ThrowIfNull(argument: modeSwitchables);
        ArgumentNullException.ThrowIfNull(argument: memory);
        ArgumentNullException.ThrowIfNull(argument: key1);
        ArgumentNullException.ThrowIfNull(argument: busMasters);

        ICpu? busMaster = null;

        foreach (var candidate in busMasters) {
            busMaster = candidate;

            break;
        }

        m_busMaster = busMaster;
        m_componentClock = componentClock;
        m_key1 = key1;
        m_memory = memory;
        m_modelState = modelState;
        m_modeSwitchables = [.. modeSwitchables];
        m_snapshotables = [.. snapshotables];
    }

    /// <summary>Gets the machine's master clock.</summary>
    public MasterClock Clock =>
        m_componentClock.Clock;
    /// <summary>Gets the current instant on the master timeline.</summary>
    public Tick Now =>
        m_componentClock.Clock.Now;
    /// <summary>Gets whether a bus master (CPU) drives this machine.</summary>
    public bool HasBusMaster =>
        (m_busMaster is not null);
    /// <summary>Gets the console model the machine is CURRENTLY emulating — the boot model until
    /// <see cref="SwitchModel"/> retargets it.</summary>
    public ConsoleModel Model =>
        m_modelState.Model;

    /// <summary>Re-pushes a model's capability gates into every switchable component (idempotent) — the fan-out a
    /// restore uses to re-derive gates from the snapshotted model, and the render/hardware half of a live swap.</summary>
    /// <param name="model">The model to gate for.</param>
    public void ApplyModel(ConsoleModel model) {
        foreach (var component in m_modeSwitchables) {
            component.ApplyModel(model: model);
        }
    }
    /// <summary>The LIVE device swap (the boot shim): retargets the running machine to <paramref name="model"/> WITHOUT
    /// a reboot. It re-gates every color-path component, and on a Color→monochrome demote repages the switchable RAM to
    /// its DMG-equivalent banks and drops double speed so the game's now-monochrome code addresses shared state and
    /// times correctly (the Color banks 2–7 / VRAM bank 1 survive un-paged, cartridge-move style). Finally it applies
    /// the per-ROM <paramref name="pokes"/> — the small set of cached hardware-detection bytes that flip a GB-compatible
    /// game onto the target model's own code path, so it re-renders natively. Progress in shared RAM is untouched. Call
    /// only between frames (the machine idle at an instruction boundary), never mid-step.</summary>
    /// <param name="model">The model to switch to.</param>
    /// <param name="pokes">The per-ROM detection-flag pokes for the target model (empty falls back to a bare capability
    /// flip — the game keeps its old code path, so the host should present a re-interpretation rather than expect
    /// native art).</param>
    public void SwitchModel(ConsoleModel model, ReadOnlySpan<ModePoke> pokes) {
        var demotesToMonochrome = (m_modelState.Model.SupportsColor() && !model.SupportsColor());

        m_modelState.Set(model: model);
        ApplyModel(model: model);

        if (demotesToMonochrome) {
            // Keep the game's shared state addressable and its timing sane after the color hardware seals off: repage to
            // the DMG-equivalent banks and force normal speed (KEY1 flag + the component clock's own derived copy, which
            // this unit re-syncs exactly as a snapshot restore does).
            m_memory.ForceDmgBanks();
            m_key1.ForceNormalSpeed();
            m_componentClock.IsDoubleSpeed = false;
        }

        foreach (var poke in pokes) {
            m_memory.PokeCpuByte(address: poke.Address, value: poke.Value);
        }
    }

    /// <summary>Advances the machine by exactly one CPU T-cycle (one dot at normal speed), ticking every component in
    /// domain-aware lockstep. This is the finest step for a component-driven machine; a machine with a bus master is
    /// instruction-atomic, so advance it with <see cref="StepInstruction"/> or <see cref="Run(ulong)"/> instead.</summary>
    public void StepTick() =>
        m_componentClock.AdvanceCpuTCycle();
    /// <summary>Executes exactly one instruction on the bus master, which drives the per-dot component ticks itself as
    /// it runs.</summary>
    /// <exception cref="InvalidOperationException">The machine has no bus master.</exception>
    public void StepInstruction() {
        if (m_busMaster is null) {
            throw new InvalidOperationException(message: "The machine has no bus master to step.");
        }

        m_busMaster.StepInstruction();

        // Keep the pacing target from lagging behind a directly stepped instruction, so a later Run does not replay an
        // already-elapsed budget.
        var elapsed = m_componentClock.Clock.CycleCount;

        if (m_runTargetCycles < elapsed) {
            m_runTargetCycles = elapsed;
        }
    }
    /// <summary>Advances the machine forward by a budget of T-cycles (dots) — the seam a host engine drives, handing in
    /// the exact integer T-cycle count its frame elapsed so pacing carries no floating-point drift.</summary>
    /// <param name="tCycles">The number of T-cycles to advance this call.</param>
    public void Run(ulong tCycles) {
        if (m_busMaster is null) {
            for (var remaining = tCycles; (remaining != 0UL); --remaining) {
                m_componentClock.AdvanceCpuTCycle();
            }

            return;
        }

        // Accumulate against a cumulative target rather than the current instant, so the overshoot of the last
        // instruction is absorbed by the next call instead of accreting into drift.
        m_runTargetCycles += tCycles;

        while (m_componentClock.Clock.CycleCount < m_runTargetCycles) {
            m_busMaster.StepInstruction();
        }
    }
    /// <summary>Captures the machine's entire mutable state at the current instant into a self-contained snapshot that
    /// aliases nothing live. Restore it into this machine to rewind, or into a fresh machine to fork a divergent run.</summary>
    /// <returns>The snapshot.</returns>
    public MachineSnapshot Snapshot() {
        var writer = new StateWriter();

        writer.WriteUInt64(value: Now.RawBits);

        foreach (var snapshotable in m_snapshotables) {
            snapshotable.SaveState(writer: writer);
        }

        return new MachineSnapshot(takenAt: Now, data: writer.ToArray());
    }
    /// <summary>Replaces this machine's entire state with a snapshot's, repositioning the clock and every component.</summary>
    /// <param name="snapshot">The snapshot to restore.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public void Restore(MachineSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(argument: snapshot);

        var reader = snapshot.OpenReader();

        m_componentClock.Clock.ResetTo(instant: Tick.FromRawBits(rawBits: reader.ReadUInt64()));

        foreach (var snapshotable in m_snapshotables) {
            snapshotable.LoadState(reader: reader);
        }

        // A correctly-ordered snapshot leaves the reader exactly at the end; a shortfall or overrun means a SaveState /
        // LoadState field-order drift that would otherwise be read as silently-wrong state — fault deterministically so a
        // byte difference stays a genuine divergence, never a misread.
        if (reader.Position != snapshot.Size) {
            throw new InvalidOperationException(
                message: "Snapshot restore consumed a different number of bytes than the snapshot holds; the save/load field order has drifted."
            );
        }

        // The model is snapshot state (ModelState loaded above), but each component caches its capability gate in a fast
        // field that is NOT in its own bytes; re-derive them all from the restored model so a restored live-swapped
        // machine resumes as the model it was running, not the model it booted from. Idempotent, and it also re-syncs
        // the component-clock speed no differently than the CPU's own KEY1 re-derive.
        ApplyModel(model: m_modelState.Model);

        // The pacing accumulator is not emulated state; reanchor it to the restored instant so a run after a rewind does
        // not lose or duplicate a budget.
        m_runTargetCycles = m_componentClock.Clock.CycleCount;
    }
}
