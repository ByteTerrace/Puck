namespace Puck.State;

/// <summary>A draw site's persisted state as the <see cref="StateArena"/> holds it: the site's cursor and its
/// drawn masks read out of the arena's runtime-state columns, handed to <see cref="GeneratorEngine"/>, and written
/// back. The engine itself stays a pure function of what it is given.</summary>
/// <remarks>A site reserves <see cref="ArenaRowLayout.MaskCount"/> mask words' worth of contexts, which is what
/// <see cref="StateGenerator.MaskCount"/> says the site's declared source persists. A source whose emission needs
/// more masks than the site reserved is refused rather than drawn with a short mask set.</remarks>
public static class ArenaDraws {
    private static bool TryMasks(StateArena arena, int rowOrdinal, StateGenerator generator, out ClosedBitset256[]? masks, out string reason) {
        if (((uint)rowOrdinal) >= ((uint)arena.Layout.RowCount)) {
            masks = null;
            reason = $"row ordinal {rowOrdinal} names no row of this arena";

            return false;
        }

        var declared = arena.Layout[rowOrdinal].MaskCount;
        var needed = StateGenerator.MaskCount(generator: generator);

        if (needed > declared) {
            masks = null;
            reason = $"row ordinal {rowOrdinal} reserves {declared} drawn masks, fewer than the {needed} its source persists";

            return false;
        }

        reason = string.Empty;

        if (needed == 0) {
            masks = null;

            return true;
        }

        masks = new ClosedBitset256[needed];

        for (var index = 0; (index < needed); index++) {
            masks[index] = arena.DrawnMask(
                index: index,
                rowOrdinal: rowOrdinal
            );
        }

        return true;
    }

