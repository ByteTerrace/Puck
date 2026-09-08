using Puck.Abstractions.Machines;
using Puck.Hosting;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// The shared fixture the linked-queued-host stages boot: a synthetic seat cartridge that samples its own joypad,
/// publishes the sampled image to a readable work-RAM cell, and exchanges it over the serial cable, plus the host and
/// pad construction both stages use. One side drives the transfer clock, the other waits on it; everything else about
/// the two images is identical, so any observed difference between the seats came from routing, not from the program.
/// </summary>
internal static class LinkedHostFixture {
    private const int EntryPoint = 0x0100;
    private const int RomSize = 0x8000;

    /// <summary>The engine-tick budget of one 60&#160;Hz host step — the same fixed-step budget a world tick hands a
    /// machine.</summary>
    public const ulong FrameTicks = (EngineTicks.PerSecond / 60UL);
    /// <summary>The work-RAM address the seat program publishes its own sampled joypad image to.</summary>
    public const ushort JoypadImageAddress = 0xC0F2;
    /// <summary>The work-RAM address the seat program publishes the last byte received over the cable to.</summary>
    public const ushort PeerImageAddress = 0xC0F3;
    /// <summary>The work-RAM address holding the seat program's wrapping completed-transfer counter.</summary>
    public const ushort TransferCountAddress = 0xC0F4;

    /// <summary>Creates one seat's cartridge image.</summary>
    /// <param name="internalClock">Whether this seat drives the transfer clock (SC <c>0x81</c>) or waits on the peer's
    /// clock (SC <c>0x80</c>).</param>
    /// <returns>A 32&#160;KiB ROM-only cartridge image whose entry point runs the seat program.</returns>
    public static byte[] SeatRom(bool internalClock) {
        var control = ((byte)(internalClock
            ? 0x81
            : 0x80));

        // The seat program at the post-boot entry point 0x0100. P1's select bits are active low, so writing 0x10 leaves
        // the action group selected and 0x20 leaves the direction group selected; each group's four lines read low when
        // held, so CPL + AND 0x0F yields the held bits active-high. The published image is
        // (direction nibble << 4) | action nibble, with the action nibble laid out A, B, Select, Start from bit 0.
        //   0x0100  31 FE FF   ld   sp, 0xFFFE
        //   0x0103  3E 10      ld   a, 0x10       ; loop: select the action group
        //   0x0105  E0 00      ldh  (0xFF00), a
        //   0x0107  F0 00      ldh  a, (0xFF00)
        //   0x0109  F0 00      ldh  a, (0xFF00)
        //   0x010B  2F         cpl
        //   0x010C  E6 0F      and  0x0F
        //   0x010E  4F         ld   c, a
        //   0x010F  3E 20      ld   a, 0x20       ; select the direction group
        //   0x0111  E0 00      ldh  (0xFF00), a
        //   0x0113  F0 00      ldh  a, (0xFF00)
        //   0x0115  F0 00      ldh  a, (0xFF00)
        //   0x0117  2F         cpl
        //   0x0118  E6 0F      and  0x0F
        //   0x011A  CB 37      swap a
        //   0x011C  B1         or   c
        //   0x011D  EA F2 C0   ld   (0xC0F2), a   ; publish this seat's own sampled image
        //   0x0120  E0 01      ldh  (0xFF01), a   ; SB = the sampled image
        //   0x0122  3E nn      ld   a, control
        //   0x0124  E0 02      ldh  (0xFF02), a   ; SC = start (internal or external clock)
        //   0x0126  F0 02      ldh  a, (0xFF02)   ; wait: poll the transfer bit
        //   0x0128  E6 80      and  0x80
        //   0x012A  20 FA      jr   nz, 0x0126
        //   0x012C  F0 01      ldh  a, (0xFF01)
        //   0x012E  EA F3 C0   ld   (0xC0F3), a   ; publish the peer's image
        //   0x0131  FA F4 C0   ld   a, (0xC0F4)
        //   0x0134  3C         inc  a
        //   0x0135  EA F4 C0   ld   (0xC0F4), a   ; count the completed transfer
        //   0x0138  18 C9      jr   0x0103
        byte[] program = [
            0x31, 0xFE, 0xFF,
            0x3E, 0x10,
            0xE0, 0x00,
            0xF0, 0x00,
            0xF0, 0x00,
            0x2F,
            0xE6, 0x0F,
            0x4F,
            0x3E, 0x20,
            0xE0, 0x00,
            0xF0, 0x00,
            0xF0, 0x00,
            0x2F,
            0xE6, 0x0F,
            0xCB, 0x37,
            0xB1,
            0xEA, 0xF2, 0xC0,
            0xE0, 0x01,
            0x3E, control,
            0xE0, 0x02,
            0xF0, 0x02,
            0xE6, 0x80,
            0x20, 0xFA,
            0xF0, 0x01,
            0xEA, 0xF3, 0xC0,
            0xFA, 0xF4, 0xC0,
            0x3C,
            0xEA, 0xF4, 0xC0,
            0x18, 0xC9,
        ];

        // A zero-filled image already carries a valid ROM-only header (see SyntheticRom); only the program is written.
        var rom = new byte[RomSize];

        program.CopyTo(
            array: rom,
            index: EntryPoint
        );

        return rom;
    }
    /// <summary>Creates one seat's queued host.</summary>
    /// <param name="internalClock">Whether this seat drives the transfer clock.</param>
    /// <param name="audioSampleRate">The host's audio output rate, or 0 for a silent host.</param>
    /// <returns>The host. The caller owns it and must dispose it.</returns>
    public static MachineHost NewHost(bool internalClock, int audioSampleRate = 0) =>
        new(
            audioSampleRate: audioSampleRate,
            cartridgeRom: SeatRom(internalClock: internalClock),
            model: ConsoleModel.DmgC
        );
    /// <summary>Creates a validated neutral pad image holding one button set.</summary>
    /// <param name="buttons">The buttons held.</param>
    /// <returns>The pad image.</returns>
    public static MachinePadState Pad(MachineButtons buttons) =>
        new(
            Buttons: buttons,
            LeftStick: default,
            LeftTrigger: 0f,
            RightStick: default,
            RightTrigger: 0f
        );
}
