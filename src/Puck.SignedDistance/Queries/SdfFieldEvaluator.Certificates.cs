using Puck.Maths;

namespace Puck.SignedDistance.Queries;

// THE REACH CERTIFICATES the cull tables (SdfFieldEvaluator.Cull.cs) are built from. A certificate bounds the candidate
// one shape offers, as the fixed-point walks compute it, from the world point (or box) alone:
//
// - a point walk's candidate v at world point p satisfies v ≥ G·|p − C| − R;
// - a box walk's candidate interval over box B has its lower end at least G_B·d − R_B − S_B·ω and its width at most
//   W_c + W_f·d⁺ + W_ω·ω, where ω bounds the box's half-diagonal and d and d⁺ are, for a chain that turns its box only
//   by exact half turns, the least and greatest distances from the box to C, and otherwise the distance from the box's
//   centre to C (and that distance plus ω).
//
// Each is derived from the compiled fixed-point instructions, never from the float program, by following the chain of
// point transforms from its reset to the shape the way the walks apply them:
//
// - A Translate subtracts exactly.
// - A Rotate is the fused kernel over the quantized quaternion q̃: its exact linear map is M = n²·R̂ + (1 − n²)·I for
//   n² = |q̃|² and R̂ the rotation of q̃/n, so ‖M − R̂‖ ≤ 2·|1 − n²|, which contracts or dilates a far point in proportion to
//   its distance from the rotation's origin; the kernel's two rounded stages add at most √3·(|u| + 1) raws. A box walk's
//   rotation reads each coordinate's range once per axis through the enclosed entries of M, so it maps a box of centre c
//   and half-extents h into the box of centre M·c and half-extents |M|·h, widened by each entry's width times |c|₁ and by
//   nine raws of rounding and slack; the product of those |M| (P below) carries the world box's half-extents through
//   the chain.
// - A Scale with one magnitude on every axis (a sign is exact) divides with rounding to nearest, and multiplies every
//   later candidate by the rounded running scale; any other Scale, and every fold, leaves the chain uncertified.
// - A local certificate supplies v >= PointGain*|p-C| - Radius - PointSlack. Distance-like shapes use unit gain;
//   a superellipsoid uses its compiled radii and the scaled-ratio rounding bound, never a Euclidean sphere claim.
//
// The bounds hold for every point the walk can answer: inside the frame no step leaves the carrier. They are evaluated
// once here in double from exact raw inputs, and every quantity is padded past the double's own rounding before it is
// rounded outward into raws, so a test may skip less than it could but never what can move an answer.
public sealed partial class SdfFieldEvaluator {
    private const double RawUnit = (1.0 / 65536.0);
    private const double Sqrt3 = 1.7320508075688772;
    // The relative padding past double rounding applied to every certified quantity before it reaches raws.
    private const double DoublePadding = (1.0 / 1099511627776.0);
    private const double Q32 = 4294967296.0;
    // The raws by which one box rotation's three outward-rounded products and two sums, and its linear slack, can widen
    // each axis of the enclosed image.
    private const double RotationBoxRaws = 9.0;

    // One shape's certificate in double, world units: point v ≥ Gain·D − Radius; box lower ≥ BoxGain·d − BoxRadius −
    // BoxSpread·ω, the box terms meaningful only when Boxed and the box distances the centre's when BoxCentred.
    private readonly record struct ShapeReach(
        double CenterX, double CenterY, double CenterZ,
        double Gain, double Radius,
        bool Boxed, bool BoxCentred,
        double BoxGain, double BoxRadius, double BoxSpread
    ) {
        // The same certificate for a candidate scaled by s > 0 and rounded once (a scope's pop), after subtracting a
        // blend slack from the field it scales.
        public ShapeReach Scaled(double scale, double slack) => ((scale > 0.0)
            ? this with {
                BoxGain = (BoxGain * scale),
                BoxRadius = (((BoxRadius + slack) * scale) + RawUnit),
                BoxSpread = (BoxSpread * scale),
                Gain = (Gain * scale),
                Radius = (((Radius + slack) * scale) + RawUnit),
            }
            : this with { BoxRadius = (BoxRadius + slack), Radius = (Radius + slack) });
    }
    // The local point field satisfies v >= PointGain*|p-C| - Radius - PointSlack. Existing distance-like fields
    // use unit gain. BoxRadius and BoxLoss describe a separate interval bound only when Boxed.
    private readonly record struct LocalCertificate(
        double CenterX, double CenterY, double CenterZ,
        double Radius, double PointSlack,
        bool Boxed, double BoxRadius, double BoxLoss, double PointGain = 1.0
    );
    // The chain state the analysis follows: the forward map world = Origin + Frame·(Scale·local), the bounds on how far
    // the walks' local point (or box) can stray from that exact map's image, and the running distance scale's range.
    private struct ChainCertificate {
        public bool Known;
        public double OriginX, OriginY, OriginZ;
        public double M00, M01, M02, M10, M11, M12, M20, M21, M22;
        public double Scale;
        // |local − A(p)| ≤ PointA + PointB·|A(p)|, in local units.
        public double PointA, PointB;
        // The box walk's local box has a centre within BoxA + BoxB·|A(p_c)| of A(p_c), for p_c the world box's centre,
        // and half-extents at most P·h + BoxDA + BoxDB·|A(p_c)| on every axis, for h the world box's half-extents.
        public double BoxA, BoxB, BoxDA, BoxDB;
        public double P00, P01, P02, P10, P11, P12, P20, P21, P22;
        // Whether a rotation other than an exact half turn turned the box, so it no longer maps onto an axis-aligned box.
        public bool Turned;
        // The running distance scale the point and box walks hold, each inside its own range.
        public double PointScaleLow, PointScaleHigh, BoxScaleLow, BoxScaleHigh;

