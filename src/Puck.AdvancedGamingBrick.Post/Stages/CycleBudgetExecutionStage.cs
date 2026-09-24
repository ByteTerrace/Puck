using System.Buffers.Binary;
using System.Diagnostics;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Gates cycle-budget execution against instruction stepping at irregular cycle budgets, through
/// ARM/Thumb execution, RAM code edits, DMA replacement, and restore into an already exercised machine.</summary>
internal sealed class CycleBudgetExecutionStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public bool IsConcurrent => true;
    /// <inheritdoc/>
    public string Name => "cycle-budget-execution";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    private static byte[] ThumbLoop() {
        var rom = new byte[0x8000];
        // ARM loads an odd entry address then BX enters Thumb. The loop writes an incrementing word to EWRAM.
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: rom,
            value: 0xE59F0000u
        );
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: rom.AsSpan(start: 4),
            value: 0xE12FFF10u
        );
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: rom.AsSpan(start: 8),
            value: 0x08000011u
        );
        ReadOnlySpan<ushort> code = [0x2202, 0x0612, 0x2100, 0x3101, 0x6011, 0xE7FC];

        for (var index = 0; (index < code.Length); ++index) {
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination: rom.AsSpan(start: (16 + (index * 2))),
                value: code[index]
            );
        }
        return rom;
    }

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var cases = new List<(string Label, byte[] Rom, bool Ram)> {
            ("ARM", SyntheticRom.Create(), false),
            ("mutable IWRAM", SyntheticRom.Create(), true),
            ("Thumb", ThumbLoop(), false),
        };

        // Without a verified retail BIOS the IRQ variants cannot be compared cycle-for-cycle, so only the ARM, Thumb and
        // RAM/DMA programs run and the detail says so.
        var irqNote = string.Empty;

        if (AgbBiosProfile.Identify(image: context.BiosImage.Span).IsCycleParityTrustworthy) {
            foreach (var kind in MicroRoms.Kinds) {
                cases.Add(item: (kind, MicroRoms.GenerateBytes(kind: kind), false));
            }
        } else {
            irqNote = "; IRQ variants omitted (verified retail BIOS required)";
        }

        var rows = new List<PostCaseResult>(capacity: cases.Count);

        foreach (var (label, rom, ram) in cases) {
            var start = Stopwatch.GetTimestamp();

            var (pass, detail) = ExecutionComparisonProbe.Run(
                rom: rom,
                bios: context.BiosImage,
                label: label,
                mutableRam: ram
            );

            rows.Add(item: new PostCaseResult(
                Detail: detail,
                Duration: Stopwatch.GetElapsedTime(startingTimestamp: start),
                Name: label,
                Verdict: (pass
                    ? PostCaseVerdict.Pass
                    : PostCaseVerdict.Mismatch)
            ));

            if (!pass) {
                return PostStageOutcome.Fail(
                    cases: rows,
                    detail: detail
                );
            }
        }

        return PostStageOutcome.Pass(
            cases: rows,
            detail: $"{cases.Count} programs matched instruction stepping at every state/audio checkpoint{irqNote}"
        );
    }
}
