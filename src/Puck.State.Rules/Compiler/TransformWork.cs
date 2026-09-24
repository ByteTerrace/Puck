namespace Puck.State.Rules;

/// <summary>Prices one firing of an arena transform from the operands it can touch, all known at compile time: a
/// fixed door, the scratch each kernel leases, the cells it visits, and the journal entries its writes record. A
/// firing reaches the live arena alone, so nothing here scales with the rest of the document.</summary>
/// <remarks>The unit is one element operation, the currency of every other kernel price on the work sheet. A journal
/// entry, a leased scratch element, and a vector component copied are each one; an arena cell door — its layout,
/// admission and presence reads — is <see cref="Door"/> beyond the entries it records.</remarks>
public static class TransformWork {
    /// <summary>The fixed door every arena effect arm charges once per firing: dispatch, binding resolution, the row
    /// shape checks, and the refusal path.</summary>
    public const long Call = 512L;
    /// <summary>One arena cell door beyond the journal entries it records: the row layout, the admission of the
    /// value, the enum lookup, and the presence read.</summary>
    public const long Door = 4L;
    /// <summary>The flat price a generate effect's one emission carries, whatever its source walks.</summary>
    public const long GeneratorFiring = 4_096L;

    // A board cell store records its number and presence lanes; a lattice carries no trait and feeds no derived board.
    private const long BoardEntries = 2L;
    // Pcg32XshRr.Advance composes the affine step with itself once per bit of a 64-bit count, two multiply-adds a bit.
    private const long SeekSteps = 64L;
    private const long Seek = (2L * SeekSteps);
    // One base-generator step and its output permutation.
    private const long Sample = 2L;
    // PrivateDraw's keyed sample: four SHA-256 compressions (two for the inner hash, two for the outer), each 64 rounds
    // over a 48-word message schedule, with the key and message packed around them.
    private const long PrivateSample = 512L;
    // Pcg32Extended ticks its table once every 2^16 base draws, a mixed-radix increment over all k words.
    private const long TableTickPeriod = 65_536L;
    // A live store into a traited row also rebases the cell's six clock lanes (StateArena.WriteClockAt).
    private const long ClockEntries = 6L;
    // The per-cell columns StateArena.CellColumns lists; a member move reads and writes every one of them.
    private const long CellLanes = 14L;

    /// <summary>Returns the work one board cell store costs.</summary>
    public static long BoardWrite => (Door + BoardEntries);