        public static ChainCertificate Identity => new() {
            BoxScaleHigh = 1.0,
            BoxScaleLow = 1.0,
            Known = true,
            M00 = 1.0,
            M11 = 1.0,
            M22 = 1.0,
            P00 = 1.0,
            P11 = 1.0,
            P22 = 1.0,
            PointScaleHigh = 1.0,
            PointScaleLow = 1.0,
            Scale = 1.0,
        };
        public static ChainCertificate Unknown => default;

        public void Translate(in CompiledInstruction instruction) {
            var (x, y, z) = (((double)instruction.Data0X), ((double)instruction.Data0Y), ((double)instruction.Data0Z));
            var length = Length(x: x, y: y, z: z);

            OriginX += ((((M00 * x) + (M01 * y)) + (M02 * z)) * Scale);
            OriginY += ((((M10 * x) + (M11 * y)) + (M12 * z)) * Scale);
            OriginZ += ((((M20 * x) + (M21 * y)) + (M22 * z)) * Scale);
            PointA += (PointB * length);
            BoxA += (BoxB * length);
            BoxDA += (BoxDB * length);
        }
        public void Rotate(in CompiledInstruction instruction) {
            var (x, y, z, w) = (((double)instruction.Data0X), ((double)instruction.Data0Y), ((double)instruction.Data0Z), ((double)instruction.Data0W));
            var vectorSquared = (((x * x) + (y * y)) + (z * z));
            var normSquared = (vectorSquared + (w * w));

            if (normSquared <= 0.25) {
                Known = false;

                return;
            }

            var deviation = Math.Abs(value: (1.0 - normSquared));
            var growth = (1.0 + (2.0 * deviation));
            var rounding = ((Sqrt3 * (Math.Sqrt(d: vectorSquared) + 1.0)) * RawUnit);
            var width = EntryWidth(rotation: RotationBounds.Of(instruction: in instruction));
            // The walk's linear map over the conjugate (u = −v): (1 − 2|u|²)·I + 2·u·uᵀ + 2w·[u]×.
            var (ux, uy, uz) = (-x, -y, -z);
            var diagonal = (1.0 - (2.0 * vectorSquared));

            var (m00, m01, m02) = ((diagonal + ((2.0 * ux) * ux)), (2.0 * ((ux * uy) - (w * uz))), (2.0 * ((ux * uz) + (w * uy))));
            var (m10, m11, m12) = ((2.0 * ((uy * ux) + (w * uz))), (diagonal + ((2.0 * uy) * uy)), (2.0 * ((uy * uz) - (w * ux))));
            var (m20, m21, m22) = ((2.0 * ((uz * ux) - (w * uy))), (2.0 * ((uz * uy) + (w * ux))), (diagonal + ((2.0 * uz) * uz)));
            var pad = (width + (4.0 * DoublePadding));

            var (u00, u01, u02) = ((Math.Abs(value: m00) + pad), (Math.Abs(value: m01) + pad), (Math.Abs(value: m02) + pad));
            var (u10, u11, u12) = ((Math.Abs(value: m10) + pad), (Math.Abs(value: m11) + pad), (Math.Abs(value: m12) + pad));
            var (u20, u21, u22) = ((Math.Abs(value: m20) + pad), (Math.Abs(value: m21) + pad), (Math.Abs(value: m22) + pad));
            var rows = Math.Max(val1: ((u00 + u01) + u02), val2: Math.Max(val1: ((u10 + u11) + u12), val2: ((u20 + u21) + u22)));
            var norm = Math.Sqrt(d: normSquared);

            var (qx, qy, qz, qw) = ((x / norm), (y / norm), (z / norm), (w / norm));
            // The walk rotates the point by the conjugate, so the forward frame turns by the quaternion itself.
            var r00 = (1.0 - (2.0 * ((qy * qy) + (qz * qz))));
            var r01 = (2.0 * ((qx * qy) - (qz * qw)));
            var r02 = (2.0 * ((qx * qz) + (qy * qw)));
            var r10 = (2.0 * ((qx * qy) + (qz * qw)));
            var r11 = (1.0 - (2.0 * ((qx * qx) + (qz * qz))));
            var r12 = (2.0 * ((qy * qz) - (qx * qw)));
            var r20 = (2.0 * ((qx * qz) - (qy * qw)));
            var r21 = (2.0 * ((qy * qz) + (qx * qw)));
            var r22 = (1.0 - (2.0 * ((qx * qx) + (qy * qy))));
            var zero = FixedQ4816.Zero;
            var halfTurn = ((instruction.Data0W == zero) && (
                ((FixedQ4816.Abs(value: instruction.Data0X) == FixedQ4816.One) && (instruction.Data0Y == zero) && (instruction.Data0Z == zero)) ||
                ((FixedQ4816.Abs(value: instruction.Data0Y) == FixedQ4816.One) && (instruction.Data0X == zero) && (instruction.Data0Z == zero)) ||
                ((FixedQ4816.Abs(value: instruction.Data0Z) == FixedQ4816.One) && (instruction.Data0X == zero) && (instruction.Data0Y == zero))));

            (M00, M01, M02, M10, M11, M12, M20, M21, M22) = (
                (((M00 * r00) + (M01 * r10)) + (M02 * r20)), (((M00 * r01) + (M01 * r11)) + (M02 * r21)), (((M00 * r02) + (M01 * r12)) + (M02 * r22)),
                (((M10 * r00) + (M11 * r10)) + (M12 * r20)), (((M10 * r01) + (M11 * r11)) + (M12 * r21)), (((M10 * r02) + (M11 * r12)) + (M12 * r22)),
                (((M20 * r00) + (M21 * r10)) + (M22 * r20)), (((M20 * r01) + (M21 * r11)) + (M22 * r21)), (((M20 * r02) + (M21 * r12)) + (M22 * r22))
            );
            (P00, P01, P02, P10, P11, P12, P20, P21, P22) = (
                (((u00 * P00) + (u01 * P10)) + (u02 * P20)), (((u00 * P01) + (u01 * P11)) + (u02 * P21)), (((u00 * P02) + (u01 * P12)) + (u02 * P22)),
                (((u10 * P00) + (u11 * P10)) + (u12 * P20)), (((u10 * P01) + (u11 * P11)) + (u12 * P21)), (((u10 * P02) + (u11 * P12)) + (u12 * P22)),
                (((u20 * P00) + (u21 * P10)) + (u22 * P20)), (((u20 * P01) + (u21 * P11)) + (u22 * P21)), (((u20 * P02) + (u21 * P12)) + (u22 * P22))
            );
            BoxDA = (((rows * BoxDA) + ((width * Sqrt3) * BoxA)) + (RotationBoxRaws * RawUnit));
            BoxDB = ((rows * BoxDB) + ((width * Sqrt3) * (1.0 + BoxB)));
            BoxA *= growth;
            BoxB = ((growth * BoxB) + (2.0 * deviation));
            PointA = ((growth * PointA) + rounding);
            PointB = ((growth * PointB) + (2.0 * deviation));
            Turned |= !halfTurn;
        }
        public void ScaleBy(in CompiledInstruction instruction) {
            var magnitude = instruction.Data0W.Value;

            if (
                (magnitude <= 0L) ||
                (Math.Abs(value: instruction.Data0X.Value) != magnitude) ||
                (Math.Abs(value: instruction.Data0Y.Value) != magnitude) ||
                (Math.Abs(value: instruction.Data0Z.Value) != magnitude)
            ) {
                Known = false;

                return;
            }

            var scale = ((double)instruction.Data0W);

            var (sx, sy, sz) = (((double)Math.Sign(value: instruction.Data0X.Value)), ((double)Math.Sign(value: instruction.Data0Y.Value)), ((double)Math.Sign(value: instruction.Data0Z.Value)));

            (M00, M01, M02, M10, M11, M12, M20, M21, M22) = ((M00 * sx), (M01 * sy), (M02 * sz), (M10 * sx), (M11 * sy), (M12 * sz), (M20 * sx), (M21 * sy), (M22 * sz));
            (P00, P01, P02, P10, P11, P12, P20, P21, P22) = ((P00 / scale), (P01 / scale), (P02 / scale), (P10 / scale), (P11 / scale), (P12 / scale), (P20 / scale), (P21 / scale), (P22 / scale));
            Scale *= scale;
            PointA = ((PointA / scale) + RawUnit);
            BoxA /= scale;
            BoxDA = ((BoxDA / scale) + RawUnit);
            PointScaleLow = ((PointScaleLow * scale) - (0.5 * RawUnit));
            PointScaleHigh = ((PointScaleHigh * scale) + (0.5 * RawUnit));
            BoxScaleLow = ((BoxScaleLow * scale) - RawUnit);
            BoxScaleHigh = ((BoxScaleHigh * scale) + RawUnit);

            if ((PointScaleLow <= 0.0) || (BoxScaleLow <= 0.0)) {
                Known = false;
            }
        }
        // The shape's certificate, or none when the chain or the shape admits none.
        //
        // Box: B_s, the local box the walk evaluates the shape over, lies in a ball about its centre c of radius
        // ‖P‖·ω + √3·s (s = BoxDA + BoxDB·|z|, z = A(p_c)); an unturned one lies, axis by axis, within (P − I/Scale)·h + s of
        // the exact image of the world box, so its least distance to the shape's centre is at least the image's, Scale⁻¹
        // times the world gap, less |c − z| and those. Through |z| ≤ d/Scale + |c_l| (plus ω/Scale for the gap) each term
        // becomes a multiple of d, of ω, or a constant, and the running distance scale's range turns local into world.
        public readonly ShapeReach? Certify(in CompiledInstruction instruction) {
            if (!Known || (TryLocalCertificate(instruction: in instruction) is not { } sphere)) {
                return null;
            }

            var reach = (1.0 / Scale);
            var localCentre = Length(x: sphere.CenterX, y: sphere.CenterY, z: sphere.CenterZ);

            var (cx, cy, cz) = ((sphere.CenterX * Scale), (sphere.CenterY * Scale), (sphere.CenterZ * Scale));
            var spreadNorm = NormBound(diagonal: 0.0);
            var loss = (1.0 + sphere.BoxLoss);
            // The rate at which the box's drift from the exact image grows with |z|.
            var drift = (BoxB + ((Sqrt3 * loss) * BoxDB));

            if ((PointB >= 0.5) || (sphere.Boxed && (drift >= 0.5))) {
                return null;
            }

            var imageSpread = (Turned ? spreadNorm : NormBound(diagonal: reach));
            var lowRadius = ((((BoxA + ((Sqrt3 * loss) * BoxDA)) + (drift * localCentre)) + sphere.BoxRadius) * BoxScaleHigh);

            return new ShapeReach(
                BoxCentred: Turned,
                BoxGain: (((1.0 - drift) * reach) * BoxScaleLow),
                BoxRadius: (lowRadius + RawUnit),
                BoxSpread: (((imageSpread + (sphere.BoxLoss * spreadNorm)) + (Turned ? 0.0 : (drift * reach))) * BoxScaleHigh),
                Boxed: sphere.Boxed,
                CenterX: (OriginX + (((M00 * cx) + (M01 * cy)) + (M02 * cz))),
                CenterY: (OriginY + (((M10 * cx) + (M11 * cy)) + (M12 * cz))),
                CenterZ: (OriginZ + (((M20 * cx) + (M21 * cy)) + (M22 * cz))),
                Gain: (((sphere.PointGain * (1.0 - PointB)) * reach) * PointScaleLow),
                Radius: (((((sphere.PointGain * (PointA + (PointB * localCentre))) + sphere.Radius) + sphere.PointSlack) * PointScaleHigh) + (0.5 * RawUnit))
            );
        }

