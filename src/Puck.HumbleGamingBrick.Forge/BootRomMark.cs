namespace Puck.HumbleGamingBrick.Forge;

/// <summary>The cartridge-header logo policy of a Puck-branded boot program.</summary>
/// <remarks>
/// Logo checks are diagnostic compatibility policies, not authentication or a restricted-distribution boundary.
/// The displayed artwork belongs to the firmware and never comes from the cartridge header.
/// </remarks>
public enum BootRomMark {
    /// <summary>The bitmap the revision's own era cartridges carry.</summary>
    Era = 0,
    /// <summary>The bitmap a cartridge this repository forges carries.</summary>
    House = 1,
    /// <summary>Accepts any cartridge logo while retaining the revision's header-checksum validation.</summary>
    Compatible = 2,
}
