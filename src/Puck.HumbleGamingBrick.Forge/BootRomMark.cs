namespace Puck.HumbleGamingBrick.Forge;

/// <summary>The boot bitmap an emitted boot program scrolls in and hands off to.</summary>
/// <remarks>
/// A boot program wedges on a cartridge whose header bitmap is not the one it carries, so this is what decides which
/// cartridges an image will run. It is a substitution rather than an addition: the Color image has 0x700 bytes for its
/// code and tables and spends nearly all of them, with no room for a second 48-byte bitmap.
/// </remarks>
public enum BootRomMark {
    /// <summary>The bitmap the revision's own era cartridges carry.</summary>
    Era = 0,
    /// <summary>The bitmap a cartridge this repository forges carries.</summary>
    House = 1,
}
