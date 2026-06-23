namespace Puck.GameBoyAdvance;

/// <summary>Specifies the non-volatile save backup a Game Boy Advance cartridge carries, detected from the
/// identifier string the developer libraries embed in the ROM.</summary>
public enum CartridgeBackup {
    /// <summary>No detectable save backup.</summary>
    None = 0,
    /// <summary>32&#160;KiB battery-backed static RAM, mapped 8-bit at 0x0E000000.</summary>
    Sram = 1,
    /// <summary>64&#160;KiB flash memory.</summary>
    Flash64 = 2,
    /// <summary>128&#160;KiB flash memory (two banks).</summary>
    Flash128 = 3,
    /// <summary>Serial EEPROM (512&#160;B or 8&#160;KiB), accessed over the cartridge bus.</summary>
    Eeprom = 4,
}
