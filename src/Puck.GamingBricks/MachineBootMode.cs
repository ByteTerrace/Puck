namespace Puck.GamingBricks;

/// <summary>Selects startup independently of the firmware image retained by a machine.</summary>
public enum MachineBootMode {
    /// <summary>Execute firmware from reset, including its native startup presentation.</summary>
    Cold,

    /// <summary>Start at the machine's seeded cartridge handoff. Firmware remains available for runtime services
    /// and snapshot identity; skipping startup does not select a different firmware image.</summary>
    Fast,
}
