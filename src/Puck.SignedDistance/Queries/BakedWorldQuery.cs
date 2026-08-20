using Puck.Maths;

namespace Puck.SignedDistance.Queries;

/// <summary>
/// Pure fixed-point <see cref="IWorldQuery"/> over a baked <see cref="WorldQueryArtifact"/> — the query-namespace
/// generalization of a flat walk grid, adding the heightfield layer and the cast/overlap verbs a walk grid never
/// needed, and keeping its "out of bounds reads as not blocked" blocked-bitmap contract. Every answer carries
/// <see cref="WorldQueryConfidence.Bounded"/> — a baked artifact is resolution-quantized by construction, never
/// sub-cell-exact.
/// <para>
/// <b>Positions are world-space.</b> A baked grid's origin is a world coordinate, so every position argument is
/// rebased against the world origin through <see cref="FixedPosition.TryDelta"/> — exact integer arithmetic, the
/// identity inside cell <c>(0,0,0)</c> — before it reaches a cell index. Two positions sharing a
/// <see cref="FixedPosition.Local"/> offset in different hierarchy cells therefore answer differently, as their
/// world separation demands. A position the world origin is farther than signed Q48.16 from is refused by parameter
/// name rather than answered against a coordinate the carrier cannot hold.
/// </para>
/// <para>
/// <b>Conservative by construction.</b> No verb samples the segment at discrete points. A cast enumerates every cell
/// whose column the swept volume can reach and intersects the segment with that cell's box analytically, so an
/// answer of "clear" means no cell in the artifact can be reached, not that no probe happened to land on one. Entry
/// parameters round down and exit parameters round up, so fixed-point truncation can only widen an interval, never
/// narrow it past the truth.
/// </para>
/// <para>
/// <b>Where the answer is deliberately loose.</b> A swept sphere is tested against each cell box dilated by the
/// radius on each axis — an axis-aligned dilation that contains the true rounded-rectangle sweep, so contact can be
/// reported up to <c>radius * (sqrt(2) - 1)</c> early at a box corner. <see cref="Overlap"/> uses the exact
/// clamp-to-solid Euclidean test instead, so it is the tighter of the two; a cast never reports clear where
/// <see cref="Overlap"/> reports blocked.
/// </para>
/// <para>
/// <b>The 2.5D meaning of Y.</b> A blocked cell comes from a footprint with no height
/// (<see cref="WorldQueryBlockerInput"/>) and therefore blocks at every Y — an infinite vertical column. The height
/// layer is the half-space below a cell's authored ground, and blocks where the query volume's lowest point reaches
/// at or below it. Both layers answer every verb — casts, <see cref="LineOfSight"/>, and <see cref="Overlap"/> —
/// which is the fallback <see cref="QueryCapabilities"/> describes: an artifact carrying only one layer still
/// answers with it.
/// </para>
/// <para>
/// <b>A radius is body-scale, and the bound is enforced.</b> Both radius-taking verbs walk every cell the radius
/// reaches, which is quadratic in it; a radius spanning more than <see cref="MaxRadiusCells"/> cells
/// (<see cref="MaxRadius"/> world units for a given artifact) is refused by parameter name rather than silently
/// clamped or silently paid for. Nothing here indexes occupancy hierarchically, so a consumer that genuinely needs a
/// wider query is a request for that index, and a named refusal is how it arrives.
/// </para>
/// </summary>
public sealed class BakedWorldQuery : IWorldQuery {
    private readonly WorldQueryArtifact m_artifact;

    /// <summary>The widest query radius the cell walk accepts, in cells — the ceiling
    /// <see cref="Overlap"/> and <see cref="SphereCast"/> refuse past.</summary>
    public const int MaxRadiusCells = 64;

    /// <summary>Wraps a baked artifact.</summary>
    /// <param name="artifact">The baked artifact to query.</param>
    /// <exception cref="ArgumentNullException"><paramref name="artifact"/> is <see langword="null"/>.</exception>
    public BakedWorldQuery(WorldQueryArtifact artifact) {
        ArgumentNullException.ThrowIfNull(argument: artifact);

        m_artifact = artifact;
    }

