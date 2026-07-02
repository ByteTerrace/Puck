namespace Puck.AdvancedGamingBrick;

/// <summary>Specifies the ARM7TDMI processor mode, held in the low five bits of the CPSR. The mode selects
/// which banked registers are visible and whether privileged operations are permitted.</summary>
public enum CpuMode : uint {
    /// <summary>User mode: the unprivileged mode normal program code runs in.</summary>
    User = 0x10,
    /// <summary>Fast-interrupt mode, entered on FIQ; banks R8–R14 and SPSR.</summary>
    Fiq = 0x11,
    /// <summary>Interrupt mode, entered on IRQ; banks R13–R14 and SPSR.</summary>
    Irq = 0x12,
    /// <summary>Supervisor mode, entered on reset and <c>SWI</c>; banks R13–R14 and SPSR.</summary>
    Supervisor = 0x13,
    /// <summary>Abort mode, entered on a memory abort; banks R13–R14 and SPSR.</summary>
    Abort = 0x17,
    /// <summary>Undefined mode, entered on an undefined instruction; banks R13–R14 and SPSR.</summary>
    Undefined = 0x1B,
    /// <summary>System mode: privileged, but shares the User-mode register bank.</summary>
    System = 0x1F,
}
