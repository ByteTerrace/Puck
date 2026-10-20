using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.SignedDistance.Queries;

// THE RENDER READING's ops (SdfFieldReading.Render): the warps, folds and reliefs mapCore draws that no contact field
// reads, each in fixed point. KEEP IN SYNC with the matching SDF_OP_* case in field/sdf-map.hlsli, with
// sdfWallpaperFoldCell/sdfWallpaperCellKey (field/sdf-point.hlsli, whose float statement is SdfWallpaperFold.Fold) and
// with sdfValueNoise3/sdfPcg3d (field/sdf-noise.hlsli, field/sdf-hash.hlsli). Each hashed decision is integer-only on
// both sides, so a cell, a corner or a variant is chosen identically; the float arithmetic between them differs by the
// Q48.16 grid and the trigonometric tables' half-ULP envelopes.
public sealed partial class SdfFieldEvaluator {
    // SDF_LOGSPHERE_MIN_RADIUS, SDF_LANE_ERODE_RAGGED_AMOUNT, SDF_SQRT3 and the R3 lattice's increments (field/sdf-hash.hlsli),
    // each converted once.
    private static readonly FixedQ4816 LogSphereMinRadius = FixedQ4816.FromDouble(value: 1.0e-4);
    private static readonly FixedQ4816 LaneErodeRaggedAmount = FixedQ4816.FromDouble(value: SdfProgramBuilder.LaneErodeRaggedAmount);
    private static readonly FixedQ4816 HalfSqrt3 = FixedQ4816.FromDouble(value: (0.5 * Sqrt3));
    private static readonly FixedQ4816 Ln2 = FixedQ4816.FromDouble(value: 0.6931471805599453);
    private static readonly FixedQ4816 Log2E = FixedQ4816.FromDouble(value: 1.4426950408889634);
    private static readonly FixedQ4816 Three = FixedQ4816.FromInteger(value: 3L);
    private static readonly FixedQ4816 Four = FixedQ4816.FromInteger(value: 4L);
    private const uint R3Alpha1 = 3518319155u;
    private const uint R3Alpha2 = 2882110345u;
    private const uint R3Alpha3 = 2360945575u;

