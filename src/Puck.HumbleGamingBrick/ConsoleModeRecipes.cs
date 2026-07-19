namespace Puck.HumbleGamingBrick;

/// <summary>
/// The per-ROM "boot shim" recipes: which cached hardware-detection bytes to overwrite so a RUNNING GB-compatible game
/// switches onto the target model's own code path, no reboot. A dual-mode cartridge reads the console model once at
/// power-on (register A) and caches the answer; every render routine thereafter branches on that cache. Flipping the
/// emulated hardware alone leaves the game running its old code (a color game keeps writing color palettes a DMG PPU
/// ignores), so a live device swap also pokes the cache — then the game re-detects and re-renders in the new mode while
/// its progress in shared RAM is untouched. Flag addresses/values are found empirically with the differential-boot
/// finder (boot the same ROM as CGB and as DMG, diff work/high RAM, keep the byte that stays stably different). A ROM
/// with no recipe simply gets a presentation-only swap (the framebuffer re-interprets its old art) — coherent, but not
/// the game's authored other-mode art.
/// <para>
/// This is a fact about HARDWARE and a CARTRIDGE, not about any host — so it lives engine-side, beside the core that
/// applies it (<see cref="Machine.SwitchModel"/>), rather than in a demo. The table is empty today (no forged cartridge
/// caches a colour-detection flag), so a live swap is presentation-only across the board.
/// </para>
/// </summary>
internal static class ConsoleModeRecipes {
    // One cached detection byte: the value it holds when the game booted monochrome vs color.
    private readonly record struct ModeFlag(
        ushort Address,
        byte MonochromeValue,
        byte ColorValue
    );

    // Keyed by cartridge-header title PREFIX (Header.Title is the printable ASCII of 0x134-0x142). Empty by default: no
    // shipped cartridge caches a hardware-detection byte to shim, so a live swap is presentation-only (the framebuffer
    // re-interprets). Author an entry here — keyed by a cartridge's title prefix — if a cartridge ever caches a
    // colour-detection flag (say, one gated on a HRAM byte) and ships an authored other-mode render path to switch onto.
    private static readonly (string TitlePrefix, ModeFlag[] Flags)[] Recipes = [];

    /// <summary>Returns the flag pokes that flip <paramref name="title"/> onto <paramref name="target"/>'s code path, or
    /// an empty array when no recipe is known (the caller then does a presentation-only swap).</summary>
    /// <param name="title">The cartridge header title.</param>
    /// <param name="target">The model being switched to.</param>
    /// <returns>The pokes, or an empty array.</returns>
    public static ModePoke[] PokesFor(string title, ConsoleModel target) {
        foreach (var (prefix, flags) in Recipes) {
            if (!title.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            var color = target.SupportsColor();
            var pokes = new ModePoke[flags.Length];

            for (var index = 0; (index < flags.Length); index++) {
                pokes[index] = new ModePoke(
                    Address: flags[index].Address,
                    Value: (color ? flags[index].ColorValue : flags[index].MonochromeValue)
                );
            }

            return pokes;
        }

        return [];
    }

    /// <summary>Whether a known recipe exists for <paramref name="title"/> — the host distinguishes a real live retarget
    /// (the game re-renders natively) from a presentation-only re-interpretation when it echoes the swap.</summary>
    /// <param name="title">The cartridge header title.</param>
    public static bool HasRecipe(string title) {
        foreach (var (prefix, _) in Recipes) {
            if (title.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }
}
