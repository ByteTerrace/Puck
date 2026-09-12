namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Runs original call fixtures against two images; exposes function results, caller-owned writable memory,
/// and explicitly selected I/O results.</summary>
internal sealed class FirmwareOracleComparison : IDisposable {
    private readonly AdvancedGamingBrickCore m_puck;
    private readonly AdvancedGamingBrickCore m_retail;
    private readonly List<string> m_examples = [];
    private readonly SortedDictionary<byte, int> m_failuresByService = [];
    private int m_cases;
    private int m_mismatches;
    private int m_bothUnreturned;
    private int m_nonreturnObservations;

    /// <summary>Creates two independent native machines without copying any retail firmware instructions into tests.</summary>
    /// <param name="retail">The caller-provided, verified retail BIOS image.</param>
    public FirmwareOracleComparison(byte[] retail) {
        m_puck = FirmwareSwiProbe.Run(number: 8, thumb: false, r0: 0);
        try {
            m_retail = FirmwareSwiProbe.Run(number: 8, thumb: false, r0: 0, bios: retail);
        } catch {
            m_puck.Dispose();
            throw;
        }
    }

    /// <summary>Compares only named return registers, with an explicit mask for documented sixteen-bit results.</summary>
    /// <param name="number">The native SWI number.</param>
    /// <param name="thumb">Whether the caller invokes SWI in Thumb state.</param>
    /// <param name="r0">The first input register.</param>
    /// <param name="registers">The documented output register indices to compare.</param>
    /// <param name="r1">The second input register.</param>
    /// <param name="mask">The meaningful result bits, excluding unspecified upper bits when applicable.</param>
    public void Registers(byte number, bool thumb, uint r0, int[] registers, uint r1 = 0, uint mask = uint.MaxValue) {
        var label = $"SWI {number:X2} {(thumb ? "Thumb" : "ARM")} in={r0:X8},{r1:X8}";
        if (!Call(number: number, thumb: thumb, r0: r0, r1: r1, r2: 0, r3: 0, setup: null, label: label)) {
            return;
        }
        foreach (var register in registers) {
            var expected = m_retail.Instance.Machine.Cpu.GetRegister(index: register) & mask;
            var actual = m_puck.Instance.Machine.Cpu.GetRegister(index: register) & mask;
            if (actual != expected) {
                Mismatch(number: number, detail: $"{label} r{register}: Puck={actual:X8}, retail={expected:X8}");
                return;
            }
        }
    }

    /// <summary>Observes the specifically classified zero-divisor boundary without treating finite nonreturn as an output-value pass.</summary>
    /// <param name="number">Div or DivArm.</param>
    /// <param name="thumb">Whether the caller invokes SWI in Thumb state.</param>
    /// <param name="r0">The first input register.</param>
    /// <param name="r1">The second input register.</param>
    /// <exception cref="ArgumentException">The inputs are outside the explicitly classified nonreturn boundary.</exception>
    public void NonreturningDivision(byte number, bool thumb, uint r0, uint r1) {
        var numerator = (int)(number == 6 ? r0 : r1);
        var denominator = number == 6 ? r1 : r0;
        if (number is not (6 or 7) || denominator != 0 || numerator is >= -1 and <= 1) {
            throw new ArgumentException(message: "Only the explicit zero-divisor, magnitude-greater-than-one boundary admits bounded nonreturn observations.");
        }
        ++m_nonreturnObservations;
        var retail = ObserveNonreturn(core: m_retail, number: number, thumb: thumb, r0: r0, r1: r1);
        var puck = ObserveNonreturn(core: m_puck, number: number, thumb: thumb, r0: r0, r1: r1);
        if (retail is not null || puck is not null) {
            Mismatch(number: number, detail: $"SWI {number:X2} {(thumb ? "Thumb" : "ARM")} bounded zero divisor in={r0:X8},{r1:X8}: Puck {puck ?? "remained responsive inside BIOS"}; retail {retail ?? "remained responsive inside BIOS"}");
        }
    }

