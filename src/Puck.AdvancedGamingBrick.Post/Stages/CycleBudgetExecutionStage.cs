using System.Buffers.Binary;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Gates cycle-budget execution against instruction stepping at irregular cycle budgets, through
/// ARM/Thumb execution, RAM code edits, DMA replacement, and restore into an already exercised machine.</summary>
internal sealed class CycleBudgetExecutionStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "cycle-budget-execution";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var cases = new List<(string Label, byte[] Rom, bool Ram)> {
            ("ARM", SyntheticRom.Create(), false),
            ("mutable IWRAM", SyntheticRom.Create(), true),
            ("Thumb", ThumbLoop(), false),
        };
        if (AgbBiosProfile.Identify(image: context.BiosImage.Span).IsCycleParityTrustworthy) {
            foreach (var kind in MicroRoms.Kinds) {
                cases.Add(item: (kind, MicroRoms.GenerateBytes(kind: kind), false));
            }
        } else {
            Console.WriteLine(value: "  IRQ variants omitted: verified retail BIOS required; ARM, Thumb and RAM/DMA cases still run.");
        }
        foreach (var (label, rom, ram) in cases) {
            var (pass, detail) = ExecutionComparisonProbe.Run(rom: rom, bios: context.BiosImage, label: label, mutableRam: ram);
            Console.WriteLine(value: $"  [{(pass ? "PASS" : "FAIL")}] {detail}");
            if (!pass) {
                return PostStageOutcome.Fail(detail: detail);
            }
        }
        return PostStageOutcome.Pass(detail: $"{cases.Count} programs matched instruction stepping at every state/audio checkpoint");
    }

    private static byte[] ThumbLoop() {
        var rom = new byte[0x8000];
        // ARM loads an odd entry address then BX enters Thumb. The loop writes an incrementing word to EWRAM.
        BinaryPrimitives.WriteUInt32LittleEndian(destination: rom, value: 0xE59F0000u);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: rom.AsSpan(start: 4), value: 0xE12FFF10u);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: rom.AsSpan(start: 8), value: 0x08000011u);
        ReadOnlySpan<ushort> code = [0x2202, 0x0612, 0x2100, 0x3101, 0x6011, 0xE7FC];
        for (var index = 0; index < code.Length; ++index) {
            BinaryPrimitives.WriteUInt16LittleEndian(destination: rom.AsSpan(start: (16 + index * 2)), value: code[index]);
        }
        return rom;
    }
}
