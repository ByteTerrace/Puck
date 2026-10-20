using System.Globalization;

namespace Puck.AdvancedGamingBrick;

/// <summary>The ways an AGB program stops being able to run. Each is terminal: no firmware the machine runs returns
/// from it, and no cartridge reaches it on purpose.</summary>
public enum AgbExecutionFaultKind {
    /// <summary>The CPU took the undefined-instruction exception and sits in Undefined mode; the firmware's vector
    /// parks it there.</summary>
    UndefinedInstruction,
    /// <summary>The CPU is in Abort mode, which nothing on the machine raises; its state is corrupt.</summary>
    Abort,
    /// <summary>The CPU fetches from an address that holds no code: past the 16 KiB BIOS, I/O, backup memory or an
    /// unused region. Typical of a call into firmware that returned into nothing.</summary>
    UnmappedExecution,
}

/// <summary>
/// A hosted AGB core that can no longer run its program. <see cref="AdvancedGamingBrickCore"/> checks after every
/// cycle budget and throws this, so the queued worker stops on it and the host reports the machine as faulted with the
/// kind named, rather than <c>Running</c> on a dead screen. The check reads only emulated state, so a replay faults at
/// the same budget.
/// </summary>
public sealed class AgbExecutionFaultException : InvalidOperationException {
    /// <summary>Initializes a new instance of the <see cref="AgbExecutionFaultException"/> class for the CPU state a
    /// check observed.</summary>
    /// <param name="kind">The fault.</param>
    /// <param name="programCounter">The CPU's fetch address (R15).</param>
    /// <param name="cpsr">The CPU's status register.</param>
    public AgbExecutionFaultException(AgbExecutionFaultKind kind, uint programCounter, uint cpsr)
        : base(message: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{Name(kind: kind)} at pc=0x{programCounter:X8} cpsr=0x{cpsr:X8}"
        )) {
        Kind = kind;
        ProgramCounter = programCounter;
        Cpsr = cpsr;
    }

    /// <summary>Gets the fault.</summary>
    public AgbExecutionFaultKind Kind { get; }
    /// <summary>Gets the CPU's fetch address when the fault was observed.</summary>
    public uint ProgramCounter { get; }
    /// <summary>Gets the CPU's status register when the fault was observed.</summary>
    public uint Cpsr { get; }

    /// <summary>Gets whether <paramref name="address"/> lies in a region the CPU can fetch code from: the BIOS, both
    /// work RAMs, palette, VRAM, OAM and the three cartridge ROM windows.</summary>
    /// <param name="address">A bus address.</param>
    /// <returns><see langword="true"/> for an executable region.</returns>
    public static bool IsExecutable(uint address) {
        var region = (address >> 24);

        return (address < ReplacementBios.ImageSize)
            || ((region >= 0x02u) && (region <= 0x03u))
            || ((region >= 0x05u) && (region <= 0x07u))
            || ((region >= 0x08u) && (region <= 0x0Du));
    }
    /// <summary>Throws when <paramref name="cpu"/> sits in a state no program leaves.</summary>
    /// <param name="cpu">The CPU to inspect between instructions.</param>
    /// <exception cref="AgbExecutionFaultException">The CPU is in Undefined or Abort mode, or fetches from a region
    /// that holds no code.</exception>
    public static void ThrowIfFaulted(IArmCpu cpu) {
        ArgumentNullException.ThrowIfNull(argument: cpu);
        var cpsr = cpu.Cpsr;
        var pc = cpu.GetRegister(index: 15);
        AgbExecutionFaultKind? kind = (cpsr & 0x1Fu) switch {
            ((uint)CpuMode.Undefined) => AgbExecutionFaultKind.UndefinedInstruction,
            ((uint)CpuMode.Abort) => AgbExecutionFaultKind.Abort,
            _ => (IsExecutable(address: pc)
                ? null
                : AgbExecutionFaultKind.UnmappedExecution
            ),
        };

        if (kind is { } fault) {
            throw new AgbExecutionFaultException(
                cpsr: cpsr,
                kind: fault,
                programCounter: pc
            );
        }
    }

    private static string Name(AgbExecutionFaultKind kind) => kind switch {
        AgbExecutionFaultKind.UndefinedInstruction => "undefined-instruction",
        AgbExecutionFaultKind.Abort => "abort",
        _ => "unmapped-execution",
    };
}