    /// <summary>Compares the documented output extent and adjacent writable canaries, never protected BIOS memory.</summary>
    /// <param name="number">The native SWI number.</param>
    /// <param name="thumb">Whether the caller invokes SWI in Thumb state.</param>
    /// <param name="source">An original source fixture placed in caller-owned EWRAM.</param>
    /// <param name="outputLength">The documented destination extent in bytes, including stride gaps.</param>
    /// <param name="name">A descriptive fixture label for bounded failure reports.</param>
    /// <param name="r2">The third input register.</param>
    /// <param name="r3">The fourth input register.</param>
    /// <param name="info">Optional bit-unpack information placed at the shared probe's Info address.</param>
    /// <param name="vram">Whether the destination is VRAM instead of EWRAM.</param>
    public void Memory(byte number, bool thumb, byte[] source, int outputLength, string name, uint r2 = 0, uint r3 = 0, byte[]? info = null, bool vram = false) {
        var destination = vram ? 0x06000020u : FirmwareSwiProbe.Destination;
        var label = $"SWI {number:X2} {(thumb ? "Thumb" : "ARM")} {name}";
        var comparedLength = outputLength + 32;
        void Setup(AdvancedGamingBrickMachine machine) {
            FirmwareSwiProbe.WriteBytes(bus: machine.Bus, address: FirmwareSwiProbe.Source, bytes: source);
            if (info is not null) {
                FirmwareSwiProbe.WriteBytes(bus: machine.Bus, address: FirmwareSwiProbe.Info, bytes: info);
            }
            for (var index = 0; index < comparedLength; index += 2) {
                machine.Bus.Write16(address: destination - 16 + (uint)index, value: 0xCCCC, access: BusAccessType.NonSequential);
            }
        }
        if (!Call(number: number, thumb: thumb, r0: FirmwareSwiProbe.Source, r1: destination, r2: r2, r3: r3, setup: Setup, label: label)) {
            return;
        }
        for (var index = 0; index < comparedLength; ++index) {
            var address = destination - 16 + (uint)index;
            var expected = m_retail.Instance.Machine.Bus.Read8(address: address, access: BusAccessType.NonSequential);
            var actual = m_puck.Instance.Machine.Bus.Read8(address: address, access: BusAccessType.NonSequential);
            if (actual != expected) {
                Mismatch(number: number, detail: $"{label} output offset {index - 16}: Puck={actual:X2}, retail={expected:X2}");
                return;
            }
        }
    }

    /// <summary>Compares the SoundBias direction result without comparing ramp timing or scratch registers.</summary>
    /// <param name="thumb">Whether the caller invokes SWI in Thumb state.</param>
    /// <param name="initial">The caller-selected initial bias and output-resolution bits.</param>
    /// <param name="flag">Zero for a downward ramp, otherwise an upward ramp.</param>
    public void SoundBias(bool thumb, ushort initial, uint flag) {
        const uint Address = 0x04000088;
        var label = $"SWI 19 {(thumb ? "Thumb" : "ARM")} initial={initial:X4}, flag={flag:X8}";
        if (!Call(number: 0x19, thumb: thumb, r0: flag, r1: 0, r2: 0, r3: 0,
            setup: machine => machine.Bus.Write16(address: Address, value: initial, access: BusAccessType.NonSequential), label: label)) {
            return;
        }
        var expected = m_retail.Instance.Machine.Bus.Read16(address: Address, access: BusAccessType.NonSequential);
        var actual = m_puck.Instance.Machine.Bus.Read16(address: Address, access: BusAccessType.NonSequential);
        if (actual != expected) {
            Mismatch(number: 0x19, detail: $"{label} SOUNDBIAS: Puck={actual:X4}, retail={expected:X4}");
        }
    }

    /// <summary>Reports a bounded set of diagnostic results, without exporting either firmware image.</summary>
    /// <returns>A functional compatibility result, explicitly excluding timing and unspecified clobbers.</returns>
    public PostStageOutcome Outcome() {
        var summary = $"{m_cases} ARM/Thumb returning-result cases; {m_nonreturnObservations} separate bounded zero-divisor observations (2,000,000 instructions, BIOS confinement, advancing cycles and snapshot replay; not infinite-behavior or timing proof); {m_mismatches} mismatches; {m_bothUnreturned} unexpected joint timeouts; documented return registers, writable RAM and SOUNDBIAS only, no scratch-register claim";
        if (m_mismatches != 0) {
            var groups = string.Join(separator: ", ", values: m_failuresByService.Select(selector: pair => $"SWI {pair.Key:X2}={pair.Value}"));
            return PostStageOutcome.Fail(detail: $"{summary}; {groups}; {string.Join(separator: "; ", values: m_examples)}");
        }
        return m_bothUnreturned == 0
            ? PostStageOutcome.Pass(detail: summary)
            : PostStageOutcome.Fail(detail: $"{summary}; bounded calls that did not return remain unresolved; {string.Join(separator: "; ", values: m_examples)}");
    }

