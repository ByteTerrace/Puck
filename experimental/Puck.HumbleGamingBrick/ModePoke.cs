namespace Puck.HumbleGamingBrick;

/// <summary>
/// One byte a live device swap writes into work/high RAM to flip a game's cached hardware-detection state onto the
/// TARGET model's code path — the "boot shim" for a running cartridge. A GB-compatible game reads the console model
/// once at power-on (register A) and caches the answer; every rendering routine thereafter branches on that cache, so
/// changing the emulated hardware alone leaves the game running its old code. A per-ROM recipe supplies the small set
/// of cached bytes to overwrite so the running game re-detects — position, party, and progress in shared RAM are
/// untouched, only the mode cache flips. Addresses outside work RAM (0xC000–0xFDFF incl. echo) and high RAM
/// (0xFF80–0xFFFE) are ignored: a swap never reaches into ROM, I/O, or VRAM.
/// </summary>
/// <param name="Address">The CPU-space address to write (work RAM or high RAM).</param>
/// <param name="Value">The byte to store — the value the game's own boot detection would have written for the target model.</param>
public readonly record struct ModePoke(
    ushort Address,
    byte Value
);
