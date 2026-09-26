namespace Puck.HumbleGamingBrick.Forge;

/// <summary>
/// Builds the Color cartridge a light gun is aimed at to prove it reaches a running program. It draws the left half of
/// the screen white and the right half black, arms the infrared receiver (RP, <c>0xFF56</c>) with its own LED off so it
/// never senses itself, then publishes the received-light bit to <see cref="SensedAddress"/> forever: 1 while the light
/// gun sees a lit pixel, 0 otherwise. The header is <see cref="BootRomProbeCartridge"/>'s, logo and checksum included,
/// so the cartridge boots cold on every firmware as well as fast.
/// </summary>
public static class LightGunProbeCartridge {
    private const int EntryPoint = 0x0100;
    private const int ProgramAddress = 0x0150;

    //   0x0100  00        nop
    //   0x0101  C3 50 01  jp   0x0150              ; past the header
    //
    //   0x0150  F3        di
    //   0x0151  31 FE FF  ld   sp, 0xFFFE
    //   wait    0x0154  F0 44     ldh  a, (LY)     ; turn the LCD off only inside VBlank
    //           0x0156  FE 90     cp   144
    //           0x0158  20 FA     jr   nz, wait
    //           0x015A  AF        xor  a
    //           0x015B  E0 40     ldh  (LCDC), a
    //           0x015D  21 00 80  ld   hl, 0x8000  ; tile 0: every pixel color 0
    //           0x0160  06 10     ld   b, 16
    //   t0      0x0162  22        ld   (hl+), a
    //           0x0163  05        dec  b
    //           0x0164  20 FC     jr   nz, t0
    //           0x0166  3E FF     ld   a, 0xFF     ; tile 1: every pixel color 3
    //           0x0168  06 10     ld   b, 16
    //   t1      0x016A  22        ld   (hl+), a
    //           0x016B  05        dec  b
    //           0x016C  20 FC     jr   nz, t1
    //           0x016E  21 00 98  ld   hl, 0x9800  ; map: columns 0-9 tile 0, columns 10-31 tile 1
    //           0x0171  0E 20     ld   c, 32
    //   row     0x0173  06 20     ld   b, 32
    //   col     0x0175  78        ld   a, b        ; b counts 32..1, so the column is 32 - b
    //           0x0176  FE 17     cp   23
    //           0x0178  3E 00     ld   a, 0
    //           0x017A  30 02     jr   nc, store   ; column < 10
    //           0x017C  3E 01     ld   a, 1
    //   store   0x017E  22        ld   (hl+), a
    //           0x017F  05        dec  b
    //           0x0180  20 F3     jr   nz, col
    //           0x0182  0D        dec  c
    //           0x0183  20 EE     jr   nz, row
    //           0x0185  3E 80     ld   a, 0x80     ; BCPS: palette 0, color 0, auto-increment
    //           0x0187  E0 68     ldh  (BCPS), a
    //           0x0189  3E FF     ld   a, 0xFF     ; color 0 white (0x7FFF), colors 1-3 black
    //           0x018B  E0 69     ldh  (BCPD), a
    //           0x018D  3E 7F     ld   a, 0x7F
    //           0x018F  E0 69     ldh  (BCPD), a
    //           0x0191  AF        xor  a
    //           0x0192  E0 69     ldh  (BCPD), a   ; six times
    //           ...
    //           0x019E  3E 01     ld   a, 1        ; VRAM bank 1: every map attribute 0 (palette 0, no flip)
    //           0x01A0  E0 4F     ldh  (VBK), a
    //           0x01A2  21 00 98  ld   hl, 0x9800
    //           0x01A5  01 00 04  ld   bc, 0x0400
    //   attr    0x01A8  AF        xor  a
    //           0x01A9  22        ld   (hl+), a
    //           0x01AA  0B        dec  bc
    //           0x01AB  78        ld   a, b
    //           0x01AC  B1        or   c
    //           0x01AD  20 F9     jr   nz, attr
    //           0x01AF  AF        xor  a
    //           0x01B0  E0 4F     ldh  (VBK), a
    //           0x01B2  E0 42     ldh  (SCY), a
    //           0x01B4  E0 43     ldh  (SCX), a
    //           0x01B6  3E 91     ld   a, 0x91     ; LCD on, tile data 0x8000, map 0x9800, background on
    //           0x01B8  E0 40     ldh  (LCDC), a
    //           0x01BA  3E C0     ld   a, 0xC0     ; RP: receiver armed, LED off
    //           0x01BC  E0 56     ldh  (RP), a
    //   loop    0x01BE  F0 56     ldh  a, (RP)
    //           0x01C0  E6 02     and  0x02        ; bit 1: 0 = light received
    //           0x01C2  0F        rrca
    //           0x01C3  EE 01     xor  0x01        ; 1 = light
    //           0x01C5  EA 00 C0  ld   (0xC000), a
    //           0x01C8  18 F4     jr   loop
    private static readonly byte[] Program = [
        0xF3,
        0x31, 0xFE, 0xFF,
        0xF0, 0x44,
        0xFE, 0x90,
        0x20, 0xFA,
        0xAF,
        0xE0, 0x40,
        0x21, 0x00, 0x80,
        0x06, 0x10,
        0x22,
        0x05,
        0x20, 0xFC,
        0x3E, 0xFF,
        0x06, 0x10,
        0x22,
        0x05,
        0x20, 0xFC,
        0x21, 0x00, 0x98,
        0x0E, 0x20,
        0x06, 0x20,
        0x78,
        0xFE, 0x17,
        0x3E, 0x00,
        0x30, 0x02,
        0x3E, 0x01,
        0x22,
        0x05,
        0x20, 0xF3,
        0x0D,
        0x20, 0xEE,
        0x3E, 0x80,
        0xE0, 0x68,
        0x3E, 0xFF,
        0xE0, 0x69,
        0x3E, 0x7F,
        0xE0, 0x69,
        0xAF,
        0xE0, 0x69,
        0xE0, 0x69,
        0xE0, 0x69,
        0xE0, 0x69,
        0xE0, 0x69,
        0xE0, 0x69,
        0x3E, 0x01,
        0xE0, 0x4F,
        0x21, 0x00, 0x98,
        0x01, 0x00, 0x04,
        0xAF,
        0x22,
        0x0B,
        0x78,
        0xB1,
        0x20, 0xF9,
        0xAF,
        0xE0, 0x4F,
        0xE0, 0x42,
        0xE0, 0x43,
        0x3E, 0x91,
        0xE0, 0x40,
        0x3E, 0xC0,
        0xE0, 0x56,
        0xF0, 0x56,
        0xE6, 0x02,
        0x0F,
        0xEE, 0x01,
        0xEA, ((byte)(SensedAddress & 0xFF)), ((byte)(SensedAddress >> 8)),
        0x18, 0xF4,
    ];

    /// <summary>The first pixel column of the black right half.</summary>
    public const int DarkColumn = 80;
    /// <summary>The work-RAM address the program publishes the received-light bit to.</summary>
    public const ushort SensedAddress = 0xC000;

    /// <summary>Creates the cartridge image.</summary>
    /// <returns>A 32&#160;KiB ROM-only cartridge image that declares Color support, which RP needs to answer.</returns>
    public static byte[] Create() {
        var rom = BootRomProbeCartridge.Create(probe: new BootRomProbe(
            ColorFlag: 0x80,
            HandoffLine: 0,
            NewLicenseeCode: "  ",
            OldLicenseeCode: 0x00,
            Title: "LIGHTGUN"
        ));

        rom[EntryPoint] = 0x00;
        rom[(EntryPoint + 1)] = 0xC3;
        rom[(EntryPoint + 2)] = ((byte)(ProgramAddress & 0xFF));
        rom[(EntryPoint + 3)] = ((byte)(ProgramAddress >> 8));
        Program.CopyTo(
            array: rom,
            index: ProgramAddress
        );

        return rom;
    }
}