    /// <summary>Reads a draw site's persisted drawn masks.</summary>
    /// <param name="arena">The arena holding the site.</param>
    /// <param name="rowOrdinal">The site row's catalog ordinal.</param>
    /// <returns>One mask per reserved context, or <see langword="null"/> when the site reserves none or the ordinal
    /// names no row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    public static ClosedBitset256[]? ReadMasks(StateArena arena, int rowOrdinal) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        if (((uint)rowOrdinal) >= ((uint)arena.Layout.RowCount)) {
            return null;
        }

        var declared = arena.Layout[rowOrdinal].MaskCount;

        if (declared <= 0) {
            return null;
        }

        var masks = new ClosedBitset256[declared];

        for (var index = 0; (index < declared); index++) {
            masks[index] = arena.DrawnMask(
                index: index,
                rowOrdinal: rowOrdinal
            );
        }

        return masks;
    }
    /// <summary>Fires one emission for a draw site against the arena: reads the site's cursor and drawn masks,
    /// draws, and writes the advanced cursor and the masks the source persists back into the arena.</summary>
    /// <param name="arena">The arena holding the site.</param>
    /// <param name="rowOrdinal">The site row's catalog ordinal.</param>
    /// <param name="generator">The site's resolved source.</param>
    /// <param name="seed">The site's seed.</param>
    /// <param name="result">The emission, on success.</param>
    /// <param name="reason">Why the emission was refused, or empty on success.</param>
    /// <param name="secret">The site's authority-provisioned secret, when it declares one.</param>
    /// <param name="skip">The site's authored seek.</param>
    /// <returns><see langword="true"/> when the site drew and its state was written back.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> or <paramref name="generator"/> is
    /// <see langword="null"/>.</exception>
    /// <remarks>The write-back is two arena writes, so a caller that wants the draw to rewind with a refused firing
    /// fires it inside a journal scope.</remarks>
    public static bool TryFire(StateArena arena, int rowOrdinal, StateGenerator generator, DrawSeed seed, out GeneratorEngine.FireResult result, out string reason, ClosedBitset256? secret = null, long skip = 0L) {
        ArgumentNullException.ThrowIfNull(argument: arena);
        ArgumentNullException.ThrowIfNull(argument: generator);

        result = default;

        if (!TryMasks(
            arena: arena,
            generator: generator,
            masks: out var masks,
            reason: out reason,
            rowOrdinal: rowOrdinal
        )) {
            return false;
        }

        var cursor = arena.DrawCursor(rowOrdinal: rowOrdinal);

        if (!GeneratorEngine.TryFire(
            cursor: cursor,
            generator: generator,
            masks: masks,
            reason: out reason,
            result: out result,
            secret: secret,
            seedState: seed.State,
            skip: skip,
            stream: seed.Stream,
            targetKind: arena.Layout[rowOrdinal].Kind
        )) {
            return false;
        }

        return TryWrite(
            arena: arena,
            cursor: (cursor + result.Samples),
            masks: GeneratorEngine.MasksAfter(
                fired: result.Masks,
                generator: generator,
                previous: masks
            ),
            reason: out reason,
            rowOrdinal: rowOrdinal
        );
    }
    /// <summary>Opens a draw site's stream at the cursor the arena holds, for a caller that draws several samples in
    /// one pass: the site is sought once, and each sample after that is constant work.</summary>
    /// <param name="arena">The arena holding the site.</param>
    /// <param name="rowOrdinal">The site row's catalog ordinal.</param>
    /// <param name="generator">The site's resolved numeric source.</param>
    /// <param name="seed">The site's seed.</param>
    /// <param name="stream">The positioned stream, on success.</param>
    /// <param name="reason">Why the stream was refused, or empty on success.</param>
    /// <param name="secret">The site's authority-provisioned secret, when it declares one.</param>
    /// <param name="skip">The site's authored seek.</param>
    /// <returns><see langword="true"/> when the stream is positioned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> or <paramref name="generator"/> is
    /// <see langword="null"/>.</exception>
    /// <remarks>Nothing is written until <see cref="TryClose"/> stores the advanced cursor, so a caller that refuses
    /// between the two leaves the site where it was.</remarks>
    public static bool TryOpen(StateArena arena, int rowOrdinal, StateGenerator generator, DrawSeed seed, out GeneratorEngine.DrawStream stream, out string reason, ClosedBitset256? secret = null, long skip = 0L) {
        ArgumentNullException.ThrowIfNull(argument: arena);
        ArgumentNullException.ThrowIfNull(argument: generator);

        stream = default;

        return (TryMasks(
            arena: arena,
            generator: generator,
            masks: out var masks,
            reason: out reason,
            rowOrdinal: rowOrdinal
        ) && GeneratorEngine.DrawStream.TryOpen(
            cursor: arena.DrawCursor(rowOrdinal: rowOrdinal),
            generator: generator,
            masks: masks,
            reason: out reason,
            secret: secret,
            seed: seed,
            skip: skip,
            stream: out stream,
            targetKind: arena.Layout[rowOrdinal].Kind
        ));
    }
    /// <summary>Stores a stream's advanced cursor and the drawn masks its source persists back into the site.</summary>
    /// <param name="arena">The arena holding the site.</param>
    /// <param name="rowOrdinal">The site row's catalog ordinal.</param>
    /// <param name="stream">The stream <see cref="TryOpen"/> positioned at this site.</param>
    /// <param name="reason">Why the store was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the site's state was stored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    public static bool TryClose(StateArena arena, int rowOrdinal, in GeneratorEngine.DrawStream stream, out string reason) => TryWrite(
        arena: arena,
        cursor: stream.Cursor,
        masks: stream.MasksAfter,
        reason: out reason,
        rowOrdinal: rowOrdinal
    );
    /// <summary>Writes a draw site's cursor and drawn masks back into the arena.</summary>
    /// <param name="arena">The arena holding the site.</param>
    /// <param name="rowOrdinal">The site row's catalog ordinal.</param>
    /// <param name="cursor">The cursor to store; never negative.</param>
    /// <param name="masks">The masks to store, or <see langword="null"/> when the source persists none.</param>
    /// <param name="reason">Why the write was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the site's state was stored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    public static bool TryWrite(StateArena arena, int rowOrdinal, long cursor, IReadOnlyList<ClosedBitset256>? masks, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        if (!arena.TryWriteDrawCursor(
            cursor: cursor,
            rowOrdinal: rowOrdinal
        )) {
            reason = $"row ordinal {rowOrdinal} is not a draw site the arena stores a cursor of {cursor} for";

            return false;
        }

        for (var index = 0; (index < (masks?.Count ?? 0)); index++) {
            if (!arena.TryWriteDrawnMask(
                index: index,
                mask: masks![index],
                rowOrdinal: rowOrdinal
            )) {
                reason = $"row ordinal {rowOrdinal} reserves no drawn mask {index}";

                return false;
            }
        }

        reason = string.Empty;

        return true;
    }
}