        // A bound on the spectral norm of P − diagonal·I: the lesser of its Frobenius norm and the root of its largest
        // column sum times its largest row sum.
        private readonly double NormBound(double diagonal) {
            var (a00, a11, a22) = ((P00 - diagonal), (P11 - diagonal), (P22 - diagonal));
            var frobenius = Math.Sqrt(d: (((((((((a00 * a00) + (P01 * P01)) + (P02 * P02)) + (P10 * P10)) + (a11 * a11)) + (P12 * P12)) + (P20 * P20)) + (P21 * P21)) + (a22 * a22)));
            var rows = Math.Max(val1: ((Math.Abs(value: a00) + Math.Abs(value: P01)) + Math.Abs(value: P02)), val2: Math.Max(val1: ((Math.Abs(value: P10) + Math.Abs(value: a11)) + Math.Abs(value: P12)), val2: ((Math.Abs(value: P20) + Math.Abs(value: P21)) + Math.Abs(value: a22))));
            var columns = Math.Max(val1: ((Math.Abs(value: a00) + Math.Abs(value: P10)) + Math.Abs(value: P20)), val2: Math.Max(val1: ((Math.Abs(value: P01) + Math.Abs(value: a11)) + Math.Abs(value: P21)), val2: ((Math.Abs(value: P02) + Math.Abs(value: P12)) + Math.Abs(value: a22))));

            return (Math.Min(val1: frobenius, val2: Math.Sqrt(d: (rows * columns))) * (1.0 + DoublePadding));
        }
    }

