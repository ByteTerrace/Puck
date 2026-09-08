namespace Puck.HumbleGamingBrick;

/// <summary>The console a hardware revision belongs to. A family shares one SoC design and one boot ROM; the revisions
/// within it differ only in the stepping-level details the capability questions on
/// <see cref="ConsoleModelExtensions"/> name.</summary>
public enum ConsoleFamily {
    /// <summary>The original monochrome GamingBrick.</summary>
    Dmg = 0,
    /// <summary>The pocket monochrome GamingBrick, whose boot ROM differs from <see cref="Dmg"/> in one byte.</summary>
    Mgb = 1,
    /// <summary>The monochrome GamingBrick that plays through a companion console's video hardware.</summary>
    Sgb = 2,
    /// <summary>The second revision of that companion console's cartridge, whose boot ROM differs from
    /// <see cref="Sgb"/> in one byte.</summary>
    Sgb2 = 3,
    /// <summary>The colour GamingBrick.</summary>
    Cgb = 4,
    /// <summary>The Advanced GamingBrick playing a GamingBrick cartridge through its Color-compatible hardware.</summary>
    Agb = 5,
    /// <summary>The folding Advanced GamingBrick, which carries the same SoC as <see cref="Agb"/> in a different
    /// package.</summary>
    Ags = 6,
}
/// <summary>
/// The hardware revision a machine emulates. A revision — not a costume — is the unit here, because the behaviour that
/// differs between two consoles is usually a stepping-level difference within one family rather than a whole
/// generation: the pixel fetcher's row latch changed at CPU CGB D, the boot ROM's wave-RAM initialization appeared at
/// CPU CGB A, and each family hands the cartridge a different post-boot register file. Components ask the named
/// capability questions on <see cref="ConsoleModelExtensions"/> rather than comparing against a member, so a gate reads
/// as the hardware fact it depends on.
/// </summary>
/// <remarks>The members are ordered oldest-to-newest so a "this revision and later" capability can be written as a
/// single comparison. The value is serialized as one byte in <see cref="ModelState"/> and stamped into
/// <see cref="MachineIdentity"/>.</remarks>
public enum ConsoleModel {
    /// <summary>DMG-CPU (revision 0), the early monochrome stepping whose boot ROM is rearranged and lacks the
    /// registered-trademark glyph.</summary>
    Dmg0 = 0,
    /// <summary>DMG-CPU B.</summary>
    DmgB = 1,
    /// <summary>DMG-CPU C, the monochrome stepping the accuracy work targets.</summary>
    DmgC = 2,
    /// <summary>MGB-CPU, the pocket console's SoC. Its boot ROM leaves <c>0xFF</c> in A where the DMG leaves
    /// <c>0x01</c>.</summary>
    Mgb = 3,
    /// <summary>SGB-CPU, the companion-console cartridge's SoC. Its boot ROM performs no header checks and forwards
    /// the header to the companion console instead.</summary>
    Sgb = 4,
    /// <summary>SGB2-CPU. Its boot ROM leaves <c>0xFF</c> in A where <see cref="Sgb"/> leaves <c>0x01</c>.</summary>
    Sgb2 = 5,
    /// <summary>CPU CGB (revision 0), the early colour stepping whose boot ROM does not initialize wave RAM.</summary>
    Cgb0 = 6,
    /// <summary>CPU CGB A.</summary>
    CgbA = 7,
    /// <summary>CPU CGB B.</summary>
    CgbB = 8,
    /// <summary>CPU CGB C.</summary>
    CgbC = 9,
    /// <summary>CPU CGB D, the stepping from which the background fetcher latches its row at the tile step.</summary>
    CgbD = 10,
    /// <summary>CPU CGB E, the colour stepping the accuracy work targets.</summary>
    CgbE = 11,
    /// <summary>CPU AGB, playing a GamingBrick cartridge through its Color-compatible hardware. Its boot ROM hands off
    /// the Color state after one extra <c>inc b</c> — the register difference cartridges probe to detect Advanced
    /// hardware.</summary>
    Agb = 12,
    /// <summary>CPU AGB in the folding console's package. Emulated identically to <see cref="Agb"/>; it exists so a
    /// conformance case tagged for that console names the hardware it was verified on.</summary>
    Ags = 13,
}
/// <summary>The capability questions components ask of a revision, so a gate reads as the hardware fact it depends on
/// rather than an equality against one model. Each question documents the fact it answers.</summary>
public static class ConsoleModelExtensions {
    /// <summary>Returns the console family a revision belongs to — the unit that shares one SoC design and one boot
    /// ROM.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns>The family.</returns>
    public static ConsoleFamily Family(this ConsoleModel model) =>
        model switch {
            ConsoleModel.Dmg0 or ConsoleModel.DmgB or ConsoleModel.DmgC => ConsoleFamily.Dmg,
            ConsoleModel.Mgb => ConsoleFamily.Mgb,
            ConsoleModel.Sgb => ConsoleFamily.Sgb,
            ConsoleModel.Sgb2 => ConsoleFamily.Sgb2,
            ConsoleModel.Agb => ConsoleFamily.Agb,
            ConsoleModel.Ags => ConsoleFamily.Ags,
            _ => ConsoleFamily.Cgb,
        };
    /// <summary>Returns the stepping number within the family: <c>0</c> for a revision-0 SoC, and otherwise the
    /// revision letter's position in the alphabet (A is 1, E is 5). Families with a single known stepping return
    /// <c>0</c>.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns>The stepping number.</returns>
    public static int Stepping(this ConsoleModel model) =>
        model switch {
            ConsoleModel.DmgB or ConsoleModel.CgbB => 2,
            ConsoleModel.DmgC or ConsoleModel.CgbC => 3,
            ConsoleModel.CgbA => 1,
            ConsoleModel.CgbD => 4,
            ConsoleModel.CgbE => 5,
            _ => 0,
        };
    /// <summary>Returns whether the revision has Color hardware: palette RAM, the VRAM DMA unit, the speed switch, and
    /// the Color I/O block. True for every <c>CPU CGB</c> stepping and for the Advanced console, which plays
    /// GamingBrick cartridges on Color-compatible silicon.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> when the revision has Color hardware.</returns>
    public static bool SupportsColor(this ConsoleModel model) =>
        (model >= ConsoleModel.Cgb0);
    /// <summary>Returns whether the boot ROM hands the cartridge the Advanced console's register file: the Color
    /// handoff after one extra <c>inc b</c>, which is the single register difference a cartridge probes to detect
    /// Advanced hardware. The same instruction costs one machine cycle, which is why the Advanced post-boot divider
    /// runs four T-cycles ahead of the Color one.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> for the Advanced console's two packages.</returns>
    public static bool HasAgbBootHandoff(this ConsoleModel model) =>
        (model >= ConsoleModel.Agb);
    /// <summary>Returns whether the revision is a companion-console cartridge. Its boot ROM skips the header checks,
    /// forwards the header to the companion console, and hands off with the audio unit silent.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> for both companion-console revisions.</returns>
    /// <remarks>Only the cartridge-side SoC is emulated: the boot handoff, the divider phase, and the register
    /// identity. The companion console's own command processing — the packet protocol that drives its borders,
    /// palettes, and multiplayer input — is not modelled, so a cartridge that sends those packets sees them accepted
    /// and dropped.</remarks>
    public static bool IsSuperGameBoy(this ConsoleModel model) =>
        (model is (ConsoleModel.Sgb or ConsoleModel.Sgb2));
    /// <summary>Returns whether the background fetcher latches its fetch row once, at the tile step, instead of
    /// re-deriving it live at each data step. CPU CGB D introduced the latch, so a mid-fetch write to the vertical
    /// scroll register lands on the data steps of older silicon but not on newer.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> from CPU CGB D onward.</returns>
    public static bool LatchesFetchRowAtTileStep(this ConsoleModel model) =>
        (model >= ConsoleModel.CgbD);
    /// <summary>Returns whether the display samples a monochrome-palette register one T-cycle earlier inside the
    /// writing machine cycle than the pins present it. CPU CGB D moved the sample, so the same mid-drawing palette
    /// write lands a pixel apart on older and newer Color silicon.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> from CPU CGB D onward.</returns>
    public static bool SamplesPaletteWriteEarly(this ConsoleModel model) =>
        (model >= ConsoleModel.CgbD);
    /// <summary>Returns whether an object-enable bit going low reaches the display's object path within the settling
    /// T-cycle of its own control write while the pixel pipeline sits at the start of a column. The compact
    /// monochrome package routes that bit one latch deeper, so the same write leaves the column's objects drawn
    /// there and drops them on every other monochrome package.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> for every revision except the compact monochrome package.</returns>
    public static bool DropsObjectEnableAtColumnStart(this ConsoleModel model) =>
        (model != ConsoleModel.Mgb);
    /// <summary>Returns whether the revision runs the rearranged monochrome boot ROM. It takes long enough to hand
    /// off mid vertical blank rather than at the top of a frame, so the display counter, the status register, and the
    /// processor register file it leaves all differ from every later monochrome revision.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> for the revision-0 monochrome package.</returns>
    public static bool HasRearrangedMonochromeBootRom(this ConsoleModel model) =>
        (model == ConsoleModel.Dmg0);
    /// <summary>Returns whether the boot ROM hands the cartridge the revised console-identity byte in the accumulator
    /// (all ones) instead of the original one. It is the single register difference a cartridge probes to tell the
    /// compact monochrome package and the second companion-console revision from their predecessors.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> for the compact monochrome package and the second companion-console revision.</returns>
    public static bool HasRevisedBootIdentity(this ConsoleModel model) =>
        (model is (ConsoleModel.Mgb or ConsoleModel.Sgb2));
    /// <summary>Returns whether the infrared receiver reports the machine's own emitted light. CPU CGB E and every
    /// earlier Color stepping sense their own lit LED through the infrared port; the Advanced console does not, so an
    /// unpaired Advanced machine reads dark unless a cartridge with its own infrared window is present.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> for the Color steppings.</returns>
    public static bool SensesOwnInfraredLight(this ConsoleModel model) =>
        ((model >= ConsoleModel.Cgb0) && (model <= ConsoleModel.CgbE));
    /// <summary>Returns whether the audio unit is the early silicon, up to and including CPU CGB C. Its envelope
    /// counter passes every NRx2 write through an all-ones intermediate value, its sweep unit takes an extra tick to
    /// arm a calculation, and its noise counter reloads on a different phase — so the same write sequence lands on a
    /// different volume, frequency, or noise phase than on the later steppings.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> for every monochrome revision and the Color steppings up to CPU CGB C.</returns>
    public static bool HasEarlyAudioStepping(this ConsoleModel model) =>
        (model <= ConsoleModel.CgbC);
    /// <summary>Returns whether the audio unit is CPU CGB D or E, the two steppings that carry the late colour
    /// quirks: a square channel's duty position steps once more when a silent channel restarts, and a DIV-APU
    /// envelope step is deferred by one machine cycle under double speed.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> for CPU CGB D and CPU CGB E.</returns>
    public static bool HasLateColorAudioQuirks(this ConsoleModel model) =>
        (model is (ConsoleModel.CgbD or ConsoleModel.CgbE));
    /// <summary>Returns whether each audio channel drives its own analog DAC. Up to CPU CGB E a channel whose DAC is
    /// off holds the level it last published and its wave RAM stays CPU-addressable while it plays; the Advanced
    /// console sums the channels digitally instead, so every channel always publishes and wave RAM reads back as
    /// open bus while the channel plays.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> for every revision up to CPU CGB E.</returns>
    public static bool HasPerChannelDacs(this ConsoleModel model) =>
        (model <= ConsoleModel.CgbE);
    /// <summary>Returns whether the sweep unit holds off its shadow-register reload for the shorter window after a
    /// channel-1 trigger. CPU CGB D is the one Color stepping that does; the others hold for two extra audio
    /// ticks.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> for CPU CGB D.</returns>
    public static bool HasShortSweepRestartHold(this ConsoleModel model) =>
        (model == ConsoleModel.CgbD);
    /// <summary>Returns whether the boot ROM initializes wave RAM to its alternating pattern. The CPU CGB revision-0
    /// boot ROM does not, so a cartridge that plays the wave channel without loading it first sounds different on that
    /// stepping.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> for the Color steppings from CPU CGB A onward.</returns>
    public static bool SeedsWaveRamOnBoot(this ConsoleModel model) =>
        (model.SupportsColor() && (model != ConsoleModel.Cgb0));
    /// <summary>Returns whether the boot ROM hands the cartridge a still-sounding start-up chime — square channel 1
    /// enabled with its envelope already decayed. The companion-console boot ROM plays no chime, so it hands off with
    /// the audio unit powered and every channel silent.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> for every revision except the companion console's.</returns>
    public static bool LeavesBootChimeSounding(this ConsoleModel model) =>
        !model.IsSuperGameBoy();
    /// <summary>Returns whether the boot ROM hands the cartridge the joypad register with both button groups
    /// deselected, so it reads <c>0xFF</c> with no button held. The monochrome boot ROMs leave both groups selected,
    /// which reads <c>0xCF</c>; the companion console's leaves both deselected.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> when the boot ROM deselects both button groups.</returns>
    public static bool DeselectsJoypadOnBoot(this ConsoleModel model) =>
        model.IsSuperGameBoy();
    /// <summary>Returns whether the revision's object attribute memory (OAM) is exposed to the corruption bug: a 16-bit
    /// register increment or decrement — however it arises (<c>INC</c>/<c>DEC rr</c>, the implicit stack-pointer moves
    /// inside <c>PUSH</c>/<c>POP</c>/<c>CALL</c>/<c>RET</c>/<c>RST</c> and interrupt dispatch, <c>LD [hli]</c>/
    /// <c>[hld]</c>, or <c>ADD SP</c>/<c>LD HL,SP+e</c>) whose register value lies in <c>0xFE00</c>–<c>0xFEFF</c> drives
    /// the increment/decrement unit's output onto the address bus. While the PPU is scanning OAM (mode 2) that address
    /// lands on the row it is currently reading and corrupts it. The Color hardware rewired OAM so the flaw does not
    /// reach it, even running a monochrome cartridge in compatibility mode.</summary>
    /// <param name="model">The revision to interrogate.</param>
    /// <returns><see langword="true"/> for every revision except the Color steppings and the Advanced console.</returns>
    public static bool HasOamCorruptionBug(this ConsoleModel model) =>
        !model.SupportsColor();
}
