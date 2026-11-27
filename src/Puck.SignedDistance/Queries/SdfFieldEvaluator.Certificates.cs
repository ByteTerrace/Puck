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
// point transforms from its reset to the shape the way the walks apply them. Under the render reading each also holds the
// field whose parameters are the packed floats (CompiledInstruction's THE PARAMETER ENCLOSURES): a lane off the grid
// widens each step by what any parameter its enclosure holds can move the point or the candidate, as each step says.
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
        // The same certificate for a candidate scaled by an s > 0 in [low, high] and rounded once (a scope's pop), after
        // subtracting a blend slack from the field it scales: a gain takes the least scale, a radius the greatest.
        public ShapeReach Scaled(double low, double high, double slack) => ((high > 0.0)
            ? this with {
                BoxGain = (BoxGain * low),
                BoxRadius = (((BoxRadius + slack) * high) + RawUnit),
                BoxSpread = (BoxSpread * high),
                Gain = (Gain * low),
                Radius = (((Radius + slack) * high) + RawUnit),
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
            // A translation off the grid moves the local point, and the box's centre and half-extents, by at most its
            // enclosure's widths.
            var spread = LaneSpread(first: instruction.Bounds0X, second: instruction.Bounds0Y, third: instruction.Bounds0Z);

            if (double.IsInfinity(d: spread)) {
                Known = false;

                return;
            }

            OriginX += ((((M00 * x) + (M01 * y)) + (M02 * z)) * Scale);
            OriginY += ((((M10 * x) + (M11 * y)) + (M12 * z)) * Scale);
            OriginZ += ((((M20 * x) + (M21 * y)) + (M22 * z)) * Scale);
            PointA += ((PointB * length) + spread);
            BoxA += ((BoxB * length) + spread);
            BoxDA += ((BoxDB * length) + spread);
        }
        public void Rotate(in CompiledInstruction instruction) {
            var (x, y, z, w) = (((double)instruction.Data0X), ((double)instruction.Data0Y), ((double)instruction.Data0Z), ((double)instruction.Data0W));
            var vectorSquared = (((x * x) + (y * y)) + (z * z));
            var normSquared = (vectorSquared + (w * w));

            if (normSquared <= 0.25) {
                Known = false;

                return;
            }

            var width = EntryWidth(rotation: RotationBounds.Of(instruction: in instruction));

            // An entry no enclosure bounds leaves the turn unbounded, as an unbounded translation does.
            if (double.IsInfinity(d: width)) {
                Known = false;

                return;
            }
            // ‖M − R̂‖ for the raws' map, and, for a quaternion off the grid, what any map of its enclosure adds: each entry
            // of such a map and of the raws' lies in the enclosed entry (RotationBounds over the enclosure), so their
            // difference's spectral norm is at most its Frobenius norm, three times the widest entry's width.
            var deviation = ((2.0 * Math.Abs(value: (1.0 - normSquared))) + (instruction.ExactData0 ? 0.0 : (3.0 * width)));
            var growth = (1.0 + deviation);
            var rounding = ((Sqrt3 * (Math.Sqrt(d: vectorSquared) + 1.0)) * RawUnit);
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
            var halfTurn = (instruction.ExactData0 && (instruction.Data0W == zero) && (
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
            BoxB = ((growth * BoxB) + deviation);
            PointA = ((growth * PointA) + rounding);
            PointB = ((growth * PointB) + deviation);
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
            // Axes off the grid each divide by some magnitude in [low, high] about the raws' one: a local point or box
            // centre within ρ = (high − low)/low of the raws' image, relative to its own distance, and a half-extent and
            // a distance scale over the whole range.
            var (low, high) = ScaleRange(instruction: in instruction);

            if (!(low > 0.0)) {
                Known = false;

                return;
            }

            var spread = ((high - low) / low);
            var (sx, sy, sz) = (((double)Math.Sign(value: instruction.Data0X.Value)), ((double)Math.Sign(value: instruction.Data0Y.Value)), ((double)Math.Sign(value: instruction.Data0Z.Value)));

            (M00, M01, M02, M10, M11, M12, M20, M21, M22) = ((M00 * sx), (M01 * sy), (M02 * sz), (M10 * sx), (M11 * sy), (M12 * sz), (M20 * sx), (M21 * sy), (M22 * sz));
            (P00, P01, P02, P10, P11, P12, P20, P21, P22) = ((P00 / low), (P01 / low), (P02 / low), (P10 / low), (P11 / low), (P12 / low), (P20 / low), (P21 / low), (P22 / low));
            Scale *= scale;
            PointA = ((PointA / low) + RawUnit);
            PointB = ((PointB * (1.0 + spread)) + spread);
            BoxA /= low;
            BoxB = ((BoxB * (1.0 + spread)) + spread);
            BoxDA = ((BoxDA / low) + RawUnit);
            BoxDB *= (1.0 + spread);
            PointScaleLow = ((PointScaleLow * low) - (0.5 * RawUnit));
            PointScaleHigh = ((PointScaleHigh * high) + (0.5 * RawUnit));
            BoxScaleLow = ((BoxScaleLow * low) - RawUnit);
            BoxScaleHigh = ((BoxScaleHigh * high) + RawUnit);

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

            switch (SdfFieldOps.CertificateChainOf(value: instruction.Op)) {
                case SdfCertificateChainAction.Identity: {
                        break;
                    }
                case SdfCertificateChainAction.Translate: {
                        chain.Translate(instruction: in instruction);
                        break;
                    }
                case SdfCertificateChainAction.Rotate: {
                        if (!(instruction.ExactData0 && IsIdentityRotation(instruction: in instruction))) {
                            chain.Rotate(instruction: in instruction);
                        }

                        break;
                    }
                case SdfCertificateChainAction.Scale: {
                        if (!(instruction.ExactData0 && IsUnitScale(instruction: in instruction))) {
                            chain.ScaleBy(instruction: in instruction);
                        }

                        break;
                    }
                case SdfCertificateChainAction.ShapeBlend: {
                        if (!instruction.Detail && (instruction.ScopeScale == 0L)) {
                            reaches[index] = chain.Certify(instruction: in instruction);
                        }

                        break;
                    }
                case SdfCertificateChainAction.Unknown: {
                        chain = ChainCertificate.Unknown;
                        break;
                    }
                default:
                    throw new InvalidOperationException(message: "Invalid CertificateChain action.");
            }
        }

        return reaches;
    }
    // A local field certificate, or none for a primitive not covered. Distance-like fields use the distance to a
    // containing sphere; the superellipsoid supplies its own field gain and rounding slack. The box rules of
    // Sphere, Box, Cylinder and Torus read each coordinate's range once, so each endpoint of their interval is the exact
    // field at a point of the box, rounded outward by a floor or ceiling root.
    // Under the render reading, a shape whose lanes or side table lie off the grid: its certificate over its raws, widened
    // by what its floats can move the field by (ParameterReach) and by the point rule's own slack once more, since that
    // slack bounds the rounded raws' field and the floats' field is exact.
    private static LocalCertificate? TryLocalCertificate(in CompiledInstruction instruction) {
        var raw = RawCertificate(instruction: in instruction);

        if ((raw is not { } certificate) || instruction.Exact) {
            return raw;
        }

        var reach = (ParameterReach(instruction: in instruction) + certificate.PointSlack);

        return certificate with {
            BoxRadius = (certificate.Boxed ? (certificate.BoxRadius + reach) : certificate.BoxRadius),
            Radius = (certificate.Radius + reach),
        };
    }
    // What a shape's floats off the grid can move a covered shape's field by: each covered field moves by at most its
    // outline's displacement, at most twice for a signed one, and each lane moves the outline by at most its own change
    // times √3 (a box's half-extents together, a capsule's end, a trapezoid's half-widths and height), so three times the
    // sum of the lanes' widths bounds it, and a side table's points move by at most a raw each (TableOutlineReach).
    private static double ParameterReach(in CompiledInstruction instruction) {
        var widths = ((((Width(lane: instruction.Bounds0X) + Width(lane: instruction.Bounds0Y)) + (Width(lane: instruction.Bounds0Z) + Width(lane: instruction.Bounds0W))) +
            ((Width(lane: instruction.Bounds1X) + Width(lane: instruction.Bounds1Y)) + (Width(lane: instruction.Bounds1Z) + Width(lane: instruction.Bounds1W)))) * 3.0);

        return ((widths + (instruction.TableInexact ? (4.0 * RawUnit) : 0.0)) * (1.0 + DoublePadding));

        static double Width(FixedInterval lane) => (lane.IsUnbounded ? double.PositiveInfinity : (((double)lane.Upper) - ((double)lane.Lower)));
    }
    private static LocalCertificate? RawCertificate(in CompiledInstruction instruction) {
        const double Slack = (4.0 * RawUnit);

        switch (SdfFieldShapes.CertificateOf(value: (SdfShapeType)instruction.Shape)) {
            case SdfCertificateAction.Sphere: {
                    var radius = Math.Abs(value: ((double)instruction.Data0X));

                    return Separable(radius: radius);
                }
            case SdfCertificateAction.Box: {
                    var corner = instruction.Data0W;

                    if (
                        (instruction.Data0X < FixedQ4816.Zero) || (instruction.Data0Y < FixedQ4816.Zero) || (instruction.Data0Z < FixedQ4816.Zero) ||
                        (corner < FixedQ4816.Zero) || (corner > FixedQ4816.Min(x: instruction.Data0X, y: FixedQ4816.Min(x: instruction.Data0Y, y: instruction.Data0Z)))
                    ) {
                        return null;
                    }

                    return Separable(radius: Length(x: ((double)instruction.Data0X), y: ((double)instruction.Data0Y), z: ((double)instruction.Data0Z)));
                }
            case SdfCertificateAction.Cylinder: {
                    if ((instruction.Data0X < FixedQ4816.Zero) || (instruction.Data0Y < FixedQ4816.Zero)) {
                        return null;
                    }

                    return Separable(radius: (Length(x: ((double)instruction.Data0X), y: ((double)instruction.Data0Y), z: 0.0) + Math.Abs(value: ((double)instruction.Data1W))));
                }
            case SdfCertificateAction.Torus: {
                    return Separable(radius: (Math.Abs(value: ((double)instruction.Data0X)) + Math.Abs(value: ((double)instruction.Data0Y))));
                }
            case SdfCertificateAction.Capsule: {
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
            case SdfCertificateAction.Superellipsoid: {
                    return TrySuperellipsoidCertificate(instruction: in instruction);
                }
            case SdfCertificateAction.Trapezoid: {
                    return TryTrapezoidCertificate(instruction: in instruction);
                }
            case SdfCertificateAction.Sweep: {
                    return TrySweepCertificate(instruction: in instruction);
                }
            case SdfCertificateAction.Unavailable: {
                    return null;
                }
            default:
                throw new InvalidOperationException(message: "Invalid Certificate action.");
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
    // Point-only: the sweep's candidate is |p - S| - r at a strand centre S and a radius r the formula or the search
    // picked. A centreline point is a convex combination of a, b and c, so it lies in the ball about the control points'
    // box centre C through the farthest of them, of radius R; a strand stands at most strandOffset off it, and r is at most
    // the larger end radius plus a positive bulge (the profile's power is at most one). So v >= |p - C| - R -
    // strandOffset - max(radiusStart, radiusEnd) - max(bulge, 0). The point side's Lerps, the orbit's frame and power and
    // the final root round a few raws apiece: the offset and the bulge take a hundredth of themselves, and the slack 32 raws.
    private static LocalCertificate? TrySweepCertificate(in CompiledInstruction instruction) {
        if (instruction.SweepCurve is not { } curve) {
            return null;
        }

        var (ax, ay, az) = (((double)curve.A.X), ((double)curve.A.Y), ((double)curve.A.Z));
        var (bx, by, bz) = (((double)curve.B.X), ((double)curve.B.Y), ((double)curve.B.Z));
        var (cx, cy, cz) = (((double)curve.C.X), ((double)curve.C.Y), ((double)curve.C.Z));
        var centreX = (0.5 * (Math.Min(val1: ax, val2: Math.Min(val1: bx, val2: cx)) + Math.Max(val1: ax, val2: Math.Max(val1: bx, val2: cx))));
        var centreY = (0.5 * (Math.Min(val1: ay, val2: Math.Min(val1: by, val2: cy)) + Math.Max(val1: ay, val2: Math.Max(val1: by, val2: cy))));
        var centreZ = (0.5 * (Math.Min(val1: az, val2: Math.Min(val1: bz, val2: cz)) + Math.Max(val1: az, val2: Math.Max(val1: bz, val2: cz))));
        var hull = Math.Max(
            val1: Length(x: (ax - centreX), y: (ay - centreY), z: (az - centreZ)),
            val2: Math.Max(
                val1: Length(x: (bx - centreX), y: (by - centreY), z: (bz - centreZ)),
                val2: Length(x: (cx - centreX), y: (cy - centreY), z: (cz - centreZ))
            )
        );
        var radius = Math.Max(val1: ((double)curve.RadiusStart), val2: ((double)curve.RadiusEnd));

        return new LocalCertificate(
            BoxLoss: 0.0, BoxRadius: 0.0, Boxed: false,
            CenterX: centreX, CenterY: centreY, CenterZ: centreZ,
            PointSlack: (32.0 * RawUnit),
            Radius: (((hull + radius) + (1.01 * Math.Abs(value: ((double)instruction.Data0W)))) + (1.01 * Math.Max(val1: ((double)curve.Bulge), val2: 0.0)))
        );
    }
    private static LocalCertificate? TrySuperellipsoidCertificate(in CompiledInstruction instruction) {
        var (rx, ry, rz) = (instruction.Data0X.Value, instruction.Data0Y.Value, instruction.Data0Z.Value);
        var exponent = instruction.Data0W;

        if ((rx <= 0L) || (ry <= 0L) || (rz <= 0L) ||
            (exponent < FixedQ4816.FromDouble(value: SdfProgramBuilder.MinSuperellipsoidExponent)) ||
            (exponent > FixedQ4816.FromDouble(value: SdfProgramBuilder.MaxSuperellipsoidExponent))) {
            return null;
        }

        // The least radius at its least and the greatest at its greatest over the radii's enclosures: the gain falls and
        // the radius rises as the enclosures widen, so the certificate holds for every radius they hold.
        var (bx, by, bz) = (instruction.Bounds0X, instruction.Bounds0Y, instruction.Bounds0Z);

        if (bx.IsUnbounded || by.IsUnbounded || bz.IsUnbounded || (bx.Lower.Value <= 0L) || (by.Lower.Value <= 0L) || (bz.Lower.Value <= 0L)) {
            return null;
        }

        var minimum = Math.Min(val1: bx.Lower.Value, val2: Math.Min(val1: by.Lower.Value, val2: bz.Lower.Value));
        var maximum = Math.Max(val1: bx.Upper.Value, val2: Math.Max(val1: by.Upper.Value, val2: bz.Upper.Value));
        var minimumReach = Math.Min(val1: bx.Upper.Value, val2: Math.Min(val1: by.Upper.Value, val2: bz.Upper.Value));
        // E: SuperellipsoidScaledRatio rounds the exact p*s/r once, to nearest, at most half a raw; one whole raw outward.
        return new LocalCertificate(
            BoxLoss: 0.0, BoxRadius: 0.0, Boxed: false,
            CenterX: 0.0, CenterY: 0.0, CenterZ: 0.0,
            PointGain: ((((double)minimum) / maximum) / Sqrt3),
            PointSlack: RawUnit,
            Radius: (minimumReach * RawUnit)
        );
    }
    // SdfTrapezoidSolid's geometry, its profile T in the (x, y) plane of half-widths r1 below and r2 above and half-height
    // h, inside the disc of radius r = |(max r, h)|, revolved at radius lift or extruded to half-depth lift.
    //
    // Point: the 2D field is sign·√min(|ca|², |cb|²): ca is exact and ends on a cap, cb ends on the slant at the rounded,
    // clamped projection, so each length is at least the distance to the outline; the rounded squares and root cost at
    // most 2⁻⁸·⁵ units. The projection rounds the exact quotient once, so it is within εp = 1 raw of the exact one, which moves cb by |k2|·εp
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
        var projection = RawUnit;
        var crossing = ((Math.Abs(value: kx) * projection) + (0.5 * RawUnit));
        var outline = ((crossing * slant) / (2.0 * h));
        var widest = Math.Max(val1: r1, val2: r2);
        var profile = Length(x: widest, y: h, z: 0.0);
        var inner = Length(x: (2.0 * widest), y: h, z: 0.0);
        var lift = Math.Abs(value: ((double)instruction.Data0W));
        var rounding = Math.Abs(value: ((double)instruction.Data1W));
        // Extrusion only where the lift's enclosure is past one half: the revolution's radius, the sum, is the larger.
        var extruded = (!instruction.Bounds1Y.IsUnbounded && (instruction.Bounds1Y.Lower > Half));
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
    // The length of three lanes' enclosure widths, in units, padded past the double's rounding: how far a vector those
    // lanes hold can lie from their raws. Infinite for an unbounded lane.
    private static double LaneSpread(FixedInterval first, FixedInterval second, FixedInterval third) {
        if (first.IsUnbounded || second.IsUnbounded || third.IsUnbounded) {
            return double.PositiveInfinity;
        }

        static double Width(FixedInterval lane) => (((double)lane.Upper) - ((double)lane.Lower));

        return (Length(x: Width(lane: first), y: Width(lane: second), z: Width(lane: third)) * (1.0 + DoublePadding));
    }
    // The least and greatest magnitude a Scale's axes and distance scale reach over their enclosures, padded outward past
    // the double's rounding; the raws' magnitude on the grid, and a non-positive least where an enclosure reaches zero.
    private static (double Low, double High) ScaleRange(in CompiledInstruction instruction) {
        if (instruction.ExactData0) {
            var scale = ((double)instruction.Data0W);

            return (scale, scale);
        }

        var (low, high) = (double.PositiveInfinity, 0.0);

        foreach (var lane in ((ReadOnlySpan<FixedInterval>)[instruction.Bounds0X, instruction.Bounds0Y, instruction.Bounds0Z, instruction.Bounds0W])) {
            if (lane.IsUnbounded) {
                return (0.0, double.PositiveInfinity);
            }

            var (lower, upper) = (((double)lane.Lower), ((double)lane.Upper));

            low = Math.Min(val1: low, val2: ((lower > 0.0) ? lower : ((upper < 0.0) ? -upper : 0.0)));
            high = Math.Max(val1: high, val2: Math.Max(val1: Math.Abs(value: lower), val2: Math.Abs(value: upper)));
        }

        return ((low * (1.0 - DoublePadding)), (high * (1.0 + DoublePadding)));
    }
    // The widest entry of a rotation's enclosed linear map, in units; infinite where an entry is unbounded, whose ends hold
    // no width.
    private static double EntryWidth(in RotationBounds rotation) {
        var widest = 0L;

        foreach (var entry in ((ReadOnlySpan<FixedInterval>)[rotation.M00, rotation.M01, rotation.M02, rotation.M10, rotation.M11, rotation.M12, rotation.M20, rotation.M21, rotation.M22,])) {
            if (entry.IsUnbounded) {
                return double.PositiveInfinity;
            }

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

        // Whether every candidate the certificate bounds is at least threshold raws at a point.
        public bool PointClears(FixedVector3 point, Int128 threshold) =>
            ClearsAt(threshold: threshold, x: Magnitude(raw: (point.X.Value - CenterX)), y: Magnitude(raw: (point.Y.Value - CenterY)), z: Magnitude(raw: (point.Z.Value - CenterZ)));
        // The point bound read off the largest axis distance alone, no greater than the bound at the exact distance.
        public long CheapPointReach(FixedVector3 point) =>
            CheapReachAt(x: Magnitude(raw: (point.X.Value - CenterX)), y: Magnitude(raw: (point.Y.Value - CenterY)), z: Magnitude(raw: (point.Z.Value - CenterZ)));
        // Whether every candidate the certificate bounds is at least threshold raws at every point of the box: the point
        // test at the box's nearest point to the centre, which no point of the box stands nearer than.
        public bool RegionClears(in BoxQuery query, Int128 threshold) =>
            ClearsAt(threshold: threshold, x: Gap(axis: query.Box.X, centre: CenterX), y: Gap(axis: query.Box.Y, centre: CenterY), z: Gap(axis: query.Box.Z, centre: CenterZ));
        // The point bound over a box read off the largest axis gap alone, no greater than the bound at any point of it.
        public long CheapRegionReach(in BoxQuery query) =>
            CheapReachAt(x: Gap(axis: query.Box.X, centre: CenterX), y: Gap(axis: query.Box.Y, centre: CenterY), z: Gap(axis: query.Box.Z, centre: CenterZ));

        // The point test at a point these axis distances from the centre: the largest axis distance first, then the
        // exact sum of squares; |p − C| ≥ L exactly when that sum reaches L².
        private bool ClearsAt(Int128 threshold, ulong x, ulong y, ulong z) {
            var least = Least(inverseGain: InverseGain, required: (threshold + Radius));

            if (least <= 0L) {
                return true;
            }

            return ((least < LeastLimit) && Reaches(least: ((UInt128)least), x: x, y: y, z: z));
        }
        private long CheapReachAt(ulong x, ulong y, ulong z) =>
            Clamp(value: (((Int128)Scaled(magnitude: Math.Max(val1: x, val2: Math.Max(val1: y, val2: z)), factor: Gain)) - Radius));
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