    // Every shape's certificate, by instruction; none for a shape whose chain or geometry admits none. The chain state
    // starts from the world point at the stream's start, at every ResetPoint and at every instance boundary: the
    // program refuses a segment that reads a point moved before it (SdfProgram.RequireSegmentsStartAtTheWorldPoint).
    private static ShapeReach?[] BuildShapeReaches(SdfProgram program, CompiledInstruction[] compiled) {
        var reaches = new ShapeReach?[compiled.Length];
        CountArrayPayload(rows: reaches, work: PointWork, arrays: CertificateArraysCreated, bytes: CertificatePayloadCreated);
        var boundary = new bool[(compiled.Length + 1)];
        var chain = ChainCertificate.Identity;

        foreach (var instance in program.Instances) {
            boundary[instance.First] = true;
            boundary[instance.End] = true;
        }

        for (var index = 0; (index < compiled.Length); index++) {
            ref readonly var instruction = ref compiled[index];

            if (boundary[index] || (instruction.Op == SdfOp.ResetPoint)) {
                chain = ChainCertificate.Identity;
            }

            switch (instruction.Op) {
                case SdfOp.ResetPoint:
                case SdfOp.PushField:
                case SdfOp.PopField: {
                        break;
                    }
                case SdfOp.Translate: {
                        chain.Translate(instruction: in instruction);
                        break;
                    }
                case SdfOp.Rotate: {
                        if (!IsIdentityRotation(instruction: in instruction)) {
                            chain.Rotate(instruction: in instruction);
                        }

                        break;
                    }
                case SdfOp.Scale: {
                        if (!IsUnitScale(instruction: in instruction)) {
                            chain.ScaleBy(instruction: in instruction);
                        }

                        break;
                    }
                case SdfOp.ShapeBlend: {
                        if (!instruction.Detail) {
                            reaches[index] = chain.Certify(instruction: in instruction);
                        }

                        break;
                    }
                default: {
                        chain = ChainCertificate.Unknown;
                        break;
                    }
            }
        }

        return reaches;
    }
    // A local field certificate, or none for a primitive not covered. Distance-like fields use the distance to a
    // containing sphere; the superellipsoid supplies its own field gain and rounding slack. The box rules of
    // Sphere, Box, Cylinder and Torus read each coordinate's range once, so each endpoint of their interval is the exact
    // field at a point of the box, rounded outward by a floor or ceiling root.
    private static LocalCertificate? TryLocalCertificate(in CompiledInstruction instruction) {
        const double Slack = (4.0 * RawUnit);

        switch ((SdfShapeType)instruction.Shape) {
            case SdfShapeType.Sphere: {
                    var radius = Math.Abs(value: ((double)instruction.Data0X));

                    return Separable(radius: radius);
                }
            case SdfShapeType.Box:
            case SdfShapeType.ScreenSlab: {
                    var corner = instruction.Data0W;

                    if (
                        (instruction.Data0X < FixedQ4816.Zero) || (instruction.Data0Y < FixedQ4816.Zero) || (instruction.Data0Z < FixedQ4816.Zero) ||
                        (corner < FixedQ4816.Zero) || (corner > FixedQ4816.Min(x: instruction.Data0X, y: FixedQ4816.Min(x: instruction.Data0Y, y: instruction.Data0Z)))
                    ) {
                        return null;
                    }

                    return Separable(radius: Length(x: ((double)instruction.Data0X), y: ((double)instruction.Data0Y), z: ((double)instruction.Data0Z)));
                }
            case SdfShapeType.Cylinder: {
                    if ((instruction.Data0X < FixedQ4816.Zero) || (instruction.Data0Y < FixedQ4816.Zero)) {
                        return null;
                    }

                    return Separable(radius: (Length(x: ((double)instruction.Data0X), y: ((double)instruction.Data0Y), z: 0.0) + Math.Abs(value: ((double)instruction.Data1W))));
                }
            case SdfShapeType.Torus: {
                    return Separable(radius: (Math.Abs(value: ((double)instruction.Data0X)) + Math.Abs(value: ((double)instruction.Data0Y))));
                }
            case SdfShapeType.Capsule: {
                    var (x, y, z) = (((double)instruction.Data0X), ((double)instruction.Data0Y), ((double)instruction.Data0Z));

                    // The rounded parameter is clamped into [0, 1], so the point it picks lies on the segment and the
                    // length to it is at least the distance to the segment; the interval rule reads h's range, so no box.
                    return new LocalCertificate(
                        BoxLoss: 0.0,
                        BoxRadius: 0.0,
                        Boxed: false,
                        CenterX: (0.5 * x),
                        CenterY: (0.5 * y),
                        CenterZ: (0.5 * z),
                        PointSlack: Slack,
                        Radius: ((0.5 * Length(x: x, y: y, z: z)) + Math.Abs(value: ((double)instruction.Data0W)))
                    );
                }
            case SdfShapeType.Superellipsoid: {
                    return TrySuperellipsoidCertificate(instruction: in instruction);
                }
            case SdfShapeType.Trapezoid: {
                    return TryTrapezoidCertificate(instruction: in instruction);
                }
            default: {
                    return null;
                }
        }

        static LocalCertificate Separable(double radius) => new(
            BoxLoss: 0.0,
            BoxRadius: (radius + Slack),
            Boxed: true,
            CenterX: 0.0,
            CenterY: 0.0,
            CenterZ: 0.0,
            PointSlack: Slack,
            Radius: radius
        );
    }
    // Point-only: with s=min(r), each rounded q_i differs from |p_i|*s/r_i by at most E raws. The factored
    // power has a unit component and a root >= One, so v >= max(q)-s, also for e=2's full-width norm.
    // Thus v >= (s/max(r))/sqrt(3)*|p| - s - E. The frame's uncullable interval walk proves intermediate
    // operations fit; outside that frame this certificate grants nothing. The numeric sign/range proof and the
    // ratio-rounding envelope live in docs/rendering/sdf/handbook/queries-and-determinism.md.
    private static LocalCertificate? TrySuperellipsoidCertificate(in CompiledInstruction instruction) {
        var (rx, ry, rz) = (instruction.Data0X.Value, instruction.Data0Y.Value, instruction.Data0Z.Value);
        var exponent = instruction.Data0W;

        if ((rx <= 0L) || (ry <= 0L) || (rz <= 0L) ||
            (exponent < FixedQ4816.FromDouble(value: SdfProgramBuilder.MinSuperellipsoidExponent)) ||
            (exponent > FixedQ4816.FromDouble(value: SdfProgramBuilder.MaxSuperellipsoidExponent))) {
            return null;
        }

        var minimum = Math.Min(val1: rx, val2: Math.Min(val1: ry, val2: rz));
        var maximum = Math.Max(val1: rx, val2: Math.Max(val1: ry, val2: rz));
        // E: SuperellipsoidScaledRatio rounds the exact p*s/r once, to nearest, at most half a raw; one whole raw outward.
        return new LocalCertificate(
            BoxLoss: 0.0, BoxRadius: 0.0, Boxed: false,
            CenterX: 0.0, CenterY: 0.0, CenterZ: 0.0,
            PointGain: ((((double)minimum) / maximum) / Sqrt3),
            PointSlack: RawUnit,
            Radius: (minimum * RawUnit)
        );
    }
    // SdfTrapezoidSolid's geometry, its profile T in the (x, y) plane of half-widths r1 below and r2 above and half-height
    // h, inside the disc of radius r = |(max r, h)|, revolved at radius lift or extruded to half-depth lift.
    //
    // Point: the 2D field is sign·√min(|ca|², |cb|²): ca is exact and ends on a cap, cb ends on the slant at the rounded,
    // clamped projection, so each length is at least the distance to the outline; the rounded squares and root cost at
    // most 2⁻⁸·⁵ units. The projection is within εp = 4 raws/|k2|² + 1 raw of the exact one, which moves cb by |k2|·εp
    // and its x by εx = |k2.x|·εp + ½ raw; a point the sign reads inside while it is outside then has cb.x in [0, εx) and
    // lies within μ = εx·|k2|/(2h) of the slant. So the 2D field is within 2μ + |k2|·εp + 512 raws of the exact signed
    // distance, and the extrusion's plain join and the revolution's rounded radius are 1-Lipschitz in it.
    //
    // Box: over a 2D box Q, both squared lengths' lower ends are their components' least magnitudes, each at least the
    // corresponding coordinate's least magnitude less max r or h, so the root is at least gap(Q, 0) − r; the interval is
    // that root when it is surely outside, which holds unless Q reaches inside ρ0 = |(2·max r, h)| of the origin, and
    // otherwise its lower end is the negated upper root, at least −(far(Q, 0) + h) ≥ −(ρ0 + 2ω₂ + h). So the lower end is
    // at least gap(Q, 0) − r − 2ρ0 − h − 2ω₂. The revolution reads gap(Q, 0) ≥ gap(B, 0) − |lift|, the extrusion's plain
    // join gap(B, 0) − |(r + 2ρ0 + h + 2ω₂, lift)|, and the chamfer's maximum only raises it.
    private static LocalCertificate? TryTrapezoidCertificate(in CompiledInstruction instruction) {
        var (r1, r2, h) = (((double)instruction.Data0X), ((double)instruction.Data0Y), ((double)instruction.Data0Z));
        var (kx, ky) = ((r2 - r1), (2.0 * h));
        var slantSquared = ((kx * kx) + (ky * ky));

        // ca ends on a cap only while both half-widths are non-negative.
        if ((h <= 0.0) || (r1 < 0.0) || (r2 < 0.0) || (slantSquared <= 0.0)) {
            return null;
        }

        var slant = Math.Sqrt(d: slantSquared);
        var projection = (((4.0 * RawUnit) / slantSquared) + RawUnit);
        var crossing = ((Math.Abs(value: kx) * projection) + (0.5 * RawUnit));
        var outline = ((crossing * slant) / (2.0 * h));
        var widest = Math.Max(val1: r1, val2: r2);
        var profile = Length(x: widest, y: h, z: 0.0);
        var inner = Length(x: (2.0 * widest), y: h, z: 0.0);
        var lift = Math.Abs(value: ((double)instruction.Data0W));
        var rounding = Math.Abs(value: ((double)instruction.Data1W));
        var extruded = (instruction.Data1Y > Half);
        var margin = (((2.0 * inner) + h) + (16.0 * RawUnit));

        return new LocalCertificate(
            BoxLoss: 2.0,
            BoxRadius: ((extruded ? Length(x: (profile + margin), y: lift, z: 0.0) : ((profile + margin) + lift)) + rounding),
            Boxed: true,
            CenterX: 0.0,
            CenterY: 0.0,
            CenterZ: 0.0,
            PointSlack: ((((2.0 * outline) + (slant * projection)) + (512.0 * RawUnit)) + (4.0 * RawUnit)),
            Radius: ((extruded ? Length(x: profile, y: lift, z: 0.0) : (lift + profile)) + rounding)
        );
    }
    // The widest entry of a rotation's enclosed linear map, in units.
    private static double EntryWidth(in RotationBounds rotation) {
        var widest = 0L;

        foreach (var entry in ((FixedInterval[])[rotation.M00, rotation.M01, rotation.M02, rotation.M10, rotation.M11, rotation.M12, rotation.M20, rotation.M21, rotation.M22,])) {
            widest = Math.Max(val1: widest, val2: (entry.Upper.Value - entry.Lower.Value));
        }

        return (widest * RawUnit);
    }
    private static double Length(double x, double y, double z) => Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));

    // A box a walk asks about, with what every box test reads of it: its doubled centre in raws, so a half raw stays
    // exact, and an upper bound on its half-diagonal in raws, √3 times its widest half-side rounded up.
    private readonly struct BoxQuery {
        public readonly IntervalVector3 Box;
        public readonly Int128 CentreX, CentreY, CentreZ;
        public readonly long Spread;

        public BoxQuery(IntervalVector3 box) {
            Box = box;
            CentreX = (((Int128)box.X.Lower.Value) + box.X.Upper.Value);
            CentreY = (((Int128)box.Y.Lower.Value) + box.Y.Upper.Value);
            CentreZ = (((Int128)box.Z.Lower.Value) + box.Z.Upper.Value);

            var widest = Int128.Max(x: (((Int128)box.X.Upper.Value) - box.X.Lower.Value), y: Int128.Max(x: (((Int128)box.Y.Upper.Value) - box.Y.Lower.Value), y: (((Int128)box.Z.Upper.Value) - box.Z.Lower.Value)));
            var half = ((ulong)Int128.Min(x: ((widest + 1) >> 1), y: (long.MaxValue / 8L)));

            // 3144134279 / 2³² exceeds √3 − 1.
            Spread = ((long)((half + ((ulong)((((UInt128)half) * 3144134279UL) >> 32))) + 1UL));
        }
    }
    // A certificate in raws, the form both walks test: v ≥ (Gain/Q32)·D − Radius. Gain rounds down and its inverse up;
    // for a box lower ≥ BoxGain·d − BoxRadius − BoxSpread·ω, each Q32 factor rounded the way that weakens the bound.
    private readonly record struct Reach(
        long CenterX, long CenterY, long CenterZ,
        long Radius, ulong Gain, ulong InverseGain,
        bool Boxed, bool BoxCentred, long BoxRadius, ulong BoxGain, ulong BoxInverseGain, ulong BoxSpread
    ) {
        // The distance past which a test's least distance is refused: 2⁶⁰ raws, far past any frame, keeps every doubled
        // square inside 128 bits.
        private const long LeastLimit = (1L << 60);

        // The certificate in raws, or none when a quantity is not finite, a gain is not positive or a raw would pass
        // 2⁶⁰, where the walks' frame could not hold the subtraction of its centre.
        public static bool TryOf(in ShapeReach shape, out Reach reach) {
            const double Limit = (1152921504606846976.0 * RawUnit);

            reach = default;

            foreach (var value in ((double[])[shape.CenterX, shape.CenterY, shape.CenterZ, shape.Radius,])) {
                if (!double.IsFinite(d: value) || (Math.Abs(value: value) >= Limit)) {
                    return false;
                }
            }

            if (!RepresentableGain(gain: shape.Gain)) {
                return false;
            }

            var boxed = (shape.Boxed && RepresentableGain(gain: shape.BoxGain) && (Math.Abs(value: shape.BoxRadius) < Limit) && (shape.BoxSpread < 1024.0));

            reach = Of(shape: (shape with { Boxed = boxed }));

            return true;
        }

        private static Reach Of(in ShapeReach shape) {
            var centreMagnitude = (Math.Max(val1: Math.Abs(value: shape.CenterX), val2: Math.Max(val1: Math.Abs(value: shape.CenterY), val2: Math.Abs(value: shape.CenterZ))) * Sqrt3);
            // A rounded centre is within √3/2 raws of the exact one, which every distance to it absorbs as one raw.
            var padding = (((centreMagnitude * DoublePadding) / RawUnit) + 2.0);

            return new Reach(
                BoxCentred: shape.BoxCentred,
                BoxGain: (shape.Boxed ? GainBits(gain: shape.BoxGain) : 0UL),
                BoxInverseGain: (shape.Boxed ? InverseGainBits(gain: shape.BoxGain) : 0UL),
                BoxRadius: (shape.Boxed ? RawsUp(units: shape.BoxRadius, padding: padding) : 0L),
                BoxSpread: (shape.Boxed ? FactorUp(factor: shape.BoxSpread) : 0UL),
                Boxed: shape.Boxed,
                CenterX: RawsNearest(units: shape.CenterX),
                CenterY: RawsNearest(units: shape.CenterY),
                CenterZ: RawsNearest(units: shape.CenterZ),
                Gain: GainBits(gain: shape.Gain),
                InverseGain: InverseGainBits(gain: shape.Gain),
                Radius: RawsUp(units: shape.Radius, padding: padding)
            );
        }

        // Whether every candidate the certificate bounds is at least threshold raws at a point: the largest axis
        // distance first, then the exact sum of squares; |p − C| ≥ L exactly when that sum reaches L².
        public bool PointClears(FixedVector3 point, Int128 threshold) {
            var least = Least(inverseGain: InverseGain, required: (threshold + Radius));

            if (least <= 0L) {
                return true;
            }

            return ((least < LeastLimit) && Reaches(least: ((UInt128)least), x: Magnitude(raw: (point.X.Value - CenterX)), y: Magnitude(raw: (point.Y.Value - CenterY)), z: Magnitude(raw: (point.Z.Value - CenterZ))));
        }
        // The point bound read off the largest axis distance alone, no greater than the bound at the exact distance.
        public long CheapPointReach(FixedVector3 point) {
            var axis = Math.Max(val1: Magnitude(raw: (point.X.Value - CenterX)), val2: Math.Max(val1: Magnitude(raw: (point.Y.Value - CenterY)), val2: Magnitude(raw: (point.Z.Value - CenterZ))));

            return Clamp(value: (((Int128)Scaled(magnitude: axis, factor: Gain)) - Radius));
        }
        // The most CheapPointReach can fall, in raws, between two points whose largest axis distances from any centre
        // differ by at most `move` raws: the gain is at most MaxGain, the floor in Scaled adds one raw, and the clamp only
        // narrows a difference.
        public static long CheapPointReachFall(long move) => ((MaxGain * move) + 1L);
        // The largest gain a certificate holds (RepresentableGain), which bounds how fast its cheap reach changes.
        private const long MaxGain = 2L;
        // Whether every candidate interval's lower end over the box is at least threshold raws.
        public bool BoxClears(in BoxQuery query, Int128 threshold) {
            var least = Least(inverseGain: BoxInverseGain, required: ((threshold + BoxRadius) + SpreadRaws(factor: BoxSpread, omega: query.Spread)));

            if (least <= 0L) {
                return true;
            }

            if (least >= LeastLimit) {
                return false;
            }

            if (BoxCentred) {
                var doubled = (((UInt128)least) << 1);

                return Reaches(least: doubled, x: DoubledMagnitude(centre: query.CentreX, raw: CenterX), y: DoubledMagnitude(centre: query.CentreY, raw: CenterY), z: DoubledMagnitude(centre: query.CentreZ, raw: CenterZ));
            }

            return Reaches(least: ((UInt128)least), x: Gap(axis: query.Box.X, centre: CenterX), y: Gap(axis: query.Box.Y, centre: CenterY), z: Gap(axis: query.Box.Z, centre: CenterZ));
        }
        // The box lower bound read off the largest axis distance alone.
        public long CheapBoxReach(in BoxQuery query) {
            var axis = (BoxCentred
                ? ((ulong)UInt128.Min(x: (UInt128.Max(x: DoubledMagnitude(centre: query.CentreX, raw: CenterX), y: UInt128.Max(x: DoubledMagnitude(centre: query.CentreY, raw: CenterY), y: DoubledMagnitude(centre: query.CentreZ, raw: CenterZ))) >> 1), y: ulong.MaxValue))
                : Math.Max(val1: Gap(axis: query.Box.X, centre: CenterX), val2: Math.Max(val1: Gap(axis: query.Box.Y, centre: CenterY), val2: Gap(axis: query.Box.Z, centre: CenterZ))));

            return Clamp(value: ((((Int128)Scaled(magnitude: axis, factor: BoxGain)) - BoxRadius) - SpreadRaws(factor: BoxSpread, omega: query.Spread)));
        }

        private static long Least(ulong inverseGain, Int128 required) {
            if (required <= Int128.Zero) {
                return 0L;
            }

            // TryOf admits gain <= 2, hence its upward inverse >= 2^31. This is already past the clamp.
            // Below it required < 2^61 and inverse < 2^64, so the product plus rounding fits signed Int128.
            if (required >= (((Int128)LeastLimit) * 2)) {
                return LeastLimit;
            }

            var least = ((((required * inverseGain) + (((Int128)1) << 32)) - 1) >> 32);

            return ((least >= LeastLimit) ? LeastLimit : ((long)least));
        }
        // Whether a vector of these component magnitudes is at least least long.
        private static bool Reaches(UInt128 least, UInt128 x, UInt128 y, UInt128 z) {
            if (UInt128.Max(x: x, y: UInt128.Max(x: y, y: z)) >= least) {
                return true;
            }

            return ((((x * x) + (y * y)) + (z * z)) >= (least * least));
        }
        private static ulong Magnitude(long raw) => FusedArithmetic.RawMagnitude(value: raw);
        private static UInt128 DoubledMagnitude(Int128 centre, long raw) {
            var difference = (centre - (((Int128)raw) << 1));

            return ((UInt128)((difference < Int128.Zero) ? -difference : difference));
        }
        private static ulong Gap(FixedInterval axis, long centre) {
            var (lower, upper) = (axis.Lower.Value, axis.Upper.Value);

            return ((centre < lower) ? ((ulong)(lower - centre)) : ((centre > upper) ? ((ulong)(centre - upper)) : 0UL));
        }
        private static ulong Scaled(ulong magnitude, ulong factor) => ((ulong)((((UInt128)magnitude) * factor) >> 32));
        private static ulong ScaledUp(ulong magnitude, ulong factor) => ((ulong)(((((UInt128)magnitude) * factor) + uint.MaxValue) >> 32));
        private static long SpreadRaws(ulong factor, long omega) => ((long)Math.Min(val1: ScaledUp(factor: factor, magnitude: ((ulong)omega)), val2: ((ulong)(long.MaxValue / 4L))));
        private static long Clamp(Int128 value) => ((long)Int128.Clamp(max: (long.MaxValue / 2L), min: (long.MinValue / 2L), value: value));
        // Certificate representation only: valid fields remain readable when the gain has no safe raw form.
        private static bool RepresentableGain(double gain) =>
            ((gain > 0.0) && (gain <= MaxGain) &&
             (Math.Floor(d: ((gain * (1.0 - DoublePadding)) * Q32)) >= 1.0) &&
             (Math.Ceiling(a: (((1.0 + DoublePadding) / gain) * Q32)) < ((double)ulong.MaxValue)));
        private static ulong GainBits(double gain) => ((ulong)Math.Floor(d: ((gain * (1.0 - DoublePadding)) * Q32)));
        private static ulong InverseGainBits(double gain) => (((ulong)Math.Ceiling(a: (((1.0 + DoublePadding) / gain) * Q32))) + 1UL);
        private static ulong FactorUp(double factor) => (((ulong)Math.Ceiling(a: ((factor * (1.0 + DoublePadding)) * Q32))) + 1UL);
        private static long RawsUp(double units, double padding) => checked((long)Math.Ceiling(a: (((units * (1.0 + DoublePadding)) / RawUnit) + padding)));
        private static long RawsNearest(double units) => checked((long)Math.Round(mode: MidpointRounding.ToEven, value: (units / RawUnit)));
    }

    // The certificate around several, all in world units, about the members' mean centre or a given one: the least gain,
    // and each member's radius grown by its gain times its centre's distance from the centre, which the triangle
    // inequality allows. Its box terms read the box centre's distance when any member's do; a member that reads the gap
    // then counts one more half-diagonal per unit of gain, since the gap is at least the centre's distance less it.
    private static ShapeReach Enclose(IReadOnlyList<ShapeReach> members, (double X, double Y, double Z)? centre = null) {
        var (x, y, z) = (0.0, 0.0, 0.0);
        var centred = false;

        foreach (var member in members) {
            x += member.CenterX;
            y += member.CenterY;
            z += member.CenterZ;
            centred |= member.BoxCentred;
        }

        (x, y, z) = (centre ?? ((x / members.Count), (y / members.Count), (z / members.Count)));

        var enclosure = new ShapeReach(
            BoxCentred: centred,
            BoxGain: double.MaxValue,
            BoxRadius: 0.0,
            BoxSpread: 0.0,
            Boxed: true,
            CenterX: x,
            CenterY: y,
            CenterZ: z,
            Gain: double.MaxValue,
            Radius: 0.0
        );

        foreach (var member in members) {
            var offset = Length(x: (member.CenterX - x), y: (member.CenterY - y), z: (member.CenterZ - z));

            enclosure = enclosure with {
                BoxGain = Math.Min(val1: enclosure.BoxGain, val2: member.BoxGain),
                BoxRadius = Math.Max(val1: enclosure.BoxRadius, val2: (member.BoxRadius + (member.BoxGain * offset))),
                BoxSpread = Math.Max(val1: enclosure.BoxSpread, val2: (member.BoxSpread + ((centred && !member.BoxCentred) ? member.BoxGain : 0.0))),
                Boxed = (enclosure.Boxed && member.Boxed),
                Gain = Math.Min(val1: enclosure.Gain, val2: member.Gain),
                Radius = Math.Max(val1: enclosure.Radius, val2: (member.Radius + (member.Gain * offset))),
            };
        }

        return enclosure;
    }
}
