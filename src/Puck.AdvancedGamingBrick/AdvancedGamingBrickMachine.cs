namespace Puck.AdvancedGamingBrick;

/// <summary>
/// A complete Advanced GamingBrick instance: the CPU bound to the system bus, resolved together from one DI scope
/// so a machine never shares stateful peripherals with another. Mirrors the public surface of the DMG/CGB
/// <c>HumbleGamingBrickMachine</c> — construct, boot, then step — so the conformance harness drives both cores the same way.
/// <para>
/// It also orchestrates the whole-machine savestate: <see cref="Snapshot"/> captures every state-bearing component's
/// <see cref="ISnapshotable"/> bytes into one flat, deterministic image, and <see cref="Restore"/> replays it —
/// the keystone primitive rewind, runahead, and rollback all reduce to. The driver itself holds no emulated state
/// beyond the component references, which is what lets a snapshot capture the machine completely.
/// </para>
/// </summary>
public sealed class AdvancedGamingBrickMachine {
    /// <summary>The address a cartridge begins executing from on the Advanced GamingBrick.</summary>
    public const uint CartridgeEntryPoint = 0x08000000u;

    /// <summary>Master cycles per frame (228 scanlines × 1232 dots/scanline).</summary>
    public const int CyclesPerFrame = (228 * 1232);

    private readonly IArmCpu m_cpu;
    private readonly IAgbBus m_bus;
    private readonly AgbBus? m_concreteBus;
    private readonly IAgbPpu m_ppu;
    private readonly IAgbApu m_apu;
    private readonly AgbScheduler m_scheduler;
    private readonly IAgbInterruptController m_interrupts;
    private readonly IAgbTimerController m_timers;
    private readonly IAgbDmaController m_dma;
    private readonly IAgbSerialController m_serial;
    private readonly AgbCartridge m_cartridge;
    private readonly AgbMachineIdentity m_identity;
    private readonly StateWriter m_stateWriter = new(capacity: 4096);
    private ISnapshotable[]? m_snapshotables;

