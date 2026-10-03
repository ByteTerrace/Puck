namespace Puck.SignedDistance.Illumination;

/// <summary>One level of the radiance cache: a lattice of probes at one spacing.</summary>
/// <param name="Name">The level's name, as a document names it.</param>
/// <param name="Spacing">The distance, in world units, between neighbouring probes.</param>
/// <param name="Reach">The distance, in world units, a probe's ray travels before it continues into the next coarser
/// level; zero for the coarsest level, whose rays travel to the far distance.</param>
/// <param name="Radius">The distance, in world units, from a camera within which the level allocates bricks; zero for
/// no radius, which allocates every brick near geometry.</param>
/// <param name="Strata">The count of 64-direction strata each probe traces.</param>
public sealed record IrradianceLevel(string Name, double Spacing, double Reach, double Radius, int Strata) {
    /// <summary>Gets the count of rays each probe traces.</summary>
    public int Rays => (Strata * IrradianceLattice.RaysPerStratum);
}
/// <summary>A probe of a level: the level's index and the probe's integer lattice coordinate, whose world position is the
/// coordinate times the level's spacing.</summary>
/// <param name="Level">The level's index, zero the finest.</param>
/// <param name="X">The lattice X coordinate.</param>
/// <param name="Y">The lattice Y coordinate.</param>
/// <param name="Z">The lattice Z coordinate.</param>
public readonly record struct IrradianceProbeKey(int Level, int X, int Y, int Z) : IComparable<IrradianceProbeKey> {
    /// <inheritdoc/>
    public int CompareTo(IrradianceProbeKey other) {
        var order = Level.CompareTo(value: other.Level);

        if (order == 0) {
            order = Z.CompareTo(value: other.Z);
        }

        if (order == 0) {
            order = Y.CompareTo(value: other.Y);
        }

        return ((order == 0) ? X.CompareTo(value: other.X) : order);
    }
}
/// <summary>A brick of a level: <see cref="IrradianceLattice.BrickProbes"/> probes a side, the unit of allocation.</summary>
/// <param name="Level">The level's index, zero the finest.</param>
/// <param name="X">The brick X coordinate.</param>
/// <param name="Y">The brick Y coordinate.</param>
/// <param name="Z">The brick Z coordinate.</param>
public readonly record struct IrradianceBrickKey(int Level, int X, int Y, int Z) : IComparable<IrradianceBrickKey> {
    /// <inheritdoc/>
    public int CompareTo(IrradianceBrickKey other) {
        var order = Level.CompareTo(value: other.Level);

        if (order == 0) {
            order = Z.CompareTo(value: other.Z);
        }

        if (order == 0) {
            order = Y.CompareTo(value: other.Y);
        }

        return ((order == 0) ? X.CompareTo(value: other.X) : order);
    }
}
/// <summary>
/// The radiance cache's layout as pure functions: probes, bricks and cells of a level, the corner order and the 28
/// corner pairs of a cell, each probe's ray directions, and the octahedral texel maps with their borders. The GPU reads
/// the same layout; these functions are its reference.
/// </summary>
public static class IrradianceLattice {
    /// <summary>The probes along each side of a brick.</summary>
    public const int BrickProbes = 4;
    /// <summary>The rays in one stratum, one 64-lane workgroup's worth.</summary>
    public const int RaysPerStratum = 64;
    /// <summary>The interior texels along each side of an octahedral map.</summary>
    public const int InteriorTexels = 6;
    /// <summary>The texels along each side of an octahedral map with its one-texel border.</summary>
    public const int BorderedTexels = (InteriorTexels + 2);
    /// <summary>The corners of a cell.</summary>
    public const int CellCorners = 8;
    /// <summary>The corner pairs of a cell: 12 edges, 12 face diagonals and 4 body diagonals.</summary>
    public const int CellSegments = 28;
    /// <summary>The orientations a probe's directions take: the symmetries of the cube, which permute and negate axes
    /// and so rotate a direction exactly on every machine.</summary>
    public const int Orientations = 48;

    private static readonly (int First, int Second)[] Segments = BuildSegments();

