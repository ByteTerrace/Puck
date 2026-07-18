using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// The CRITTER-SWAP cartridge's public face — the <see cref="Build"/>/<see cref="Verify"/> pair every framework game
/// facade carries (the forge CLI and the overworld cart-type table call these), plus the battery-save helpers the demo
/// uses to give two linked cabinets DIFFERENT starting critters. The cart is a two-player trading toy whose whole point
/// is the link cable; its self-verify (<see cref="Verify"/>) proves the swap over a real two-machine session before any
/// bytes are written.
/// </summary>
internal static class CritterSwapRom {
    /// <summary>The battery-save filename under the demo's local-state directory.</summary>
    public const string SaveFileName = "critterswap.sav";

    // The MBC1+RAM external RAM window is 8 KiB (header 0x0149 = 0x02); a seeded .sav is that raw image with the
    // framework save block at offset 0 (SRAM base 0xA000 maps to external-RAM offset 0).
    private const int ExternalRamByteCount = 0x2000;

    /// <summary>Assembles the CRITTER-SWAP <c>.gbc</c> (a genuine 32 KiB MBC1 Color image with battery-backed SRAM).</summary>
    /// <param name="title">The cartridge header title.</param>
    /// <returns>The ROM image.</returns>
    public static byte[] Build(string title = "CRITTERS") => CritterSwapGame.Build(title: title);

    /// <summary>Boots the ROM on real Humble machines and asserts the game's observable behaviour — the title renders a
    /// critter, START offers a trade, a two-machine link session swaps both critters and commits both SRAMs, and the
    /// whole scenario is replay-identical across two fresh runs (the forge's "verify by running" gate). Throws on any
    /// violation.</summary>
    /// <param name="rom">The ROM image to verify.</param>
    public static void Verify(byte[] rom) => CritterSwapVerify.Run(rom: rom);

    /// <summary>Resolves (and creates the directory for) the cart's default battery-save path.</summary>
    /// <returns>The save-file path.</returns>
    public static string PrepareDefaultSavePath() => RomForge.PrepareDefaultSavePath(saveFileName: SaveFileName);

    /// <summary>Builds a valid battery-save image (an external-RAM dump) holding one critter — the framework save block
    /// (<c>magic | version | species | level | sum16</c>) at offset 0, the rest zero. The demo writes these to seed a
    /// cabinet's distinct starting critter; the same layout the <see cref="SaveModule"/> reads on boot.</summary>
    /// <param name="species">The critter's species id.</param>
    /// <param name="level">The critter's packed-BCD level.</param>
    /// <returns>The 8 KiB external-RAM image.</returns>
    public static byte[] BuildSaveImage(byte species, byte level) {
        var image = new byte[ExternalRamByteCount];
        var checksum = (ushort)(species + level); // The framework save's additive sum16 over the payload bytes.

        image[0] = SaveModule.MagicLow;
        image[1] = SaveModule.MagicHigh;
        image[2] = CritterSwapProtocol.SaveVersion;
        image[3] = species;
        image[4] = level;
        image[5] = (byte)(checksum & 0xFF);
        image[6] = (byte)((checksum >> 8) & 0xFF);

        return image;
    }

    /// <summary>Seeds a cabinet's starting critter for a save SLOT — the demo's per-cabinet seam. Resolves the slot's own
    /// default species (<see cref="CritterSwapProtocol.DefaultSpeciesForSlot"/>) and its default level, then seeds the
    /// save at <paramref name="path"/> if none exists yet, so two linked cabinets on distinct slots hold DIFFERENT
    /// critters and the swap is visible. Never throws (see <see cref="SeedDefaultSave"/>).</summary>
    /// <param name="path">The (per-cabinet) save path.</param>
    /// <param name="slot">The cabinet's save slot (-1 = none → the slot-0 default critter).</param>
    public static void SeedDefaultSaveForSlot(string path, int slot) {
        var species = CritterSwapProtocol.DefaultSpeciesForSlot(slot: slot);

        SeedDefaultSave(path: path, species: species, level: CritterSwapProtocol.Species[species].Level);
    }

    /// <summary>Seeds a cabinet's starting critter by writing an initial battery save at <paramref name="path"/> IF none
    /// exists yet — so two linked cabinets (each with its own save slot) hold DIFFERENT critters and the swap is visible.
    /// A no-op when the file already exists (the player's own progress is never overwritten). Never throws — a seed that
    /// cannot be written just leaves the cabinet on the ROM default (narrated to stderr).</summary>
    /// <param name="path">The (per-cabinet) save path.</param>
    /// <param name="species">The starting species id.</param>
    /// <param name="level">The starting level (packed BCD).</param>
    public static void SeedDefaultSave(string path, byte species, byte level) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (File.Exists(path: path)) {
            return;
        }

        try {
            var directory = Path.GetDirectoryName(path: Path.GetFullPath(path: path));

            if (!string.IsNullOrEmpty(value: directory)) {
                _ = Directory.CreateDirectory(path: directory);
            }

            File.WriteAllBytes(path: path, bytes: BuildSaveImage(species: species, level: level));
        } catch (IOException exception) {
            Console.Error.WriteLine(value: $"[critterswap] could not seed the starter save '{path}' ({exception.Message}) — the cabinet boots the ROM default critter.");
        } catch (UnauthorizedAccessException exception) {
            Console.Error.WriteLine(value: $"[critterswap] could not seed the starter save '{path}' ({exception.Message}) — the cabinet boots the ROM default critter.");
        }
    }
}
