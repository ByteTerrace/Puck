namespace Puck.State.Rules;

public static partial class ArenaTransforms {
    // The redrawable integer streamDraw site a transfer or shuffle samples from. A boot-timing site is settled into
    // a literal at composition and has no cursor left to advance, so it is not one.
    private static bool TryDrawSite(in ArenaTransformContext context, int rowOrdinal, TransformRefusal code, string verb, out StateGenerator generator, out Draw draw, out EffectRefusal refusal) {
        draw = default!;
        generator = default!;

        if (
            (((uint)rowOrdinal) >= ((uint)context.Arena.Rows.Count)) ||
            (context.Arena.Rows[rowOrdinal].Draw is not { Timing: not DrawTiming.Boot } declared) ||
            (context.Arena.Layout[rowOrdinal].Kind != CellKind.Int) ||
            !GeneratorEngine.TryResolveSource(
            draw: declared,
            generator: out generator,
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

        draw = declared;
        refusal = EffectRefusal.None;

        return true;
    }
    // Every sample advances the site's cursor through the arena, so the caller's open scope is what un-consumes a
    // draw when the transform refuses.
    private static bool TrySample(in ArenaTransformContext context, int rowOrdinal, StateGenerator generator, in Draw draw, TransformRefusal code, out long sample, out EffectRefusal refusal) {
        sample = 0L;

        if (!ArenaDraws.TryFire(
            arena: context.Arena,
            documentSeed: context.DocumentSeed,
            generator: generator,
            instanceIdentity: context.InstanceIdentity,
            reason: out var reason,
            result: out var fired,
            rowOrdinal: rowOrdinal,
            secret: draw.Secret,
            site: context.SiteOf(rowOrdinal: rowOrdinal),
            skip: draw.Skip
        )) {
            return Refuse(
                code: code,
                reason: reason,
                refusal: out refusal
            );
        }
        if (fired.Numeric is not { } numeric) {
            return Refuse(
                code: code,
                reason: $"row '{RowName(
                    context: in context,
                    rowOrdinal: rowOrdinal
                )}' emitted no number to select with",
                refusal: out refusal
            );
        }

        sample = numeric;
        refusal = EffectRefusal.None;

        return true;
    }
    // The site records the last sample it emitted, the way an ordinary draw site's slot cell does.
    private static bool TryRecordSample(in ArenaTransformContext context, int rowOrdinal, long sample, TransformRefusal code, out EffectRefusal refusal) {
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
