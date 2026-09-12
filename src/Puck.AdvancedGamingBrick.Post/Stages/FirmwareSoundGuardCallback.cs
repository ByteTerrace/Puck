namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Original ARM callback recording the sound-area guard and a bounded nested sound-driver call.</summary>
internal static class FirmwareSoundGuardCallback {
    internal const uint Entry = 0x03006000;
    internal const uint Record = 0x02004000;
    internal const uint ClearEntry = Entry + 0x100;
    internal const uint ClearRecord = Record + 0x100;

    internal static void InstallClearRecorder(IAgbBus bus) {
        // Append each oscillator-off channel argument to original EWRAM data.
        ReadOnlySpan<uint> words = [0xE59FC014, 0xE59C1000, 0xE28C2004, 0xE7820101, 0xE2811001, 0xE58C1000, 0xE12FFF1E, ClearRecord];
        for (var index = 0; index < words.Length; ++index) {
            bus.Write32(address: ClearEntry + (uint)(index * 4), value: words[index], access: BusAccessType.NonSequential);
        }
    }

    internal static void Install(IAgbBus bus, uint area, bool nested, byte nestedNumber = 0x1C, uint nestedInput = 0) {
        ReadOnlySpan<uint> words = [
            0xE92D4070, // stmdb sp!, {r4-r6,lr}
            0xE59F4038, // ldr r4, record literal
            0xE59F5038, // ldr r5, area literal
            0xE5940000, // ldr r0, [r4]: callback count
            0xE2800001,
            0xE5840000,
            0xE5951000, // ldr r1, [r5]: guard during callback
            0xE5841004,
            0xE3500001, // cmp r0, #1
            0x1A000001, // bne past nested SWI: at most two callback entries
            0xE59F001C, // ldr r0, nested input literal
            nested ? 0xEF000000u | ((uint)nestedNumber << 16) : 0xE1A00000u,
            0xE5951000,
            0xE5841008, // guard after nested call
            0xE8BD4070,
            0xE12FFF1E, // bx lr: preserves the caller's ARM/Thumb state
            0xE1A00000,
            Record,
            area,
            nestedInput,
        ];
        for (var index = 0; index < words.Length; ++index) {
            bus.Write32(address: Entry + (uint)(index * 4), value: words[index], access: BusAccessType.NonSequential);
        }
    }
}