    /// <summary>Returns the work one live numeric store into a keyed or pool row costs: its number and presence lanes,
    /// the clock lanes a traited row rebases, and the derived boards the stored cell feeds.</summary>
    /// <param name="context">The compile context the row resolves against.</param>
    /// <param name="rowOrdinal">The written row's catalog ordinal.</param>
    /// <returns>The work units.</returns>
    public static long LiveWrite(RuleCompileContext context, int rowOrdinal) => (((Door + BoardEntries) + (((context.FindRowAt(rowOrdinal: rowOrdinal) is not { } row) || ArenaLayout.HasTraits(row: row))
        ? ClockEntries
        : 0L
    )) + Dependents(
        context: context,
        rowOrdinal: rowOrdinal
    ));
    /// <summary>Returns the work reading and writing every lane of one member cell costs, vector components
    /// included.</summary>
    /// <param name="context">The compile context the row resolves against.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns>The work units.</returns>
    public static long Cell(RuleCompileContext context, int rowOrdinal) {
        var dimensions = Dimensions(
            context: context,
            rowOrdinal: rowOrdinal
        );

        return (2L * (CellLanes + ((dimensions > 0L)
            ? (1L + dimensions)
            : 0L
        )));
    }
    /// <summary>Returns the work a reorder of one row costs: the permutation checks, a swap of two cells per
    /// displaced position, and the membership rebuild.</summary>
    /// <param name="context">The compile context the row resolves against.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="members">The most members the row holds.</param>
    /// <returns>The work units.</returns>
    public static long Reorder(RuleCompileContext context, int rowOrdinal, long members) => (((6L * members) + ((2L * members) * Cell(
        context: context,
        rowOrdinal: rowOrdinal
    ))) + Membership(
        context: context,
        members: members,
        rowOrdinal: rowOrdinal
    ));
    /// <summary>Returns the work moving one member from one row to another costs beyond the cells either row shifts to
    /// make or close its gap: the key lookups, the carried vector, the admission, the vacated tail's clear, the stored
    /// cell, both member counts, and both membership rebuilds. A transfer refuses a full destination before it
    /// removes anything, so nothing is evicted.</summary>
    /// <param name="context">The compile context the rows resolve against.</param>
    /// <param name="fromOrdinal">The source row's catalog ordinal.</param>
    /// <param name="fromMembers">The most members the source holds.</param>
    /// <param name="toOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="toMembers">The most members the destination holds.</param>
    /// <returns>The work units.</returns>
    public static long Move(RuleCompileContext context, int fromOrdinal, long fromMembers, int toOrdinal, long toMembers) => (((((((3L * Door) + Dimensions(
        context: context,
        rowOrdinal: fromOrdinal
    )) + Cell(
        context: context,
        rowOrdinal: fromOrdinal
    )) + Cell(
        context: context,
        rowOrdinal: toOrdinal
    )) + 2L) + Membership(
        context: context,
        members: fromMembers,
        rowOrdinal: fromOrdinal
    )) + Membership(
        context: context,
        members: toMembers,
        rowOrdinal: toOrdinal
    ));
    /// <summary>Returns the work drawing up to <paramref name="samples"/> samples from one site's stream costs: the
    /// open's cursor and mask reads and its one seek, each sample, and the close's cursor and mask stores and the
    /// recorded sample. The site's seed was folded when the host was built, so no term here reads the instance's
    /// identity or the site's descriptor.</summary>
    /// <param name="context">The compile context the site's row resolves against.</param>
    /// <param name="rowOrdinal">The draw site row's catalog ordinal.</param>
    /// <param name="samples">The most samples one firing draws.</param>
    /// <returns>The work units.</returns>
    /// <remarks>A secret site takes one keyed sample per draw and seeks nothing. An extended source rebuilds its
    /// table when its cached generator is not already at the site's cursor, seeks the whole extended generator, and
    /// ticks its table at most once per <c>2^16</c> draws.</remarks>
    public static long Draws(RuleCompileContext context, int rowOrdinal, long samples) {
        var draw = context.FindRowAt(rowOrdinal: rowOrdinal)?.Draw;
        var generator = (((draw is not null) && GeneratorEngine.TryResolveSource(
            draw: draw,
            generator: out var resolved,
            generators: context.Generators,
            reason: out _
        ))
            ? resolved
            : null
        );
        var masks = ((generator is null)
            ? 0L
            : (4L * StateGenerator.MaskCount(generator: generator))
        );
        var edges = (((2L * Door) + (2L * masks)) + BoardWrite);

        if (draw?.Secret is not null) {
            return (edges + (samples * PrivateSample));
        }
        if (generator?.Extended is { } extended) {
            var k = ((long)extended.K);
            var rebuild = ((2L * k) + (extended.Script?.Count ?? 0));
            var extendedSeek = ((Seek + SeekSteps) + (k * Seek));

            return ((((edges + rebuild) + (2L * extendedSeek)) + (samples * (Sample + 1L))) + ((1L + (samples / TableTickPeriod)) * (2L * k)));
        }

        return ((edges + Seek) + (samples * Sample));
    }
    /// <summary>Returns the work one step of a compiled pattern costs: a binary search of its letter cuts and one
    /// transition.</summary>
    /// <param name="pattern">The compiled pattern.</param>
    /// <returns>The work units.</returns>
    public static long PatternStep(CompiledPattern pattern) => (1L + RuleWorkBudget.SearchSteps(count: pattern.LetterCount));

    // A keyed store moves at most the cell its token left and the cell it entered on every board it is the token row
    // of, and the one cell its token stands on for every board it is the code row of; each recomputed cell scans the
    // board's token row and stores two lanes.
    private static long Dependents(RuleCompileContext context, int rowOrdinal) {
        var name = Name(
            context: context,
            rowOrdinal: rowOrdinal
        );
        var units = 0L;

        foreach (var board in context.Rows) {
            if (board.Inverse is not { } inverse) {
                continue;
            }

            var scan = (context.RowCapacity(name: inverse.Tokens.Value) + BoardEntries);

            if (string.Equals(a: inverse.Tokens.Value, b: name, comparisonType: StringComparison.Ordinal)) {
                units += (2L * scan);
            }
            if (string.Equals(a: inverse.Codes.Value, b: name, comparisonType: StringComparison.Ordinal)) {
                units += scan;
            }
        }

        return units;
    }
    // A membership change reindexes the row's keys and rebuilds every board derived from it, each cell of which scans
    // the board's token row and stores two lanes.
    private static long Membership(RuleCompileContext context, int rowOrdinal, long members) {
        var name = Name(
            context: context,
            rowOrdinal: rowOrdinal
        );
        var units = (members + 1L);

        foreach (var board in context.Rows) {
            if (
                (board.Inverse is { } inverse) &&
                (string.Equals(a: inverse.Tokens.Value, b: name, comparisonType: StringComparison.Ordinal) ||
                string.Equals(a: inverse.Codes.Value, b: name, comparisonType: StringComparison.Ordinal))
            ) {
                units += (((long)board.CellCeiling) * (context.RowCapacity(name: inverse.Tokens.Value) + BoardEntries));
            }
        }

        return units;
    }
    private static long Dimensions(RuleCompileContext context, int rowOrdinal) => ((context.FindRowAt(rowOrdinal: rowOrdinal) is { Kind: CellKind.Vector } row)
        ? (context.FindSpace(name: row.Space)?.Identity.Dimensions ?? StateCapacity.MaxVectorDimensions)
        : 0L
    );
    private static string Name(RuleCompileContext context, int rowOrdinal) => ((((uint)rowOrdinal) < ((uint)context.Catalog.Count))
        ? context.Catalog.Descriptors[rowOrdinal].Name
        : string.Empty
    );
}
