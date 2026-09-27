using Puck.Machines;

namespace Puck.HumbleGamingDeck.Post;

/// <summary>Builds the original CPU fixture and supplies machine probes with master-tick budgets.</summary>
internal static class PostMachine {
    /// <summary>The number of master ticks in one probe batch.</summary>
    public const ulong BatchTicks = 120_000;

    /// <summary>Creates an NROM image containing an original register and RAM loop.</summary>
    /// <returns>The complete image with an NES 2.0 header and reset/interrupt vectors.</returns>
    public static byte[] CreateImage() {
        var image = new byte[(16 + 16384)];

        "NES\u001a"u8.CopyTo(destination: image);
        image[4] = 1;
        image[7] = 8;
        image[10] = 7;
        image[11] = 7;
        // The loop returns to INX at $8002; the vectors below enter the initial LDX at $8000.
        ReadOnlySpan<byte> program = [0xA2, 0x00, 0xE8, 0x86, 0x00, 0xEE, 0x00, 0x60, 0x4C, 0x02, 0x80];

        program.CopyTo(destination: image.AsSpan(start: 16));
        for (var vector = 0x3FFA; (vector <= 0x3FFE); vector += 2) {
            image[(16 + vector)] = 0;
            image[((16 + vector) + 1)] = 0x80;
        }

        return image;
    }
    /// <summary>Creates an independent machine over the original fixture image.</summary>
    /// <returns>The machine instance owned and disposed by the probe.</returns>
    public static MachineInstance<HgdMachine, HgdMachineConfiguration> Build() {
        return HgdMachineFactory.Create(configuration: new HgdMachineConfiguration(cartridge: HgdCartridge.Load(image: CreateImage())));
    }
    /// <summary>Advances a probe's batch budget in master ticks.</summary>
    /// <param name="instance">The machine instance to advance.</param>
    /// <param name="count">The nonnegative number of batches.</param>
    /// <exception cref="OverflowException">The accumulated master-tick target exceeds the clock range.</exception>
    public static void RunBatches(MachineInstance<HgdMachine, HgdMachineConfiguration> instance, int count) {
        instance.Machine.RunCycles(masterTicks: (((ulong)count) * BatchTicks));
    }
    /// <summary>Locates the first differing component byte in two captures.</summary>
    /// <param name="first">The expected capture with a complete section table.</param>
    /// <param name="second">The actual capture.</param>
    /// <returns>The component and byte offset, or the identity/timestamp/length category when bytes agree.</returns>
    public static string Describe(HgdMachineSnapshot first, HgdMachineSnapshot second) {
        var length = Math.Min(val1: first.Size, val2: second.Size);

        for (var offset = 0; (offset < length); ++offset) {
            if (first.Data[offset] != second.Data[offset]) {
                var section = first.Sections.First(predicate: candidate => ((offset >= candidate.Offset) && (offset < (candidate.Offset + candidate.Length))));

                return $"{section.Name}+{(offset - section.Offset)}: {first.Data[offset]:X2} versus {second.Data[offset]:X2}";
            }
        }

        return "identity, timestamp, or image length differs";
    }
}