    /// <summary>Returns a probe's world position on its lattice, before any relocation.</summary>
    /// <param name="key">The probe.</param>
    /// <param name="level">The probe's level.</param>
    /// <returns>The position, in world units.</returns>
    public static Double3 Position(IrradianceProbeKey key, IrradianceLevel level) => new(
        X: (key.X * level.Spacing),
        Y: (key.Y * level.Spacing),
        Z: (key.Z * level.Spacing)
    );
    /// <summary>Returns the brick holding a probe.</summary>
    /// <param name="key">The probe.</param>
    /// <returns>The brick.</returns>
    public static IrradianceBrickKey BrickOf(IrradianceProbeKey key) => new(
        Level: key.Level,
        X: FloorDivide(value: key.X, divisor: BrickProbes),
        Y: FloorDivide(value: key.Y, divisor: BrickProbes),
        Z: FloorDivide(value: key.Z, divisor: BrickProbes)
    );
    /// <summary>Returns a brick's probes in lattice order.</summary>
    /// <param name="brick">The brick.</param>
    /// <returns>Its <see cref="BrickProbes"/> cubed probes.</returns>
    public static IEnumerable<IrradianceProbeKey> ProbesOf(IrradianceBrickKey brick) {
        for (var z = 0; (z < BrickProbes); z++) {
            for (var y = 0; (y < BrickProbes); y++) {
                for (var x = 0; (x < BrickProbes); x++) {
                    yield return new IrradianceProbeKey(
                        Level: brick.Level,
                        X: ((brick.X * BrickProbes) + x),
                        Y: ((brick.Y * BrickProbes) + y),
                        Z: ((brick.Z * BrickProbes) + z)
                    );
                }
            }
        }
    }
    /// <summary>Returns the world-space box a brick's probes span, from its first probe to one spacing past its last, so
    /// the boxes of neighbouring bricks tile the level.</summary>
    /// <param name="brick">The brick.</param>
    /// <param name="level">The brick's level.</param>
    /// <param name="min">The box's least corner, in world units.</param>
    /// <param name="max">The box's greatest corner, in world units.</param>
    public static void BrickBox(IrradianceBrickKey brick, IrradianceLevel level, out Double3 min, out Double3 max) {
        var side = (BrickProbes * level.Spacing);

        min = new Double3(X: (brick.X * side), Y: (brick.Y * side), Z: (brick.Z * side));
        max = (min + new Double3(X: side, Y: side, Z: side));
    }
    /// <summary>Returns the cell holding a point: the probe at its least corner.</summary>
    /// <param name="point">The point, in world units.</param>
    /// <param name="levelIndex">The level's index.</param>
    /// <param name="level">The level.</param>
    /// <returns>The cell's least corner.</returns>
    public static IrradianceProbeKey CellOf(Double3 point, int levelIndex, IrradianceLevel level) => new(
        Level: levelIndex,
        X: ((int)Math.Floor(d: (point.X / level.Spacing))),
        Y: ((int)Math.Floor(d: (point.Y / level.Spacing))),
        Z: ((int)Math.Floor(d: (point.Z / level.Spacing)))
    );
    /// <summary>Returns a cell's corner: bit 0 of <paramref name="corner"/> steps X, bit 1 Y and bit 2 Z.</summary>
    /// <param name="cell">The cell's least corner.</param>
    /// <param name="corner">The corner, 0 to 7.</param>
    /// <returns>The corner's probe.</returns>
    public static IrradianceProbeKey Corner(IrradianceProbeKey cell, int corner) => new(
        Level: cell.Level,
        X: (cell.X + (corner & 1)),
        Y: (cell.Y + ((corner >> 1) & 1)),
        Z: (cell.Z + ((corner >> 2) & 1))
    );
    /// <summary>Returns a cell's corner pair: every pair of the eight corners once, in a fixed order.</summary>
    /// <param name="segment">The pair, 0 to 27.</param>
    /// <returns>The pair's corners.</returns>
    public static (int First, int Second) Segment(int segment) => Segments[segment];
    /// <summary>Returns the trilinear weight of a cell corner for a point.</summary>
    /// <param name="cell">The cell's least corner.</param>
    /// <param name="level">The cell's level.</param>
    /// <param name="corner">The corner, 0 to 7.</param>
    /// <param name="point">The point, in world units.</param>
    /// <returns>The weight, between zero and one.</returns>
    public static double Trilinear(IrradianceProbeKey cell, IrradianceLevel level, int corner, Double3 point) {
        var fx = Math.Clamp(value: ((point.X / level.Spacing) - cell.X), min: 0.0, max: 1.0);
        var fy = Math.Clamp(value: ((point.Y / level.Spacing) - cell.Y), min: 0.0, max: 1.0);
        var fz = Math.Clamp(value: ((point.Z / level.Spacing) - cell.Z), min: 0.0, max: 1.0);

        return (((((corner & 1) != 0) ? fx : (1.0 - fx)) * ((((corner >> 1) & 1) != 0) ? fy : (1.0 - fy))) *
            ((((corner >> 2) & 1) != 0) ? fz : (1.0 - fz)));
    }
    /// <summary>Returns the base direction set of <paramref name="count"/> rays: a spherical Fibonacci set, which the host
    /// computes once in double precision and the GPU reads rounded to single.</summary>
    /// <param name="count">The count of directions.</param>
    /// <param name="index">The direction, 0 to <paramref name="count"/> − 1.</param>
    /// <returns>The unit direction.</returns>
    public static Double3 BaseDirection(int count, int index) {
        var golden = (Math.PI * (3.0 - Math.Sqrt(d: 5.0)));
        var y = (1.0 - (((index + 0.5) * 2.0) / count));
        var radius = Math.Sqrt(d: Math.Max(val1: 0.0, val2: (1.0 - (y * y))));
        var angle = (golden * index);

        return new Double3(X: (Math.Cos(d: angle) * radius), Y: y, Z: (Math.Sin(a: angle) * radius));
    }
    /// <summary>Returns the orientation a probe's directions take, from an integer hash of its key.</summary>
    /// <param name="key">The probe.</param>
    /// <returns>The orientation, 0 to <see cref="Orientations"/> − 1.</returns>
    public static int OrientationOf(IrradianceProbeKey key) {
        var hash = (((uint)key.Level) * 0x9E3779B1u);

        hash = ((hash ^ ((uint)key.X)) * 0x85EBCA77u);
        hash = ((hash ^ ((uint)key.Y)) * 0xC2B2AE3Du);
        hash = ((hash ^ ((uint)key.Z)) * 0x27D4EB2Fu);
        hash ^= (hash >> 15);

        return ((int)(hash % Orientations));
    }
    /// <summary>Applies one of the cube's symmetries to a direction: the orientation's low bits choose which axes to
    /// negate and its high part which permutation of the axes to take.</summary>
    /// <param name="direction">The direction.</param>
    /// <param name="orientation">The orientation, 0 to <see cref="Orientations"/> − 1.</param>
    /// <returns>The oriented direction, exactly as long as <paramref name="direction"/>.</returns>
    public static Double3 Orient(Double3 direction, int orientation) {
        var x = (((orientation & 1) != 0) ? -direction.X : direction.X);
        var y = (((orientation & 2) != 0) ? -direction.Y : direction.Y);
        var z = (((orientation & 4) != 0) ? -direction.Z : direction.Z);

        return ((orientation >> 3) switch {
            0 => new Double3(X: x, Y: y, Z: z),
            1 => new Double3(X: x, Y: z, Z: y),
            2 => new Double3(X: y, Y: x, Z: z),
            3 => new Double3(X: y, Y: z, Z: x),
            4 => new Double3(X: z, Y: x, Z: y),
            _ => new Double3(X: z, Y: y, Z: x),
        });
    }
    /// <summary>Returns a probe's ray direction. Stratum s holds every ray whose index modulo the stratum count is s, so
    /// each stratum alone spans the sphere.</summary>
    /// <param name="key">The probe.</param>
    /// <param name="level">The probe's level.</param>
    /// <param name="ray">The ray, 0 to the level's ray count − 1.</param>
    /// <returns>The unit direction.</returns>
    public static Double3 Direction(IrradianceProbeKey key, IrradianceLevel level, int ray) => Orient(
        direction: BaseDirection(count: level.Rays, index: ray),
        orientation: OrientationOf(key: key)
    );
    /// <summary>Returns the stratum a ray belongs to.</summary>
    /// <param name="level">The ray's level.</param>
    /// <param name="ray">The ray.</param>
    /// <returns>The stratum.</returns>
    public static int StratumOf(IrradianceLevel level, int ray) => (ray % level.Strata);
    /// <summary>Encodes a unit direction as octahedral coordinates in [0, 1] squared.</summary>
    /// <param name="direction">The unit direction.</param>
    /// <returns>The coordinates.</returns>
    public static (double U, double V) Encode(Double3 direction) {
        var sum = ((Math.Abs(value: direction.X) + Math.Abs(value: direction.Y)) + Math.Abs(value: direction.Z));
        var u = (direction.X / sum);
        var v = (direction.Z / sum);

        if (direction.Y < 0.0) {
            var foldedU = ((1.0 - Math.Abs(value: v)) * ((u >= 0.0) ? 1.0 : -1.0));
            var foldedV = ((1.0 - Math.Abs(value: u)) * ((v >= 0.0) ? 1.0 : -1.0));

            u = foldedU;
            v = foldedV;
        }

        return (((u * 0.5) + 0.5), ((v * 0.5) + 0.5));
    }
    /// <summary>Decodes octahedral coordinates in [0, 1] squared to a unit direction.</summary>
    /// <param name="u">The first coordinate.</param>
    /// <param name="v">The second coordinate.</param>
    /// <returns>The unit direction.</returns>
    public static Double3 Decode(double u, double v) {
        var x = ((u * 2.0) - 1.0);
        var z = ((v * 2.0) - 1.0);
        var y = ((1.0 - Math.Abs(value: x)) - Math.Abs(value: z));

        if (y < 0.0) {
            var foldedX = ((1.0 - Math.Abs(value: z)) * ((x >= 0.0) ? 1.0 : -1.0));
            var foldedZ = ((1.0 - Math.Abs(value: x)) * ((z >= 0.0) ? 1.0 : -1.0));

            x = foldedX;
            z = foldedZ;
        }

        return new Double3(X: x, Y: y, Z: z).Normalize();
    }
    /// <summary>Returns the direction at the centre of an interior texel of an octahedral map.</summary>
    /// <param name="texelX">The interior texel's column, 0 to <see cref="InteriorTexels"/> − 1.</param>
    /// <param name="texelY">The interior texel's row, 0 to <see cref="InteriorTexels"/> − 1.</param>
    /// <returns>The unit direction.</returns>
    public static Double3 TexelDirection(int texelX, int texelY) => Decode(
        u: ((texelX + 0.5) / InteriorTexels),
        v: ((texelY + 0.5) / InteriorTexels)
    );
    /// <summary>Returns the interior texel a bordered map's texel holds: an interior texel holds itself, and a border
    /// texel copies the interior texel across the octahedron's fold, so bilinear filtering at an edge reads the
    /// neighbouring direction rather than wrapping.</summary>
    /// <param name="borderedX">The column in the bordered map, 0 to <see cref="BorderedTexels"/> − 1.</param>
    /// <param name="borderedY">The row in the bordered map, 0 to <see cref="BorderedTexels"/> − 1.</param>
    /// <returns>The interior texel's column and row.</returns>
    public static (int X, int Y) BorderSource(int borderedX, int borderedY) =>
        BorderSource(
            borderedX: borderedX,
            borderedY: borderedY,
            interiorTexels: InteriorTexels
        );
    /// <summary>Returns the interior texel a texel of an octahedral map of any size with a one-texel border holds, by the
    /// rule <see cref="BorderSource(int, int)"/> states for the cache's maps: the sky's environment map reads a filter tap
    /// one texel past an edge through it.</summary>
    /// <param name="borderedX">The column in the bordered map, 0 to <paramref name="interiorTexels"/> + 1.</param>
    /// <param name="borderedY">The row in the bordered map, 0 to <paramref name="interiorTexels"/> + 1.</param>
    /// <param name="interiorTexels">The interior texels along each side of the map, at least one.</param>
    /// <returns>The interior texel's column and row.</returns>
    public static (int X, int Y) BorderSource(int borderedX, int borderedY, int interiorTexels) {
        var last = (interiorTexels - 1);
        var x = (borderedX - 1);
        var y = (borderedY - 1);
        var mirrorX = ((x < 0) || (x > last));
        var mirrorY = ((y < 0) || (y > last));

        if (mirrorY) {
            y = ((y < 0) ? 0 : last);
            x = (last - Math.Clamp(max: last, min: 0, value: x));
        }

        if (mirrorX) {
            x = ((x < 0) ? 0 : last);
            y = (last - Math.Clamp(max: last, min: 0, value: y));
        }

        if (mirrorX && mirrorY) {
            x = ((borderedX == 0) ? last : 0);
            y = ((borderedY == 0) ? last : 0);
        }

        return (x, y);
    }

    private static int FloorDivide(int value, int divisor) {
        var quotient = (value / divisor);

        return (((value % divisor) < 0) ? (quotient - 1) : quotient);
    }
    private static (int First, int Second)[] BuildSegments() {
        var segments = new (int First, int Second)[CellSegments];
        var count = 0;

        for (var first = 0; (first < CellCorners); first++) {
            for (var second = (first + 1); (second < CellCorners); second++) {
                segments[count] = (first, second);
                count++;
            }
        }

        return segments;
    }
}