    /// <inheritdoc/>
    public void Dispose() {
        m_puck.Dispose();
        m_retail.Dispose();
    }

    private bool Call(byte number, bool thumb, uint r0, uint r1, uint r2, uint r3, Action<AdvancedGamingBrickMachine>? setup, string label) {
        ++m_cases;
        var retailReturned = TryCall(core: m_retail, number: number, thumb: thumb, r0: r0, r1: r1, r2: r2, r3: r3, setup: setup);
        var puckReturned = TryCall(core: m_puck, number: number, thumb: thumb, r0: r0, r1: r1, r2: r2, r3: r3, setup: setup);
        if (retailReturned && puckReturned) {
            return true;
        }
        if (retailReturned != puckReturned) {
            Mismatch(number: number, detail: $"{label}: Puck {(puckReturned ? "returned" : "exceeded 2,000,000 instructions")}, retail {(retailReturned ? "returned" : "exceeded 2,000,000 instructions")}");
        } else {
            ++m_bothUnreturned;
            if (m_examples.Count < 10) { m_examples.Add(item: $"{label}: neither call returned within 2,000,000 instructions"); }
        }
        return false;
    }

    private static bool TryCall(AdvancedGamingBrickCore core, byte number, bool thumb, uint r0, uint r1, uint r2, uint r3, Action<AdvancedGamingBrickMachine>? setup, Action<AdvancedGamingBrickMachine>? afterStep = null) {
        try {
            FirmwareSwiProbe.Call(core: core, number: number, thumb: thumb, r0: r0, r1: r1, r2: r2, r3: r3, setup: setup, afterStep: afterStep);
            return true;
        } catch (InvalidOperationException exception) when (exception.Message.Contains(value: "did not return", comparisonType: StringComparison.Ordinal)) {
            return false;
        }
    }

    private static string? ObserveNonreturn(AdvancedGamingBrickCore core, byte number, bool thumb, uint r0, uint r1) {
        var start = core.CycleCount;
        var enteredBios = false;
        var escapedBios = false;
        void Observe(AdvancedGamingBrickMachine machine) {
            var inBios = machine.Cpu.GetRegister(index: 15) < ReplacementBios.ImageSize;
            // The first ARM/Thumb cartridge trampoline is legitimate; confinement starts at SWI entry.
            escapedBios |= enteredBios && !inBios;
            enteredBios |= inBios;
        }
        if (TryCall(core: core, number: number, thumb: thumb, r0: r0, r1: r1, r2: 0, r3: 0, setup: null, afterStep: Observe)) {
            return "unexpectedly completed the caller";
        }
        if (!enteredBios || escapedBios || core.CycleCount <= start) {
            return "failed BIOS confinement or clock advance";
        }
        var checkpoint = core.Instance.Machine.Snapshot();
        core.RunCycles(cycles: 4096);
        if (core.CycleCount <= checkpoint.TakenAt || core.Instance.Machine.Cpu.GetRegister(index: 15) >= ReplacementBios.ImageSize) {
            return "did not remain responsive inside BIOS after the observation budget";
        }
        var future = core.Instance.Machine.Snapshot();
        core.Instance.Machine.Restore(snapshot: checkpoint);
        if (!core.Instance.Machine.Snapshot().Data.SequenceEqual(other: checkpoint.Data)) {
            return "could not restore its bounded observation state";
        }
        core.RunCycles(cycles: 4096);
        if (!core.Instance.Machine.Snapshot().Data.SequenceEqual(other: future.Data)) {
            return "did not replay its bounded observation state identically";
        }
        return null;
    }

    private void Mismatch(byte number, string detail) {
        ++m_mismatches;
        m_failuresByService.TryGetValue(key: number, value: out var count);
        m_failuresByService[number] = count + 1;
        if (m_examples.Count < 10) { m_examples.Add(item: detail); }
    }
}
