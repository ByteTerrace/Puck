namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // Opens the redrawable integer streamDraw site a transfer or shuffle samples from, sought once at the cursor the
    // arena holds. A boot-timing site is settled into a literal at composition and has no cursor left to advance, so
    // it is not one.
    private static bool TryOpenDraws(in ArenaTransformContext context, int rowOrdinal, TransformRefusal code, string verb, out GeneratorEngine.DrawStream draws, out EffectRefusal refusal) {
        draws = default;

        if (
            (((uint)rowOrdinal) >= ((uint)context.Arena.Rows.Count)) ||
            (context.Arena.Rows[rowOrdinal].Draw is not { Timing: not DrawTiming.Boot } declared) ||
            (context.Arena.Layout[rowOrdinal].Kind != CellKind.Int) ||
            !GeneratorEngine.TryResolveSource(
            draw: declared,
            generator: out var generator,
            generators: context.Generators,
            reason: out _
        ) ||
            (generator.Source != GeneratorSource.StreamDraw)
        ) {
            return Refuse(
                code: code,
                reason: $"{verb} requires a redrawable integer streamDraw site, which row '{RowName(
                    context: in context,
                    rowOrdinal: rowOrdinal
                )}' is not",
                refusal: out refusal
            );
        }
        if (
            (context.Seeds is not { } seeds) ||
            (rowOrdinal >= seeds.Count)
        ) {
            return Refuse(
                code: code,
                reason: $"{verb} draws from row '{RowName(
                    context: in context,
                    rowOrdinal: rowOrdinal
                )}', which this host seeds no draw site for",
                refusal: out refusal
            );
        }
        if (!ArenaDraws.TryOpen(
            arena: context.Arena,
            generator: generator,
            reason: out var reason,
            rowOrdinal: rowOrdinal,
            secret: declared.Secret,
            seed: seeds[rowOrdinal],
            skip: declared.Skip,
            stream: out draws
        )) {
            return Refuse(
                code: code,
                reason: reason,
                refusal: out refusal
            );
        }

        refusal = EffectRefusal.None;

        return true;
    }
    // One sample of an open stream: constant work, and nothing written until the stream closes.
    private static bool TrySample(ref GeneratorEngine.DrawStream draws, TransformRefusal code, out long sample, out EffectRefusal refusal) {
        if (!draws.TryNext(
            reason: out var reason,
            value: out sample
        )) {
            return Refuse(
                code: code,
                reason: reason,
                refusal: out refusal
            );
        }

        refusal = EffectRefusal.None;

        return true;
    }
    // Stores the advanced cursor, then records the last sample in the site's slot cell, the way an ordinary draw
    // site's slot cell holds its last emission. Both writes land in the caller's open scope, which is what un-consumes
    // the samples when the transform refuses.
    private static bool TryCloseDraws(in ArenaTransformContext context, int rowOrdinal, in GeneratorEngine.DrawStream draws, long sample, TransformRefusal code, out EffectRefusal refusal) {
        if (!ArenaDraws.TryClose(
            arena: context.Arena,
            reason: out var closeReason,
            rowOrdinal: rowOrdinal,
            stream: in draws
        )) {
            return Refuse(
                code: code,
                reason: closeReason,
                refusal: out refusal
            );
        }
        if (!context.Arena.TryWrite(
            key: context.Arena.Keys.Intern(name: StateRow.SlotKey),
            operand: sample,
            reason: out var reason,
            rowOrdinal: rowOrdinal,
            write: StateWriteKind.Set
        )) {
            return Refuse(
                code: code,
                reason: reason,
                refusal: out refusal
            );
        }

        refusal = EffectRefusal.None;

        return true;
    }
    // The multiply-high map from a 32-bit sample onto 0..count-1: the one selection arithmetic a random transfer
    // and a shuffle share.
    private static int Select(long sample, int count) => ((int)((((ulong)sample) * ((ulong)count)) >> 32));
}
