namespace Puck.HumbleGamingBrick;

/// <summary>The memory bank controller a cartridge carries, decoded from the header's cartridge-type byte. It selects
/// which banking logic the bus drives the cartridge through.</summary>
public enum MapperKind {
    /// <summary>No mapper: a flat 32&#160;KiB ROM with optional unbanked RAM.</summary>
    RomOnly = 0,
    /// <summary>The MBC1 mapper.</summary>
    Mbc1 = 1,
    /// <summary>The MBC2 mapper (with built-in 512×4-bit RAM).</summary>
    Mbc2 = 2,
    /// <summary>The MBC3 mapper (optionally with a real-time clock).</summary>
    Mbc3 = 3,
    /// <summary>The MBC5 mapper.</summary>
    Mbc5 = 5,
    /// <summary>The MBC7 mapper (two-axis accelerometer plus a 93LC56 serial EEPROM).</summary>
    Mbc7 = 7,
    /// <summary>The MMM01 multi-game menu mapper.</summary>
    Mmm01 = 8,
    /// <summary>The Hudson HuC1 mapper (banked RAM or infrared behind one window).</summary>
    HuC1 = 9,
    /// <summary>The Hudson HuC3 mapper (mode-selected window with a nibble-protocol real-time clock and infrared).</summary>
    HuC3 = 10,
    /// <summary>The Pocket Camera (MAC-GBD) mapper.</summary>
    PocketCamera = 11,
}