    // The render reading's point ops past AxialProfile and RotatePlane (WarpPoint): Shear, GaussianPush and DomainWarp
    // move the point, RepeatPolar, WallpaperFold, LogSphere and CellJitter fold it. A fold with a material stride or
    // variant count also writes the delta a later shape's material takes (mapCore's parityMaterialDelta).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FixedVector3 RenderWarpPoint(in CompiledInstruction instruction, FixedVector3 localPosition, ref FixedQ4816 distanceScale, ref int materialDelta) {
        switch (instruction.Op) {
            case SdfOp.Shear: {
                    var target = (int)instruction.Shape;
                    var t = Component(axis: (int)instruction.Blend, point: localPosition);
                    var offset = ((((instruction.Data0Z * t) + instruction.Data0Y) * t) + instruction.Data0X) * t;

                    return WithComponent(axis: target, point: localPosition, value: (Component(axis: target, point: localPosition) + offset));
                }
            case SdfOp.GaussianPush: {
                    var push = new FixedVector3(X: instruction.Data0W, Y: instruction.Data1W, Z: instruction.Aux);
                    var offset = FixedVector3.Divide(left: (localPosition - Vector(instruction: instruction)), right: Vector1(instruction: instruction));

                    return (localPosition - (push * Exp(value: -FixedVector3.Dot(left: offset, right: offset))));
                }
            case SdfOp.DomainWarp: {
                    return (localPosition + (new FixedVector3(
                        X: FixedQ4816.Sin(angle: (instruction.Data0X * localPosition.Y)),
                        Y: FixedQ4816.Sin(angle: (instruction.Data0Y * localPosition.Z)),
                        Z: FixedQ4816.Sin(angle: (instruction.Data0Z * localPosition.X))
                    ) * instruction.Data0W));
                }
            case SdfOp.RepeatPolar: {
                    return RepeatPolar(instruction: in instruction, localPosition: localPosition, materialDelta: ref materialDelta);
                }
            case SdfOp.WallpaperFold: {
                    return WallpaperFold(instruction: in instruction, localPosition: localPosition, materialDelta: ref materialDelta);
                }
            case SdfOp.LogSphere: {
                    return LogSphere(distanceScale: ref distanceScale, instruction: in instruction, localPosition: localPosition);
                }
            default: {
                    return CellJitter(instruction: in instruction, localPosition: localPosition, materialDelta: ref materialDelta);
                }
        }
    }
    // SDF_OP_REPEAT_POLAR: the plane perpendicular to the axis folded into count sectors, each sector turned onto the
    // base one and, when mirrored, reflected across its bisector; the sector index strides the material.
    private static FixedVector3 RepeatPolar(in CompiledInstruction instruction, FixedVector3 localPosition, ref int materialDelta) {
        var (u, v) = PolarAxes(axis: instruction.Shape);
        var pu = Component(axis: u, point: localPosition);
        var pv = Component(axis: v, point: localPosition);
        var sectorAngle = instruction.Data0X;
        var halfSector = (sectorAngle * Half);
        var a = (FixedQ4816.Atan2(x: pu, y: pv) + halfSector);
        var r = new FixedVector2(X: pu, Y: pv).Length;
        var sector = FixedQ4816.Floor(value: (a * instruction.Data0Y));

        a = ((a - (sectorAngle * sector)) - halfSector);
        if (instruction.Blend != 0u) {
            a = FixedQ4816.Abs(value: a);
        }

        var (sin, cos) = FixedQ4816.SinCos(angle: a);

        if (instruction.Material != 0) {
            var wrapped = (sector - (instruction.Data0Z * FixedQ4816.Floor(value: (sector * instruction.Data0W))));

            materialDelta = (((int)(wrapped.Value >> FixedQ4816.FractionBitCount)) * instruction.Material);
        }

        return WithComponents(point: localPosition, u: u, uValue: (cos * r), v: v, vValue: (sin * r));
    }
    // The fold plane (u, v) of a polar axis, as mapCore picks it: (y, z) about X, (x, y) about Z, (x, z) about Y.
    private static (int U, int V) PolarAxes(uint axis) => axis switch {
        ((uint)SdfAxis.X) => (1, 2),
        ((uint)SdfAxis.Z) => (0, 1),
        _ => (0, 2),
    };
    // SDF_OP_WALLPAPER_FOLD: the in-plane point reduced to its lattice cell and folded by the group's in-cell isometry
    // (SdfWallpaperFold.Fold's statement, in fixed point); the cell key strides the material.
    private static FixedVector3 WallpaperFold(in CompiledInstruction instruction, FixedVector3 localPosition, ref int materialDelta) {
        var group = (SdfWallpaperGroup)instruction.Shape;
        var (axisA, axisB) = (((instruction.Blend == ((uint)Puck.SignedDistance.SdfPlane.YZ)) ? 1 : 0), ((instruction.Blend == ((uint)Puck.SignedDistance.SdfPlane.XY)) ? 1 : 2));
        var point = new FixedVector2(X: Component(axis: axisA, point: localPosition), Y: Component(axis: axisB, point: localPosition));
        var cell = new FixedVector2(X: instruction.Data0X, Y: instruction.Data0Y);
        var inverseCell = new FixedVector2(X: instruction.Data0Z, Y: instruction.Data0W);
        var limit = new FixedVector2(X: instruction.Data1X, Y: instruction.Data1Y);
        var folded = ((group >= SdfWallpaperGroup.P3)
            ? FoldHexCell(cellIndex: out var cellIndex, group: group, inversePitch: inverseCell, pitch: cell.X, q: point)
            : FoldSquareCell(cell: cell, cellIndex: out cellIndex, group: group, inverseCell: inverseCell, limit: limit, q: point));

        if (instruction.Material != 0) {
            var key = ((group >= SdfWallpaperGroup.P3)
                ? FloorMod(x: (cellIndex.X - cellIndex.Y), y: Three)
                : FloorMod(x: (cellIndex.X + cellIndex.Y), y: Two));

            materialDelta = (((int)((key + Half).Value >> FixedQ4816.FractionBitCount)) * instruction.Material);
        }

        return WithComponents(point: localPosition, u: axisA, uValue: folded.X, v: axisB, vValue: folded.Y);
    }
    private static FixedVector2 FoldSquareCell(FixedVector2 q, SdfWallpaperGroup group, FixedVector2 cell, FixedVector2 inverseCell, FixedVector2 limit, out FixedVector2 cellIndex) {
        cellIndex = new FixedVector2(
            X: FixedQ4816.Clamp(maximum: limit.X, minimum: -limit.X, value: FixedQ4816.Round(value: (q.X * inverseCell.X))),
            Y: FixedQ4816.Clamp(maximum: limit.Y, minimum: -limit.Y, value: FixedQ4816.Round(value: (q.Y * inverseCell.Y)))
        );

        var r = new FixedVector2(X: (q.X - (cell.X * cellIndex.X)), Y: (q.Y - (cell.Y * cellIndex.Y)));
        var parity = new FixedVector2(X: FloorMod(x: cellIndex.X, y: Two), Y: FloorMod(x: cellIndex.Y, y: Two));

        switch (group) {
            case SdfWallpaperGroup.P4G: {
                    var halfCell = (cell.X * Half);

                    if (r.Y < FixedQ4816.Zero) {
                        r = ((r.X >= FixedQ4816.Zero) ? new FixedVector2(X: -r.Y, Y: r.X) : new FixedVector2(X: -r.X, Y: -r.Y));
                    } else if (r.X < FixedQ4816.Zero) {
                        r = new FixedVector2(X: r.Y, Y: -r.X);
                    }
                    if ((r.X + r.Y) > halfCell) {
                        r = new FixedVector2(X: (halfCell - r.Y), Y: (halfCell - r.X));
                    }

                    return r;
                }
            case SdfWallpaperGroup.P4:
            case SdfWallpaperGroup.P4M: {
                    var turns = FloorMod(x: ((parity.Y - parity.X) - (Two * (parity.X * parity.Y))), y: Four);

                    if (turns >= Two) {
                        r = -r;
                        turns -= Two;
                    }
                    if (turns >= FixedQ4816.One) {
                        r = new FixedVector2(X: -r.Y, Y: r.X);
                    }
                    if ((group == SdfWallpaperGroup.P4M) && (r.Y > r.X)) {
                        r = new FixedVector2(X: r.Y, Y: r.X);
                    }

                    return r;
                }
            case SdfWallpaperGroup.Pm: {
                    return new FixedVector2(X: FixedQ4816.Abs(value: r.X), Y: r.Y);
                }
            case SdfWallpaperGroup.Pmm: {
                    return new FixedVector2(X: FixedQ4816.Abs(value: r.X), Y: FixedQ4816.Abs(value: r.Y));
                }
        }

        var (coefU, coefV) = group switch {
            SdfWallpaperGroup.P2 => ((FixedQ4816.One, FixedQ4816.One), (FixedQ4816.One, FixedQ4816.One)),
            SdfWallpaperGroup.Pg => ((FixedQ4816.Zero, FixedQ4816.One), (FixedQ4816.Zero, FixedQ4816.Zero)),
            SdfWallpaperGroup.Cm => ((FixedQ4816.One, FixedQ4816.One), (FixedQ4816.Zero, FixedQ4816.Zero)),
            SdfWallpaperGroup.Pmg => ((FixedQ4816.One, FixedQ4816.One), (FixedQ4816.Zero, FixedQ4816.One)),
            SdfWallpaperGroup.Pgg => ((FixedQ4816.Zero, FixedQ4816.One), (FixedQ4816.One, FixedQ4816.Zero)),
            SdfWallpaperGroup.Cmm => ((FixedQ4816.One, FixedQ4816.Zero), (FixedQ4816.Zero, FixedQ4816.One)),
            _ => ((FixedQ4816.Zero, FixedQ4816.Zero), (FixedQ4816.Zero, FixedQ4816.Zero)),
        };
        var signU = (FixedQ4816.One - (Two * FloorMod(x: ((coefU.Item1 * parity.X) + (coefU.Item2 * parity.Y)), y: Two)));
        var signV = (FixedQ4816.One - (Two * FloorMod(x: ((coefV.Item1 * parity.X) + (coefV.Item2 * parity.Y)), y: Two)));
        var folded = new FixedVector2(X: (r.X * signU), Y: (r.Y * signV));

        return (((group == SdfWallpaperGroup.Cmm) && (folded.Y < FixedQ4816.Zero)) ? -folded : folded);
    }
    private static FixedVector2 FoldHexCell(FixedVector2 q, SdfWallpaperGroup group, FixedQ4816 pitch, FixedVector2 inversePitch, out FixedVector2 cellIndex) {
        var halfSqrt3 = HalfSqrt3;
        var axialB = (q.Y * inversePitch.Y);
        var axialA = ((q.X * inversePitch.X) - (Half * axialB));
        var axialC = -(axialA + axialB);
        var roundedA = FixedQ4816.Round(value: axialA);
        var roundedB = FixedQ4816.Round(value: axialB);
        var roundedC = FixedQ4816.Round(value: axialC);
        var errorA = FixedQ4816.Abs(value: (roundedA - axialA));
        var errorB = FixedQ4816.Abs(value: (roundedB - axialB));
        var errorC = FixedQ4816.Abs(value: (roundedC - axialC));

        if ((errorA > errorB) && (errorA > errorC)) {
            roundedA = -(roundedB + roundedC);
        } else if (errorB > errorC) {
            roundedB = -(roundedA + roundedC);
        }
        cellIndex = new FixedVector2(X: roundedA, Y: roundedB);

        var r = new FixedVector2(X: (q.X - ((roundedA + (Half * roundedB)) * pitch)), Y: (q.Y - ((roundedB * halfSqrt3) * pitch)));

        if (group == SdfWallpaperGroup.P6) {
            var sector = FixedQ4816.Floor(value: (FixedQ4816.Atan2(x: r.X, y: r.Y) * (Three / FixedQ4816.Pi)));
            var (sin, cos) = FixedQ4816.SinCos(angle: -(sector * (FixedQ4816.Pi / Three)));

            return new FixedVector2(X: ((cos * r.X) - (sin * r.Y)), Y: ((sin * r.X) + (cos * r.Y)));
        }
        if (group == SdfWallpaperGroup.P3) {
            var turns = FloorMod(x: (roundedA - roundedB), y: Three);

            for (var turn = FixedQ4816.One; (turn <= turns); turn += FixedQ4816.One) {
                r = new FixedVector2(X: ((-(Half * r.X)) - (halfSqrt3 * r.Y)), Y: ((halfSqrt3 * r.X) - (Half * r.Y)));
            }

            return r;
        }

        var cornerMirrors = (group is SdfWallpaperGroup.P3M1 or SdfWallpaperGroup.P6M);
        var edgeMirrors = (group is SdfWallpaperGroup.P31M or SdfWallpaperGroup.P6M);

        if (edgeMirrors && (r.Y < FixedQ4816.Zero)) {
            r = new FixedVector2(X: r.X, Y: -r.Y);
        }
        if (cornerMirrors && (r.X < FixedQ4816.Zero)) {
            r = new FixedVector2(X: -r.X, Y: r.Y);
        }
        if (edgeMirrors && (((r.X * -halfSqrt3) + (r.Y * Half)) > FixedQ4816.Zero)) {
            r = new FixedVector2(X: ((-(Half * r.X)) + (halfSqrt3 * r.Y)), Y: ((halfSqrt3 * r.X) + (Half * r.Y)));
        }
        if (edgeMirrors && (r.Y < FixedQ4816.Zero)) {
            r = new FixedVector2(X: r.X, Y: -r.Y);
        }
        if (cornerMirrors && (((r.X * -Half) + (r.Y * halfSqrt3)) < FixedQ4816.Zero)) {
            r = new FixedVector2(X: ((Half * r.X) + (halfSqrt3 * r.Y)), Y: ((halfSqrt3 * r.X) - (Half * r.Y)));
        }
        if (cornerMirrors && (r.X < FixedQ4816.Zero)) {
            r = new FixedVector2(X: -r.X, Y: r.Y);
        }

        return r;
    }
    private static FixedQ4816 FloorMod(FixedQ4816 x, FixedQ4816 y) => (x - (y * FixedQ4816.Floor(value: (x / y))));
    // SDF_OP_LOG_SPHERE: the radius folded to its nearest shell along log(radius), the point scaled onto the prototype
    // shell, spun about Z by the shell's twist, and the candidate scaled back by the shell's scale.
    private static FixedVector3 LogSphere(in CompiledInstruction instruction, FixedVector3 localPosition, ref FixedQ4816 distanceScale) {
        var foldRadius = FixedQ4816.Max(x: localPosition.Length, y: LogSphereMinRadius);
        var logRadius = (FixedQ4816.Log2(value: foldRadius) * Ln2);
        var shell = FixedQ4816.Round(value: (logRadius * instruction.Data0Z));
        var shellScale = Exp(value: (shell * instruction.Data0X));
        var folded = (localPosition / shellScale);
        var (sin, cos) = FixedQ4816.SinCos(angle: (shell * instruction.Data0Y));

        distanceScale *= shellScale;

        return new FixedVector3(X: ((cos * folded.X) - (sin * folded.Y)), Y: ((sin * folded.X) + (cos * folded.Y)), Z: folded.Z);
    }
    // SDF_OP_CELL_JITTER: the Repeat fold, then a per-cell hashed displacement in the Blend lane's flavour, an optional
    // hashed tumble, and a hashed material variant; every hash is integer-only, as the kernel's is.
    private static FixedVector3 CellJitter(in CompiledInstruction instruction, FixedVector3 localPosition, ref int materialDelta) {
        var spacing = Vector(instruction: instruction);
        var inverseSpacing = Vector1(instruction: instruction);
        var cell = FixedVector3.Round(value: FixedVector3.Multiply(left: localPosition, right: inverseSpacing));
        var cellX = unchecked((uint)(cell.X.Value >> FixedQ4816.FractionBitCount));
        var cellY = unchecked((uint)(cell.Y.Value >> FixedQ4816.FractionBitCount));
        var cellZ = unchecked((uint)(cell.Z.Value >> FixedQ4816.FractionBitCount));
        var seed = instruction.Shape;
        var (seedX, seedY, seedZ) = (seed, unchecked(seed * Pcg3dLatticeNoise.HashStreamA), unchecked(seed * Pcg3dLatticeNoise.HashStreamB));
        var (keyX, keyY, keyZ) = ((cellX ^ seedX), (cellY ^ seedY), (cellZ ^ seedZ));
        var h0 = Pcg3dLatticeNoise.Pcg3d(x: keyX, y: keyY, z: keyZ);
        FixedVector3 r0;

        switch ((SdfNoiseFlavor)instruction.Blend) {
            case SdfNoiseFlavor.Blue: {
                    var (ux, uy, uz) = (unchecked(cellX + seedX), unchecked(cellY + seedY), unchecked(cellZ + seedZ));

                    r0 = new FixedVector3(
                        X: Fraction(hash: unchecked(((ux * R3Alpha1) + (uy * R3Alpha2)) + (uz * R3Alpha3))),
                        Y: Fraction(hash: unchecked(((ux * R3Alpha2) + (uy * R3Alpha3)) + (uz * R3Alpha1))),
                        Z: Fraction(hash: unchecked(((ux * R3Alpha3) + (uy * R3Alpha1)) + (uz * R3Alpha2)))
                    );
                    break;
                }
            case SdfNoiseFlavor.Gaussian: {
                    var g1 = Pcg3dLatticeNoise.Pcg3d(x: (keyX ^ Pcg3dLatticeNoise.HashStreamA), y: (keyY ^ Pcg3dLatticeNoise.HashStreamA), z: (keyZ ^ Pcg3dLatticeNoise.HashStreamA));
                    var g2 = Pcg3dLatticeNoise.Pcg3d(x: (keyX ^ Pcg3dLatticeNoise.HashStreamB), y: (keyY ^ Pcg3dLatticeNoise.HashStreamB), z: (keyZ ^ Pcg3dLatticeNoise.HashStreamB));

                    r0 = new FixedVector3(
                        X: (((Fraction(hash: h0.X) + Fraction(hash: g1.X)) + Fraction(hash: g2.X)) / Three),
                        Y: (((Fraction(hash: h0.Y) + Fraction(hash: g1.Y)) + Fraction(hash: g2.Y)) / Three),
                        Z: (((Fraction(hash: h0.Z) + Fraction(hash: g1.Z)) + Fraction(hash: g2.Z)) / Three)
                    );
                    break;
                }
            default: {
                    r0 = new FixedVector3(X: Fraction(hash: h0.X), Y: Fraction(hash: h0.Y), Z: Fraction(hash: h0.Z));
                    break;
                }
        }

        var folded = (localPosition - FixedVector3.Multiply(left: spacing, right: cell));
        var half = new FixedVector3(X: Half, Y: Half, Z: Half);

        folded -= ((r0 - half) * instruction.Data0W);

        if (instruction.Data1W > FixedQ4816.Zero) {
            var h1 = Pcg3dLatticeNoise.Pcg3d(x: (keyX ^ Pcg3dLatticeNoise.HashTumble), y: (keyY ^ Pcg3dLatticeNoise.HashTumble), z: (keyZ ^ Pcg3dLatticeNoise.HashTumble));
            var zz = ((Two * Fraction(hash: h1.X)) - FixedQ4816.One);
            var rr = FixedQ4816.Sqrt(value: FixedQ4816.Max(x: FixedQ4816.Zero, y: (FixedQ4816.One - (zz * zz))));
            var (sinPhi, cosPhi) = FixedQ4816.SinCos(angle: (FixedQ4816.Tau * Fraction(hash: h1.Y)));
            var (sinHalf, cosHalf) = FixedQ4816.SinCos(angle: (Half * ((Fraction(hash: h1.Z) * instruction.Data1W) * FixedQ4816.Pi)));
            var rotation = new FixedQuaternion(X: ((rr * cosPhi) * sinHalf), Y: ((rr * sinPhi) * sinHalf), Z: (zz * sinHalf), W: cosHalf);

            folded = rotation.RotateInverse(vector: folded);
        }
        if (instruction.Material != 0) {
            materialDelta = ((int)(h0.Z % ((uint)instruction.Material)));
        }

        return folded;
    }
    // A hash's value in [0, 1): its top sixteen bits as Q48.16 ticks, the kernel's (float)hash * 2^-32 on the grid.
    private static FixedQ4816 Fraction(uint hash) => FixedQ4816.FromRawBits(value: ((long)(hash >> 16)));
    // e^value through the base-two kernel: 2^(value·log2 e), zero far below and the carrier's ceiling far above.
    private static FixedQ4816 Exp(FixedQ4816 value) => FixedQ4816.Exp2(value: (value * Log2E));

    // The render reading's field ops past Onion, CellDisplace and Dilate (EditField): Displace adds the separable sine
    // relief and NoiseDisplace the normalized fBm value noise, both at the folded point.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FixedQ4816 RenderEditField(in CompiledInstruction instruction, FixedVector3 localPosition, FixedQ4816 resultDistance) {
        if (instruction.Op == SdfOp.Displace) {
            return (resultDistance + (instruction.Data0W * ((FixedQ4816.Sin(angle: (instruction.Data0X * localPosition.X)) * FixedQ4816.Sin(angle: (instruction.Data0Y * localPosition.Y))) * FixedQ4816.Sin(angle: (instruction.Data0Z * localPosition.Z)))));
        }

        var q = (localPosition * instruction.Data0X);
        var octaveAmplitude = FixedQ4816.One;
        var noiseSum = FixedQ4816.Zero;
        var octaves = (int)instruction.Blend;

        for (var octave = 0u; (octave < octaves); octave++) {
            var octaveSeed = unchecked(instruction.Shape + octave);

            noiseSum += (octaveAmplitude * ValueNoise3(q: q, seedX: octaveSeed, seedY: unchecked(octaveSeed * Pcg3dLatticeNoise.HashStreamA), seedZ: unchecked(octaveSeed * Pcg3dLatticeNoise.HashStreamB)));
            q *= instruction.Data0W;
            octaveAmplitude *= instruction.Data0Z;
        }

        return (resultDistance + ((instruction.Data0Y * instruction.Data1X) * noiseSum));
    }
    // SDF_OP_LANE_ERODE under no dynamic slot: every lane reads zero. The erosion the next shape takes, in world units,
    // or null when the shape is skipped outright (t at or past one).
    private static FixedQ4816? LaneErodeAmount(in CompiledInstruction instruction, FixedVector3 localPosition) {
        var t = FixedQ4816.Clamp(maximum: FixedQ4816.One, minimum: FixedQ4816.Zero, value: ((FixedQ4816.Zero - instruction.Data0Y) / (instruction.Data0Z - instruction.Data0Y)));

        if (t >= FixedQ4816.One) {
            return null;
        }

        var noise = (Half + (Half * ValueNoise3(q: (localPosition * instruction.Data0W), seedX: Pcg3dLatticeNoise.HashStreamA, seedY: Pcg3dLatticeNoise.HashStreamB, seedZ: Pcg3dLatticeNoise.HashTumble)));
        var ragged = FixedQ4816.Clamp(maximum: FixedQ4816.One, minimum: FixedQ4816.Zero, value: (t + (((noise - Half) * LaneErodeRaggedAmount) * ((Four * t) * (FixedQ4816.One - t)))));

        return (ragged * instruction.Data1X);
    }
    // sdfValueNoise3: one PCG3D hash per lattice corner, quintic-smoothed trilinear blend, in [-1, 1].
    private static FixedQ4816 ValueNoise3(FixedVector3 q, uint seedX, uint seedY, uint seedZ) {
        var cx = (q.X.Value >> FixedQ4816.FractionBitCount);
        var cy = (q.Y.Value >> FixedQ4816.FractionBitCount);
        var cz = (q.Z.Value >> FixedQ4816.FractionBitCount);
        var ux = Quintic(t: (q.X - FixedQ4816.FromInteger(value: cx)));
        var uy = Quintic(t: (q.Y - FixedQ4816.FromInteger(value: cy)));
        var uz = Quintic(t: (q.Z - FixedQ4816.FromInteger(value: cz)));
        var c000 = Corner(cx: cx, cy: cy, cz: cz);
        var c100 = Corner(cx: (cx + 1L), cy: cy, cz: cz);
        var c010 = Corner(cx: cx, cy: (cy + 1L), cz: cz);
        var c110 = Corner(cx: (cx + 1L), cy: (cy + 1L), cz: cz);
        var c001 = Corner(cx: cx, cy: cy, cz: (cz + 1L));
        var c101 = Corner(cx: (cx + 1L), cy: cy, cz: (cz + 1L));
        var c011 = Corner(cx: cx, cy: (cy + 1L), cz: (cz + 1L));
        var c111 = Corner(cx: (cx + 1L), cy: (cy + 1L), cz: (cz + 1L));
        var x00 = FixedQ4816.Lerp(amount: ux, from: c000, to: c100);
        var x10 = FixedQ4816.Lerp(amount: ux, from: c010, to: c110);
        var x01 = FixedQ4816.Lerp(amount: ux, from: c001, to: c101);
        var x11 = FixedQ4816.Lerp(amount: ux, from: c011, to: c111);
        var y0 = FixedQ4816.Lerp(amount: uy, from: x00, to: x10);
        var y1 = FixedQ4816.Lerp(amount: uy, from: x01, to: x11);

        return ((FixedQ4816.Lerp(amount: uz, from: y0, to: y1) * Two) - FixedQ4816.One);

        FixedQ4816 Corner(long cx, long cy, long cz) =>
            Fraction(hash: Pcg3dLatticeNoise.Pcg3d(x: (unchecked((uint)cx) ^ seedX), y: (unchecked((uint)cy) ^ seedY), z: (unchecked((uint)cz) ^ seedZ)).X);
    }
    private static FixedQ4816 Quintic(FixedQ4816 t) {
        var t3 = ((t * t) * t);

        return (t3 * ((t * ((t * FixedQ4816.FromInteger(value: 6L)) - FixedQ4816.FromInteger(value: 15L))) + FixedQ4816.FromInteger(value: 10L)));
    }
    private static FixedVector3 WithComponent(FixedVector3 point, int axis, FixedQ4816 value) => new(
        X: ((axis == 0) ? value : point.X),
        Y: ((axis == 1) ? value : point.Y),
        Z: ((axis == 2) ? value : point.Z)
    );
}