    /// <inheritdoc/>
    public QueryCapabilities Capabilities => new(
        HasBlocked: m_artifact.HasBlocked,
        HasHeightfield: m_artifact.HasHeightfield,
        HasOccupancy: false
    );
    /// <summary>Gets the widest radius <see cref="Overlap"/> and <see cref="SphereCast"/> accept against this
    /// artifact — <see cref="MaxRadiusCells"/> of its own cells, saturating at the scalar carrier.</summary>
    public FixedQ4816 MaxRadius => FixedQ4816.FromRawBits(value: ClampToLong(value: (((Int128)MaxRadiusCells) * m_artifact.CellSizeRaw)));

    // Saturating raw addition: a sum overflows exactly when both addends share a sign the sum does not.
    private static long AddRawSaturating(long left, long right) {
        var sum = unchecked((left + right));

        return ((((left ^ sum) & (right ^ sum)) < 0L)
            ? ((left < 0L) ? long.MinValue : long.MaxValue)
            : sum
        );
    }
    private static int ClampIndex(Int128 value, int maximum) =>
        ((value < Int128.Zero)
            ? 0
            : ((value > maximum)
                ? maximum
                : ((int)value)
            )
        );
    private static long ClampToLong(Int128 value) =>
        ((value < long.MinValue)
            ? long.MinValue
            : ((value > long.MaxValue)
                ? long.MaxValue
                : ((long)value)
            )
        );
    // Directed Q48.16 division: the floor (roundUp false) or the ceiling (roundUp true) of numerator/denominator,
    // exact whenever the quotient is exact, saturating at the carrier. Every slab entry takes the floor and every
    // slab exit the ceiling, which is what keeps truncation from closing an interval the segment really enters.
    private static long DivideRawDirected(long numeratorRaw, long denominatorRaw, bool roundUp) {
        var highBits = (numeratorRaw >> 47);

        // A numerator narrower than 48 signed bits — every coordinate difference a room-scale grid can produce —
        // shifts into a long without loss, and a 64-bit divide is an order of magnitude cheaper than a 128-bit one.
        if (
            (highBits == 0L) ||
            (highBits == -1L)
        ) {
            var narrowNumerator = (numeratorRaw << 16);
            var narrowQuotient = (narrowNumerator / denominatorRaw);
            var narrowRemainder = (narrowNumerator % denominatorRaw);

            if (narrowRemainder != 0L) {
                var narrowPositive = ((narrowRemainder > 0L) == (denominatorRaw > 0L));

                if (roundUp) {
                    if (narrowPositive) {
                        narrowQuotient++;
                    }
                } else if (!narrowPositive) {
                    narrowQuotient--;
                }
            }

            return narrowQuotient;
        }

        var numerator = ((((Int128)numeratorRaw)) << 16);
        var denominator = ((Int128)denominatorRaw);
        var quotient = (numerator / denominator);
        var remainder = (numerator % denominator);

        if (remainder != Int128.Zero) {
            var positive = ((numerator > Int128.Zero) == (denominatorRaw > 0L));

            if (roundUp) {
                if (positive) {
                    quotient += Int128.One;
                }
            } else if (!positive) {
                quotient -= Int128.One;
            }
        }

        return ClampToLong(value: quotient);
    }
    // Intersects the sweep with the axis slab [lowRaw, highRaw], narrowing [enterRaw, exitRaw] in place. Returns
    // false once the running interval is empty, which is the "this cell cannot be reached" answer.
    private static bool NarrowSlab(long originRaw, long directionRaw, long lowRaw, long highRaw, ref long enterRaw, ref long exitRaw) {
        if (directionRaw == 0L) {
            return (
                (originRaw >= lowRaw) &&
                (originRaw <= highRaw)
            );
        }

        var nearBoundRaw = ((directionRaw > 0L) ? lowRaw : highRaw);
        var farBoundRaw = ((directionRaw > 0L) ? highRaw : lowRaw);
        var nearRaw = DivideRawDirected(
            denominatorRaw: directionRaw,
            numeratorRaw: SubtractRawSaturating(
                left: nearBoundRaw,
                right: originRaw
            ),
            roundUp: false
        );
        var farRaw = DivideRawDirected(
            denominatorRaw: directionRaw,
            numeratorRaw: SubtractRawSaturating(
                left: farBoundRaw,
                right: originRaw
            ),
            roundUp: true
        );

        if (nearRaw > enterRaw) {
            enterRaw = nearRaw;
        }

        if (farRaw < exitRaw) {
            exitRaw = farRaw;
        }

        return (enterRaw <= exitRaw);
    }
    private static long ScaleRawSaturating(long valueRaw, long scaleRaw) =>
        ClampToLong(value: (((((Int128)valueRaw)) * scaleRaw) >> 16));
    // Saturating raw subtraction: a difference overflows exactly when the operands differ in sign and the result
    // takes the subtrahend's.
    private static long SubtractRawSaturating(long left, long right) {
        var difference = unchecked((left - right));

        return ((((left ^ right) & (left ^ difference)) < 0L)
            ? ((left < 0L) ? long.MinValue : long.MaxValue)
            : difference
        );
    }
    // The artifact's grid lives in world space, so a query point is its exact displacement from the world origin —
    // never its raw .Local, which repeats every cell and would answer for whichever copy of the grid the caller's
    // cell happens to be. The rebase is exact integer arithmetic and the identity inside cell (0,0,0).
    private static FixedVector3 WorldOf(FixedPosition position, string paramName) {
        if (!position.TryDelta(
            delta: out var world,
            origin: FixedPosition.Zero
        )) {
            throw new ArgumentOutOfRangeException(
                actualValue: position,
                message: "The position's displacement from the world origin is outside signed Q48.16, so it has no world coordinate this artifact's grid can be indexed by.",
                paramName: paramName
            );
        }

        return world;
    }
    // The exact clamp-to-solid Euclidean test, run against both layers in one walk: a blocked cell is the cell box
    // extruded through every Y, and an authored ground is the same box's half-space at or below its height. Each
    // squared term is spent against a running budget rather than summed, so no product can exceed the widened
    // carrier however far the query's Y sits from the terrain.
    private bool AnySolidWithinRadius(FixedVector3 center, long radiusRaw) {
        if (
            !m_artifact.HasBlocked &&
            !m_artifact.HasHeightfield
        ) {
            return false;
        }

        // The disc is clamped to the artifact rather than rejected when its center falls outside: an in-bounds cell
        // the disc provably covers is blocked whether or not the center itself is on the grid.
        if (!TryColumnSpan(
            first: out var firstColumn,
            highRaw: AddRawSaturating(
                left: center.X.Value,
                right: radiusRaw
            ),
            last: out var lastColumn,
            lowRaw: SubtractRawSaturating(
                left: center.X.Value,
                right: radiusRaw
            )
        )) {
            return false;
        }

        if (!TryRowSpan(
            first: out var firstRow,
            highRaw: AddRawSaturating(
                left: center.Z.Value,
                right: radiusRaw
            ),
            last: out var lastRow,
            lowRaw: SubtractRawSaturating(
                left: center.Z.Value,
                right: radiusRaw
            )
        )) {
            return false;
        }

        var radiusSquared = ((((Int128)radiusRaw)) * radiusRaw);

        for (var row = firstRow; (row <= lastRow); row++) {
            var cellMinZRaw = RowMinZRaw(row: row);
            var closestZRaw = Math.Clamp(
                max: (cellMinZRaw + m_artifact.CellSizeRaw),
                min: cellMinZRaw,
                value: center.Z.Value
            );
            var dz = ((((Int128)center.Z.Value)) - closestZRaw);

            if (
                (dz > radiusRaw) ||
                (dz < (-((Int128)radiusRaw)))
            ) {
                continue;
            }

            var afterZ = (radiusSquared - (dz * dz));

            for (var column = firstColumn; (column <= lastColumn); column++) {
                var cellIndex = ((row * m_artifact.Width) + column);
                var blocked = m_artifact.IsBlockedCell(cellIndex: cellIndex);
                var grounded = m_artifact.TryHeightRaw(
                    cellIndex: cellIndex,
                    heightRaw: out var heightRaw
                );

                if (
                    !blocked &&
                    !grounded
                ) {
                    continue;
                }

                var cellMinXRaw = ColumnMinXRaw(column: column);
                var closestXRaw = Math.Clamp(
                    max: (cellMinXRaw + m_artifact.CellSizeRaw),
                    min: cellMinXRaw,
                    value: center.X.Value
                );
                var dx = ((((Int128)center.X.Value)) - closestXRaw);

                if (
                    (dx > radiusRaw) ||
                    (dx < (-((Int128)radiusRaw)))
                ) {
                    continue;
                }

                var afterX = (afterZ - (dx * dx));

                if (afterX < Int128.Zero) {
                    continue;
                }

                if (blocked) {
                    return true;
                }

                if (!grounded) {
                    continue;
                }

                // Above the authored ground the vertical gap counts; at or below it the center is inside the solid
                // half-space and the planar test alone decides.
                var dy = ((((Int128)center.Y.Value)) - heightRaw);

                if (dy <= Int128.Zero) {
                    return true;
                }

                if (
                    (dy <= radiusRaw) &&
                    ((dy * dy) <= afterX)
                ) {
                    return true;
                }
            }
        }

        return false;
    }
    private void CheckRadius(FixedQ4816 radius) {
        var limit = (((Int128)MaxRadiusCells) * m_artifact.CellSizeRaw);

        if (radius.Value > limit) {
            throw new ArgumentOutOfRangeException(
                actualValue: radius,
                message: $"A query radius may span at most {MaxRadiusCells} cells of this artifact ({MaxRadius} world units); the cell walk it drives is quadratic in the radius and this provider carries no occupancy hierarchy.",
                paramName: nameof(radius)
            );
        }
    }
    private long ColumnMinXRaw(int column) =>
        AddRawSaturating(
            left: m_artifact.OriginXRaw,
            right: (((long)column) * m_artifact.CellSizeRaw)
        );
    // Enumerates the cells the swept volume can reach, column by column in sweep order, and keeps the nearest
    // contact. A column contributes nothing beyond the running best once the sweep cannot enter it earlier than that
    // best, which is the whole early-out: no cell is visited twice and no cell outside the swept band is visited.
    private bool March(FixedVector3 origin, FixedVector3 dir, FixedQ4816 maxDist, FixedQ4816 radius, out RayHit hit) {
        hit = default;

        var direction = dir.Normalize();

        if (
            (direction == FixedVector3.Zero) ||
            (maxDist <= FixedQ4816.Zero)
        ) {
            return false;
        }

        if (
            !m_artifact.HasBlocked &&
            !m_artifact.HasHeightfield
        ) {
            return false;
        }

        var sweep = new Sweep(
            direction: direction,
            maxDistanceRaw: maxDist.Value,
            origin: origin,
            radiusRaw: Math.Max(
                val1: 0L,
                val2: radius.Value
            )
        );

        if (!TryColumnSpan(
            first: out var firstColumn,
            highRaw: sweep.MaxXRaw,
            last: out var lastColumn,
            lowRaw: sweep.MinXRaw
        )) {
            return false;
        }

        var contact = default(Contact);
        var step = ((sweep.DirectionXRaw < 0L) ? -1 : 1);

        for (var column = ((step < 0) ? lastColumn : firstColumn); ((column >= firstColumn) && (column <= lastColumn)); column += step) {
            if (!TryColumnWindow(
                column: column,
                enterRaw: out var windowEnterRaw,
                exitRaw: out var windowExitRaw,
                sweep: sweep
            )) {
                continue;
            }

            if (
                contact.Found &&
                (
                    (contact.DistanceRaw <= 0L) ||
                    (
                        (sweep.DirectionXRaw != 0L) &&
                        (windowEnterRaw > contact.DistanceRaw)
                    )
                )
            ) {
                break;
            }

            ScanColumn(
                column: column,
                contact: ref contact,
                sweep: sweep,
                windowEnterRaw: windowEnterRaw,
                windowExitRaw: windowExitRaw
            );
        }

        if (!contact.Found) {
            return false;
        }

        hit = BuildHit(
            contact: contact,
            sweep: sweep
        );

        return true;
    }
    private RayHit BuildHit(in Contact contact, in Sweep sweep) {
        var cellMinXRaw = ColumnMinXRaw(column: contact.Column);
        var cellMinZRaw = RowMinZRaw(row: contact.Row);
        var centerXRaw = AddRawSaturating(
            left: sweep.OriginXRaw,
            right: ScaleRawSaturating(
                scaleRaw: contact.DistanceRaw,
                valueRaw: sweep.DirectionXRaw
            )
        );
        var centerYRaw = AddRawSaturating(
            left: sweep.OriginYRaw,
            right: ScaleRawSaturating(
                scaleRaw: contact.DistanceRaw,
                valueRaw: sweep.DirectionYRaw
            )
        );
        var centerZRaw = AddRawSaturating(
            left: sweep.OriginZRaw,
            right: ScaleRawSaturating(
                scaleRaw: contact.DistanceRaw,
                valueRaw: sweep.DirectionZRaw
            )
        );

        // The contact point is the touched geometry's own surface, not the sweeping center: the center clamped onto
        // the cell box for the XZ pair, and the authored ground height for a heightfield contact.
        return new RayHit(
            Confidence: WorldQueryConfidence.Bounded,
            Distance: FixedQ4816.FromRawBits(value: contact.DistanceRaw),
            Material: -1,
            Normal: new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.One,
                Z: FixedQ4816.Zero
            ),
            Point: FixedPosition.FromLocal(local: new FixedVector3(
                X: FixedQ4816.FromRawBits(value: Math.Clamp(
                    max: (cellMinXRaw + m_artifact.CellSizeRaw),
                    min: cellMinXRaw,
                    value: centerXRaw
                )),
                Y: FixedQ4816.FromRawBits(value: (contact.Ground ? contact.HeightRaw : centerYRaw)),
                Z: FixedQ4816.FromRawBits(value: Math.Clamp(
                    max: (cellMinZRaw + m_artifact.CellSizeRaw),
                    min: cellMinZRaw,
                    value: centerZRaw
                ))
            ))
        );
    }
    private long RowMinZRaw(int row) =>
        AddRawSaturating(
            left: m_artifact.OriginZRaw,
            right: (((long)row) * m_artifact.CellSizeRaw)
        );
    private void ScanColumn(in Sweep sweep, int column, long windowEnterRaw, long windowExitRaw, ref Contact contact) {
        // Both coordinates are linear in the sweep parameter, so the Z the sweep can reach inside this column is
        // bounded by its values at the window's two ends, dilated by the radius. One tick of slack absorbs the
        // rounding of the two scaled endpoints.
        var windowMinZRaw = AddRawSaturating(
            left: sweep.OriginZRaw,
            right: ScaleRawSaturating(
                scaleRaw: windowEnterRaw,
                valueRaw: sweep.DirectionZRaw
            )
        );
        var windowMaxZRaw = AddRawSaturating(
            left: sweep.OriginZRaw,
            right: ScaleRawSaturating(
                scaleRaw: windowExitRaw,
                valueRaw: sweep.DirectionZRaw
            )
        );

        if (windowMinZRaw > windowMaxZRaw) {
            (windowMinZRaw, windowMaxZRaw) = (windowMaxZRaw, windowMinZRaw);
        }

        if (!TryRowSpan(
            first: out var firstRow,
            highRaw: AddRawSaturating(
                left: windowMaxZRaw,
                right: (sweep.RadiusRaw + 1L)
            ),
            last: out var lastRow,
            lowRaw: SubtractRawSaturating(
                left: windowMinZRaw,
                right: (sweep.RadiusRaw + 1L)
            )
        )) {
            return;
        }

        for (var row = firstRow; (row <= lastRow); row++) {
            var cellIndex = ((row * m_artifact.Width) + column);
            var blocked = m_artifact.IsBlockedCell(cellIndex: cellIndex);
            var grounded = m_artifact.TryHeightRaw(
                cellIndex: cellIndex,
                heightRaw: out var heightRaw
            );

            if (
                !blocked &&
                !grounded
            ) {
                continue;
            }

            var cellMinZRaw = RowMinZRaw(row: row);
            // The column window IS this cell's dilated X slab — every cell in the column shares it — so the X
            // narrowing is already done and only the Z and Y axes are left.
            var enterRaw = windowEnterRaw;
            var exitRaw = windowExitRaw;

            if (!NarrowSlab(
                directionRaw: sweep.DirectionZRaw,
                enterRaw: ref enterRaw,
                exitRaw: ref exitRaw,
                highRaw: AddRawSaturating(
                    left: (cellMinZRaw + m_artifact.CellSizeRaw),
                    right: sweep.RadiusRaw
                ),
                lowRaw: SubtractRawSaturating(
                    left: cellMinZRaw,
                    right: sweep.RadiusRaw
                ),
                originRaw: sweep.OriginZRaw
            )) {
                continue;
            }

            if (blocked) {
                contact.Consider(
                    column: column,
                    distanceRaw: enterRaw,
                    ground: false,
                    heightRaw: 0L,
                    row: row
                );
            }

            if (!grounded) {
                continue;
            }

            var groundEnterRaw = enterRaw;
            var groundExitRaw = exitRaw;

            // Ground contact is the same slab narrowing against the half-space "the swept volume's lowest point is
            // at or below the authored height", so a sphere grounds one radius above the terrain, not centered in it.
            if (NarrowSlab(
                directionRaw: sweep.DirectionYRaw,
                enterRaw: ref groundEnterRaw,
                exitRaw: ref groundExitRaw,
                highRaw: AddRawSaturating(
                    left: heightRaw,
                    right: sweep.RadiusRaw
                ),
                lowRaw: long.MinValue,
                originRaw: sweep.OriginYRaw
            )) {
                contact.Consider(
                    column: column,
                    distanceRaw: groundEnterRaw,
                    ground: true,
                    heightRaw: heightRaw,
                    row: row
                );
            }
        }
    }
    private bool TryCellIndex(FixedQ4816 x, FixedQ4816 z, out int cellIndex) {
        cellIndex = -1;

        if (
            (m_artifact.Width <= 0) ||
            (m_artifact.Height <= 0)
        ) {
            return false;
        }

        var columnLong = (x.Value - m_artifact.OriginXRaw).FloorDivide(divisor: m_artifact.CellSizeRaw);
        var rowLong = (z.Value - m_artifact.OriginZRaw).FloorDivide(divisor: m_artifact.CellSizeRaw);

        if (
            (columnLong < 0L) ||
            (columnLong >= m_artifact.Width) ||
            (rowLong < 0L) ||
            (rowLong >= m_artifact.Height)
        ) {
            return false;
        }

        cellIndex = ((((int)rowLong) * m_artifact.Width) + ((int)columnLong));

        return true;
    }
    private bool TryColumnSpan(long lowRaw, long highRaw, out int first, out int last) =>
        TryIndexSpan(
            axisCells: m_artifact.Width,
            first: out first,
            highRaw: highRaw,
            last: out last,
            lowRaw: lowRaw,
            originRaw: m_artifact.OriginXRaw
        );
    private bool TryColumnWindow(in Sweep sweep, int column, out long enterRaw, out long exitRaw) {
        var cellMinXRaw = ColumnMinXRaw(column: column);

        enterRaw = 0L;
        exitRaw = sweep.MaxDistanceRaw;

        return NarrowSlab(
            directionRaw: sweep.DirectionXRaw,
            enterRaw: ref enterRaw,
            exitRaw: ref exitRaw,
            highRaw: AddRawSaturating(
                left: (cellMinXRaw + m_artifact.CellSizeRaw),
                right: sweep.RadiusRaw
            ),
            lowRaw: SubtractRawSaturating(
                left: cellMinXRaw,
                right: sweep.RadiusRaw
            ),
            originRaw: sweep.OriginXRaw
        );
    }
    private bool TryIndexSpan(long originRaw, int axisCells, long lowRaw, long highRaw, out int first, out int last) {
        first = 0;
        last = -1;

        if (axisCells <= 0) {
            return false;
        }

        var cellSize = ((Int128)m_artifact.CellSizeRaw);
        var lowIndex = ((((Int128)lowRaw) - originRaw).FloorDivide(divisor: cellSize));
        var highIndex = ((((Int128)highRaw) - originRaw).FloorDivide(divisor: cellSize));

        if (
            (highIndex < Int128.Zero) ||
            (lowIndex > (axisCells - 1))
        ) {
            return false;
        }

        first = ClampIndex(
            maximum: (axisCells - 1),
            value: lowIndex
        );
        last = ClampIndex(
            maximum: (axisCells - 1),
            value: highIndex
        );

        return true;
    }
    private bool TryRowSpan(long lowRaw, long highRaw, out int first, out int last) =>
        TryIndexSpan(
            axisCells: m_artifact.Height,
            first: out first,
            highRaw: highRaw,
            last: out last,
            lowRaw: lowRaw,
            originRaw: m_artifact.OriginZRaw
        );

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException">An endpoint, or the segment between them, is outside signed
    /// Q48.16 of the world origin.</exception>
    public bool LineOfSight(FixedPosition from, FixedPosition to) {
        var fromWorld = WorldOf(
            paramName: nameof(from),
            position: from
        );

        _ = WorldOf(
            paramName: nameof(to),
            position: to
        );

        if (!to.TryDelta(
            delta: out var delta,
            origin: from
        )) {
            throw new ArgumentOutOfRangeException(
                actualValue: to,
                message: "The segment between the two endpoints is longer than signed Q48.16 represents.",
                paramName: nameof(to)
            );
        }

        var reach = delta.Length;

        if (reach <= FixedQ4816.Zero) {
            // A degenerate segment is exactly the question "is this one point reachable", which the shortest
            // possible cast answers without a second code path.
            return !March(
                dir: new FixedVector3(
                    X: FixedQ4816.Zero,
                    Y: FixedQ4816.One,
                    Z: FixedQ4816.Zero
                ),
                hit: out _,
                maxDist: FixedQ4816.Epsilon,
                origin: fromWorld,
                radius: FixedQ4816.Zero
            );
        }

        // The march runs along a Q48.16 unit vector whose per-component quantization walks the far endpoint off by
        // roughly one tick per world unit of reach. Extending the reach by four times that bound keeps the requested
        // endpoint inside the marched interval, so a blocker sitting on it still blocks.
        return !March(
            dir: delta,
            hit: out _,
            maxDist: FixedQ4816.FromRawBits(value: AddRawSaturating(
                left: reach.Value,
                right: ((reach.Value >> 14) + 64L)
            )),
            origin: fromWorld,
            radius: FixedQ4816.Zero
        );
    }
    /// <inheritdoc/>
    /// <remarks>Consults both layers: a blocked cell overlaps at every Y, and an authored ground overlaps wherever
    /// the sphere reaches at or below it. The test is the exact Euclidean distance to the solid, so it is never
    /// looser than the cast verbs' axis-aligned dilation.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="center"/> is outside signed Q48.16 of the world
    /// origin, or <paramref name="radius"/> spans more than <see cref="MaxRadiusCells"/> cells.</exception>
    public bool Overlap(FixedPosition center, FixedQ4816 radius) {
        CheckRadius(radius: radius);

        return AnySolidWithinRadius(
            center: WorldOf(
                paramName: nameof(center),
                position: center
            ),
            radiusRaw: Math.Max(
                val1: 0L,
                val2: radius.Value
            )
        );
    }
    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="origin"/> is outside signed Q48.16 of the world
    /// origin.</exception>
    public bool Raycast(FixedPosition origin, FixedVector3 dir, FixedQ4816 maxDist, out RayHit hit) =>
        March(
            dir: dir,
            hit: out hit,
            maxDist: maxDist,
            origin: WorldOf(
                paramName: nameof(origin),
                position: origin
            ),
            radius: FixedQ4816.Zero
        );
    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="origin"/> is outside signed Q48.16 of the world
    /// origin, or <paramref name="radius"/> spans more than <see cref="MaxRadiusCells"/> cells.</exception>
    public bool SphereCast(FixedPosition origin, FixedVector3 dir, FixedQ4816 radius, FixedQ4816 maxDist, out RayHit hit) {
        CheckRadius(radius: radius);

        return March(
            dir: dir,
            hit: out hit,
            maxDist: maxDist,
            origin: WorldOf(
                paramName: nameof(origin),
                position: origin
            ),
            radius: radius
        );
    }
    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is outside signed Q48.16 of the
    /// world origin.</exception>
    public bool TryGroundHeight(FixedPosition position, FixedQ4816 probeUp, FixedQ4816 probeDown, out FixedQ4816 groundY) {
        groundY = FixedQ4816.Zero;

        var world = WorldOf(
            paramName: nameof(position),
            position: position
        );

        if (
            !m_artifact.HasHeightfield ||
            !TryCellIndex(
                cellIndex: out var cellIndex,
                x: world.X,
                z: world.Z
            ) ||
            !m_artifact.TryHeightRaw(
                cellIndex: cellIndex,
                heightRaw: out var raw
            )
        ) {
            return false;
        }

        var candidate = FixedQ4816.FromRawBits(value: raw);

        if (
            (candidate < (world.Y - probeDown)) ||
            (candidate > (world.Y + probeUp))
        ) {
            return false;
        }

        groundY = candidate;

        return true;
    }

    // The nearest contact found so far. Ties keep the first considered, which fixes an order the column/row walk
    // already fixes, so the answer does not depend on how the band happened to be enumerated.
    private struct Contact {
        public int Column;
        public long DistanceRaw;
        public bool Found;
        public bool Ground;
        public long HeightRaw;
        public int Row;

        public void Consider(long distanceRaw, int column, int row, bool ground, long heightRaw) {
            if (
                Found &&
                (distanceRaw >= DistanceRaw)
            ) {
                return;
            }

            Column = column;
            DistanceRaw = distanceRaw;
            Found = true;
            Ground = ground;
            HeightRaw = heightRaw;
            Row = row;
        }
    }
    // One swept query in raw Q48.16: an origin, a unit direction, a sweep length, and the swept sphere's radius
    // (zero for a ray). MinXRaw/MaxXRaw bound the X the swept volume can reach over the whole sweep.
    private readonly struct Sweep {
        public readonly long DirectionXRaw;
        public readonly long DirectionYRaw;
        public readonly long DirectionZRaw;
        public readonly long MaxDistanceRaw;
        public readonly long MaxXRaw;
        public readonly long MinXRaw;
        public readonly long OriginXRaw;
        public readonly long OriginYRaw;
        public readonly long OriginZRaw;
        public readonly long RadiusRaw;

        public Sweep(FixedVector3 origin, FixedVector3 direction, long maxDistanceRaw, long radiusRaw) {
            var endXRaw = AddRawSaturating(
                left: origin.X.Value,
                right: ScaleRawSaturating(
                    scaleRaw: maxDistanceRaw,
                    valueRaw: direction.X.Value
                )
            );

            DirectionXRaw = direction.X.Value;
            DirectionYRaw = direction.Y.Value;
            DirectionZRaw = direction.Z.Value;
            MaxDistanceRaw = maxDistanceRaw;
            MaxXRaw = AddRawSaturating(
                left: Math.Max(
                    val1: origin.X.Value,
                    val2: endXRaw
                ),
                right: (radiusRaw + 1L)
            );
            MinXRaw = SubtractRawSaturating(
                left: Math.Min(
                    val1: origin.X.Value,
                    val2: endXRaw
                ),
                right: (radiusRaw + 1L)
            );
            OriginXRaw = origin.X.Value;
            OriginYRaw = origin.Y.Value;
            OriginZRaw = origin.Z.Value;
            RadiusRaw = radiusRaw;
        }
    }
}
