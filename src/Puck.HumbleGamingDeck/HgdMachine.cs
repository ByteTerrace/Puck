namespace Puck.HumbleGamingDeck;

/// <summary>The CPU/bus Deck driven one master tick at a time. PPU, APU, controllers, and host presentation are absent.</summary>
public sealed class HgdMachine : ISnapshotableMachine {
    private readonly HgdMachineIdentity m_identity;
    private readonly ISnapshotable[] m_components;

    private readonly string[] m_componentNames = ["clock", "cpu", "bus", "mapper"];
    private readonly StateWriter m_writer = new();

    /// <summary>Initializes a new instance of the <see cref="HgdMachine"/> class with independent mutable components.</summary>
    /// <param name="configuration">The image, model, and explicit power-up profile.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    public HgdMachine(HgdMachineConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(argument: configuration);

        Configuration = configuration;
        Clock = new(configuration: configuration);
        Bus = new(mapper: configuration.Cartridge.CreateMapper(), workRamFill: configuration.PowerOn.WorkRamFill);
        Cpu = new(bus: new HgdCpuBus(bus: Bus), model: configuration.Model);
        m_identity = HgdMachineIdentity.Compute(configuration: configuration);
        m_components = [Clock, Cpu, Bus, Bus.Mapper];
    }

    /// <summary>Gets the machine's immutable execution inputs.</summary>
    public HgdMachineConfiguration Configuration {
        get;
    }
    /// <summary>Gets the master clock.</summary>
    public HgdClock Clock {
        get;
    }
    /// <summary>Gets the CPU and its resumable microsequencer.</summary>
    public HgdCpu<HgdCpuBus> Cpu {
        get;
    }
    /// <summary>Gets the NES CPU address map.</summary>
    public HgdSystemBus Bus {
        get;
    }
    /// <summary>Gets the completed master-tick count.</summary>
    public ulong MasterTicks => Clock.MasterTicks;

    /// <summary>Advances a cumulative budget of master ticks; previous manual stepping overshoot is carried.</summary>
    /// <param name="masterTicks">The master-tick budget, independent of instruction boundaries.</param>
    /// <exception cref="OverflowException">The accumulated run target exceeds <see cref="ulong.MaxValue"/>.</exception>
    public void RunCycles(ulong masterTicks) {
        Clock.AddBudget(masterTicks: masterTicks);
        while (Clock.MasterTicks < Clock.RunTargetCycles) {
            StepMasterTick();
        }
    }
    /// <summary>Advances the reference clock one tick. M2 falling commits the CPU access before notifying the board.</summary>
    public void StepMasterTick() {
        var edge = Clock.StepTick();

        if (edge == HgdM2Edge.Falling) {
            Cpu.Irq = Bus.Mapper.Irq;
            Cpu.StepCycle();
            Bus.Mapper.ObserveM2(high: false, masterTick: Clock.MasterTicks);
        } else if (edge == HgdM2Edge.Rising) {
            Bus.Mapper.ObserveM2(high: true, masterTick: Clock.MasterTicks);
        }
    }
    /// <summary>Captures all components, including CPU latches and the master-clock phase.</summary>
    /// <returns>An independent snapshot with a complete section table.</returns>
    public HgdMachineSnapshot Snapshot() {
        m_writer.Reset();
        var sections = new SnapshotSection[m_components.Length];

        for (var index = 0; (index < m_components.Length); ++index) {
            var offset = m_writer.Length;

            m_components[index].SaveState(writer: m_writer);
            sections[index] = new(Name: m_componentNames[index], Offset: offset, Length: (m_writer.Length - offset));
        }

        return new(identity: m_identity, takenAt: MasterTicks,
            image: new SnapshotImage(data: m_writer.ToArray(), sections: sections));
    }
    /// <summary>Restores a capture whose image, model, profile, and layout match this machine.</summary>
    /// <param name="snapshot">The capture to restore.</param>
    /// <exception cref="ArgumentNullException">The snapshot is null.</exception>
    /// <exception cref="InvalidOperationException">Identity or serialized length does not match.</exception>
    /// <exception cref="InvalidDataException">The serialized clock phase or pacing target is invalid.</exception>
    public void Restore(HgdMachineSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(argument: snapshot);
        if (snapshot.Identity != m_identity) {
            throw new InvalidOperationException(message: "Deck snapshot identity does not match the configuration, model, power-on profile, or image hashes.");
        }
        var reader = snapshot.OpenReader();

        RestoreState(reader: reader);
        if (!reader.AtEnd) {
            throw new InvalidOperationException(message: "Deck snapshot contains unconsumed state bytes.");
        }
    }
    /// <inheritdoc/>
    public void SerializeState(StateWriter writer) {
        foreach (var component in m_components) {
            component.SaveState(writer: writer);
        }
    }
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The reader does not contain a complete machine state.</exception>
    /// <exception cref="InvalidDataException">The serialized clock phase or pacing target is invalid.</exception>
    public void RestoreState(StateReader reader) {
        foreach (var component in m_components) {
            component.LoadState(reader: reader);
        }
    }
}