    /// <summary>Creates the machine from its subsystems (all injected from the per-machine scope).</summary>
    /// <param name="cpu">The ARM7TDMI core, already bound to <paramref name="bus"/>.</param>
    /// <param name="bus">The system bus.</param>
    /// <param name="ppu">The picture-processing unit.</param>
    /// <param name="apu">The audio-processing unit.</param>
    /// <param name="scheduler">The event scheduler / master clock.</param>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <param name="timers">The timer block.</param>
    /// <param name="dma">The DMA block.</param>
    /// <param name="serial">The serial subsystem.</param>
    /// <param name="cartridge">The inserted cartridge.</param>
    /// <param name="bios">The system BIOS (identified pre-flight so callers can gate cycle-parity work; also the snapshot identity stamp).</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AdvancedGamingBrickMachine(
        IArmCpu cpu,
        IAgbBus bus,
        IAgbPpu ppu,
        IAgbApu apu,
        AgbScheduler scheduler,
        IAgbInterruptController interrupts,
        IAgbTimerController timers,
        IAgbDmaController dma,
        IAgbSerialController serial,
        AgbCartridge cartridge,
        IBios bios
    ) {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(ppu);
        ArgumentNullException.ThrowIfNull(apu);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(dma);
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(cartridge);
        ArgumentNullException.ThrowIfNull(bios);

        m_cpu = cpu;
        m_bus = bus;
        m_concreteBus = (bus as AgbBus);
        m_ppu = ppu;
        m_apu = apu;
        m_scheduler = scheduler;
        m_interrupts = interrupts;
        m_timers = timers;
        m_dma = dma;
        m_serial = serial;
        m_cartridge = cartridge;
        m_identity = AgbMachineIdentity.Compute(bios: bios.Image.Span, rom: cartridge.Rom);
        BiosIdentity = AgbBiosProfile.Identify(image: bios.Image.Span);
    }

    /// <summary>The pre-flight identity of the loaded BIOS image (its classification and content hash). Cycle-parity
    /// and co-simulation callers gate on <see cref="AgbBiosIdentity.IsCycleParityTrustworthy"/>; the demo surfaces
    /// <see cref="AgbBiosIdentity.Description"/> in its status line.</summary>
    public AgbBiosIdentity BiosIdentity { get; }

    /// <summary>Gets the CPU core.</summary>
    public IArmCpu Cpu => m_cpu;

    /// <summary>Gets the system bus.</summary>
    public IAgbBus Bus => m_bus;

    /// <summary>Gets the picture-processing unit.</summary>
    public IAgbPpu Ppu => m_ppu;

    /// <summary>Gets the audio-processing unit.</summary>
    public IAgbApu Apu => m_apu;

    /// <summary>Gets the identity a snapshot of this machine is stamped with (format version + BIOS/ROM fingerprint).</summary>
    public AgbMachineIdentity Identity => m_identity;

    /// <summary>Gets the current master-clock cycle counter.</summary>
    public long Cycles => m_scheduler.Now;

    /// <summary>Gets the most recent 240×160 frame as packed 0xAARRGGBB pixels.</summary>
    public ReadOnlySpan<uint> Framebuffer => m_ppu.Framebuffer;

    /// <summary>Boots straight into the cartridge, skipping the BIOS, with the standard post-BIOS machine state.</summary>
    public void DirectBoot() {
        m_cpu.SetupDirectBoot(entryPoint: CartridgeEntryPoint);
    }

    /// <summary>Sets the KEYINPUT register (active-low: clear bit = pressed). Bit layout: 0=A, 1=B, 2=Select,
    /// 3=Start, 4=Right, 5=Left, 6=Up, 7=Down, 8=R, 9=L.</summary>
    public void SetKeyInput(ushort keys) {
        m_concreteBus?.SetKeyInput(keys: keys);
    }

    /// <summary>Executes one instruction (or a pending exception entry).</summary>
    public void Step() {
        m_cpu.Step();
    }

    /// <summary>Runs the machine for one full frame (~280,896 master cycles). Returns the number of
    /// instructions executed.</summary>
    public int RunFrame() {
        return RunCycles(cycles: CyclesPerFrame);
    }

    /// <summary>Advances the machine by an exact master-cycle budget — the seam a host engine drives, handing in the
    /// precise cycle count its frame's tick budget bought so emulated time tracks the deterministic tick accumulator
    /// rather than the produced-frame cadence (the Advanced GamingBrick analogue of HumbleGamingBrick's
    /// <c>Machine.Run</c>). Steps whole instructions until the master clock has advanced at least
    /// <paramref name="cycles"/> further; the final instruction's small overshoot is itself deterministic — identical
    /// on every replay of the same budget sequence — so it drifts no state between runs. A non-positive budget, or a
    /// bare test bus with no master clock, steps nothing.</summary>
    /// <param name="cycles">The master-cycle budget to advance this call.</param>
    /// <returns>The number of instructions executed.</returns>
    public int RunCycles(long cycles) {
        if ((m_concreteBus is null) || (cycles <= 0L)) {
            return 0;
        }

        var target = (m_concreteBus.Cycles + cycles);
        var steps = 0;

        while (m_concreteBus.Cycles < target) {
            m_cpu.Step();
            ++steps;
        }

        return steps;
    }

    /// <summary>Captures the machine's entire mutable state at the current instant into a self-contained snapshot that
    /// aliases nothing live. Restore it into this machine to rewind. A component's own <see cref="ISnapshotable"/>
    /// serializes its state; the scheduler's master clock is written first, so a restore repositions the clock before
    /// the peripherals re-arm their events.</summary>
    /// <returns>The snapshot.</returns>
    /// <exception cref="NotSupportedException">A subsystem does not implement <see cref="ISnapshotable"/> (an
    /// exotic test composition, e.g. a flat-memory or tracing bus).</exception>
    public AgbMachineSnapshot Snapshot() {
        var order = EnsureSnapshotables();

        m_stateWriter.Reset();

        // The section table records each component's byte range as it is written — metadata riding alongside the
        // identical serialized bytes, so a divergence localizer can map a raw byte offset back to the component (and,
        // for a fixed-layout component, a sub-region within it) that owns it.
        var sections = new SnapshotSection[(order.Length + 1)];
        var offset = 0;

        m_scheduler.SaveState(writer: m_stateWriter);
        sections[0] = new SnapshotSection(Name: "scheduler", Offset: offset, Length: (m_stateWriter.Length - offset));
        offset = m_stateWriter.Length;

        for (var index = 0; (index < order.Length); ++index) {
            order[index].SaveState(writer: m_stateWriter);
            sections[(index + 1)] = new SnapshotSection(Name: s_snapshotableNames[index], Offset: offset, Length: (m_stateWriter.Length - offset));
            offset = m_stateWriter.Length;
        }

        return new AgbMachineSnapshot(identity: m_identity, takenAt: m_scheduler.Now, image: new SnapshotImage(data: m_stateWriter.ToArray(), sections: sections));
    }

    /// <summary>Serializes the machine's entire mutable state into a writer, scheduler first, then each component in the
    /// fixed order — but without the section table or a materialized snapshot image. The zero-copy producer half of a
    /// pooled fork: the sibling reads it straight back through <see cref="RestoreState"/>.</summary>
    /// <param name="writer">The sink to serialize into.</param>
    /// <exception cref="NotSupportedException">A subsystem does not implement <see cref="ISnapshotable"/>.</exception>
    public void SerializeState(StateWriter writer) {
        var order = EnsureSnapshotables();

        m_scheduler.SaveState(writer: writer);

        foreach (var component in order) {
            component.SaveState(writer: writer);
        }
    }

    /// <summary>Reads the machine's entire mutable state back from a reader positioned at the start of a serialized
    /// image, repositioning the master clock (scheduler first, so peripherals re-arm onto a clean event queue) and every
    /// component — the shared body of both <see cref="Restore"/> and a pooled fork. It performs no identity check
    /// (callers that need one check before calling) and does not validate exact consumption (the snapshot restore path
    /// does).</summary>
    /// <param name="reader">The source to read state from.</param>
    /// <exception cref="NotSupportedException">A subsystem does not implement <see cref="ISnapshotable"/>.</exception>
    public void RestoreState(StateReader reader) {
        var order = EnsureSnapshotables();

        m_scheduler.LoadState(reader: reader);

        foreach (var component in order) {
            component.LoadState(reader: reader);
        }
    }

    /// <summary>Replaces this machine's entire state with a snapshot's, repositioning the master clock and every
    /// component. Rejects a snapshot whose machine identity (format version / BIOS / ROM) does not match this
    /// machine, and faults if the restore does not consume the snapshot exactly — either signals a save/load field
    /// drift or a mismatched image rather than silently loading wrong state.</summary>
    /// <param name="snapshot">The snapshot to restore.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The snapshot's identity does not match this machine, or the restore
    /// consumed a different number of bytes than the snapshot holds.</exception>
    /// <exception cref="NotSupportedException">A subsystem does not implement <see cref="ISnapshotable"/>.</exception>
    public void Restore(AgbMachineSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(argument: snapshot);

        if (snapshot.Identity != m_identity) {
            throw new InvalidOperationException(
                message: "Snapshot identity (format version / BIOS / ROM) does not match this machine; refusing to restore a mismatched image."
            );
        }

        var reader = snapshot.OpenReader();

        // The scheduler restores the master clock and empties the live event queue first, so each peripheral's
        // LoadState re-arms its own scheduled event onto a clean slate (a callback is never serialized).
        RestoreState(reader: reader);

        // A correctly-ordered snapshot leaves the reader exactly at the end; a shortfall or overrun means a
        // SaveState/LoadState field-order drift that would otherwise be read as silently-wrong state — fault
        // deterministically so a byte difference stays a genuine divergence, never a misread.
        if (!reader.AtEnd) {
            throw new InvalidOperationException(
                message: "Snapshot restore consumed a different number of bytes than the snapshot holds; the save/load field order has drifted."
            );
        }
    }

    // The section-table names for EnsureSnapshotables()'s order, one-to-one by index ("scheduler" itself is recorded
    // separately in Snapshot(), since it is saved before this array is walked). Keep this in lockstep with
    // EnsureSnapshotables() — an entry added/reordered there without a matching edit here mislabels a section.
    private static readonly string[] s_snapshotableNames = ["cpu", "interrupts", "timers", "dma", "serial", "ppu", "apu", "bus", "cartridge"];

    // The state-bearing components in a fixed save/restore order (the scheduler is handled explicitly first). Built
    // lazily and cached: an exotic composition that never snapshots (a flat-memory or tracing bus) never pays the
    // cast, and only faults here if it actually asks for a snapshot.
    private ISnapshotable[] EnsureSnapshotables() {
        return m_snapshotables ??= [
            AsSnapshotable(component: m_cpu),
            AsSnapshotable(component: m_interrupts),
            AsSnapshotable(component: m_timers),
            AsSnapshotable(component: m_dma),
            AsSnapshotable(component: m_serial),
            AsSnapshotable(component: m_ppu),
            AsSnapshotable(component: m_apu),
            AsSnapshotable(component: m_bus),
            m_cartridge,
        ];
    }
    private static ISnapshotable AsSnapshotable(object component) =>
        ((component as ISnapshotable)
            ?? throw new NotSupportedException(
                message: $"Component '{component.GetType().Name}' does not implement {nameof(ISnapshotable)}; whole-machine snapshot is unavailable for this composition."
            ));
}
