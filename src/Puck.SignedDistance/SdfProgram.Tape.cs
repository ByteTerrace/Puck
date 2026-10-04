using System.Numerics;
using Puck.Maths;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    /// <summary>The segment header's vector offset of the per-instruction float error and Lipschitz certificates.</summary>
    public const int SegmentTapeLane = 3;
    /// <summary>The minimum masked instruction count for which the tile tape pays its construction cost.</summary>
    public const int TapeInstructionThreshold = 30;

    /// <summary>Reads the certificate the GPU uses for an instruction. A zero-flags record leaves it live.</summary>
    /// <param name="instruction">The instruction index.</param>
    /// <returns>The packed candidate certificate.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The instruction is absent.</exception>
    public SdfTapeCertificate TapeCertificate(int instruction) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: instruction);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: instruction, other: InstructionCount);
        var offset = ((((int)m_words[(SegmentDirectoryWord() + SegmentTapeLane)]) + instruction) * WordsPerVector);

        return new SdfTapeCertificate(
            Lipschitz: BitConverter.UInt32BitsToSingle(value: m_words[offset]),
            ErrorSlope: BitConverter.UInt32BitsToSingle(value: m_words[(offset + 1)]),
            ErrorOffset: BitConverter.UInt32BitsToSingle(value: m_words[(offset + 2)]),
            Flags: m_words[(offset + 3)] & TapeCertificateFlagsMask
        );
    }

    private void PackTapeCertificates(int offset, int segmentOffset, List<BoundRecord> segments, RigidPlan rigid) {
        m_words[((segmentOffset * WordsPerVector) + SegmentTapeLane)] = ((uint)offset);
        PackTapeTokens(offset: offset, segmentOffset: segmentOffset, segmentCount: segments.Count);
        var state = TapeMetric.Identity;

        for (var segmentIndex = 0; (segmentIndex < segments.Count); segmentIndex++) {
            var segment = segments[segmentIndex];
            var omissible = TapeSegmentIsIndependent(segment: segment);
            var rigidSegment = rigid.Segments[segmentIndex];
            var rigidLeaf = rigidSegment.FirstLeaf;

            for (var index = segment.Instruction; (index < segment.End); index++) {
                var instruction = m_instructions[index];

                if (instruction.Op != SdfOp.ShapeBlend) {
                    state = state.Apply(instruction: instruction);
                    continue;
                }
                var candidateState = state;

                if (rigidSegment.LeafCount > 0) {
                    var leaf = rigid.Leaves[rigidLeaf];

                    rigidLeaf += ((leaf.FoldCount > 0) ? 2 : 1);
                    candidateState = ((leaf.FoldCount == 0)
                        ? (TapeMetric.Identity with { DynamicFrame = (rigidSegment.DynamicSlot != NoDynamicTransformSlot) })
                            .Translate(offset: leaf.Position).Rotate(rotation: leaf.Rotation)
                        : default);
                }
                var certificate = candidateState.Certify(instruction: instruction, omissible: omissible);
                var word = ((offset + index) * WordsPerVector);

                m_words[word] = BitConverter.SingleToUInt32Bits(value: certificate.Lipschitz);
                m_words[(word + 1)] = BitConverter.SingleToUInt32Bits(value: certificate.ErrorSlope);
                m_words[(word + 2)] = BitConverter.SingleToUInt32Bits(value: certificate.ErrorOffset);
                m_words[(word + 3)] |= certificate.Flags;
            }
        }
    }

    internal static SdfTapeBall TransformTapeBall(float centreMagnitude, float radius, Vector3 position, Quaternion orientation) =>
        TapeMetric.PoseIdentity.Translate(offset: position).Rotate(polynomialEnvelope: true, rotation: orientation)
            .TransformBall(centreMagnitude: centreMagnitude, radius: radius);

    private bool TapeSegmentIsIndependent(BoundRecord segment) {
        if (((segment.Instruction != 0) && (m_instructions[segment.Instruction].Op != SdfOp.ResetPoint))
            || ((segment.End < InstructionCount) && (m_instructions[segment.End].Op != SdfOp.ResetPoint))) {
            return false;
        }
        for (var index = segment.Instruction; (index < segment.End); index++) {
            if (m_instructions[index].Op is not (SdfOp.ResetPoint or SdfOp.Translate or SdfOp.Rotate or SdfOp.Scale
                or SdfOp.Elongate or SdfOp.ShapeBlend)) { return false; }
            if ((m_instructions[index].Op == SdfOp.ShapeBlend)
                && (m_instructions[index].Detail || !m_instructions[index].Secondary)) { return false; }
        }
        return true;
    }

    // This is a certificate calculation, not a field evaluator. FixedInterval supplies every arithmetic enclosure.
    // L bounds the exact point map; A*x+B bounds its magnitude for x=max(abs(world)). E*x+F bounds accumulated
    // rounding error in units of 2^-24. The fixed raw used while propagating that error is an upward enclosure of
    // 2^-24; the final power-of-two scale is applied only after directed conversion to float.
    private readonly record struct TapeMetric(bool Known, FixedInterval L, FixedInterval A, FixedInterval B,
        FixedInterval E, FixedInterval F, FixedInterval DistanceScale, int ScaleOperations, bool DynamicFrame = false) {
        private static FixedInterval One => SdfTapeCertificate.Integer(value: 1);
        private static FixedInterval Zero => SdfTapeCertificate.Integer(value: 0);
        private static FixedInterval RoundUnit => FixedInterval.FromPoint(value: new FixedQ4816(Value: 1));

        public static TapeMetric Identity => new(Known: true, L: One,
            A: FixedInterval.Sqrt(value: SdfTapeCertificate.Integer(value: 3)), B: Zero, E: Zero, F: Zero,
            DistanceScale: One, ScaleOperations: 0);
        public static TapeMetric PoseIdentity => Identity with {
            A = (SdfTapeCertificate.Integer(value: 7) / SdfTapeCertificate.Integer(value: 4)),
        };

        public TapeMetric Apply(SdfInstruction instruction) => instruction.Op switch {
            SdfOp.ResetPoint => Identity,
            SdfOp.TransformDynamic when (this == Identity) => Identity with { DynamicFrame = true },
            SdfOp.Translate => Translate(offset: new Vector3(x: instruction.Data0.X, y: instruction.Data0.Y, z: instruction.Data0.Z)),
            SdfOp.Rotate => Rotate(rotation: new Quaternion(instruction.Data0.X, instruction.Data0.Y, instruction.Data0.Z, instruction.Data0.W)),
            SdfOp.Scale => Scale(scale: instruction.Data0),
            SdfOp.Elongate when ((instruction.Data0.X >= 0f) && (instruction.Data0.Y >= 0f) && (instruction.Data0.Z >= 0f))
                => AddRound(cost: One, addition: Magnitude(value: instruction.Data0)),
            _ => default,
        };
        public TapeMetric Translate(Vector3 offset) => AddRound(cost: One, addition: Magnitude(value: new Vector4(value: offset, w: 0f))) with {
            B = (B + Magnitude(value: new Vector4(value: offset, w: 0f))),
        };
        public TapeMetric Rotate(Quaternion rotation, bool polynomialEnvelope = false) {
            if (rotation.IsIdentity && !polynomialEnvelope) { return this; }
            var q = new Vector4(w: rotation.W, x: rotation.X, y: rotation.Y, z: rotation.Z);
            var x = SdfTapeCertificate.EncloseFloat(value: q.X);
            var y = SdfTapeCertificate.EncloseFloat(value: q.Y);
            var z = SdfTapeCertificate.EncloseFloat(value: q.Z);
            var w = SdfTapeCertificate.EncloseFloat(value: q.W);
            var s = ((FixedInterval.Square(value: x) + FixedInterval.Square(value: y)) + FixedInterval.Square(value: z));
            // The quaternion polynomial has singular values 1 and sqrt(1+4*s*(s+w*w-1)), even when its packed
            // quaternion is not exactly unit length. Normalization in a builder is not an exact-isometry proof.
            var squared = (s + FixedInterval.Square(value: w));
            // Runtime pose certificates need no hardware sqrt: s <= squared bounds the norm by
            // max(1, 2*squared-1), since 1+4*squared*(squared-1) = (2*squared-1)^2 above squared=1.
            var norm = (polynomialEnvelope
                ? FixedInterval.Max(first: One, second: ((SdfTapeCertificate.Integer(value: 2) * squared) - One))
                : FixedInterval.Sqrt(value: FixedInterval.Max(first: One,
                    second: (One + ((SdfTapeCertificate.Integer(value: 4) * s) * (squared - One))))));
            var absolute = (One + (SdfTapeCertificate.Integer(value: 8) * squared));
            var rounded = AddRound(cost: (SdfTapeCertificate.Integer(value: 32) * absolute), addition: Zero);

            return rounded with { L = (L * norm), A = (A * norm), B = (B * norm), E = (rounded.E * norm), F = (rounded.F * norm) };
        }

        private TapeMetric Scale(Vector4 scale) {
            if ((scale.X <= 0f) || (scale.Y <= 0f) || (scale.Z <= 0f) || (scale.W <= 0f)) { return default; }
            var divisor = FixedInterval.Min(first: SdfTapeCertificate.EncloseFloat(value: scale.X),
                second: FixedInterval.Min(first: SdfTapeCertificate.EncloseFloat(value: scale.Y), second: SdfTapeCertificate.EncloseFloat(value: scale.Z)));
            var inverse = (One / divisor);
            var distance = (DistanceScale * SdfTapeCertificate.EncloseFloat(value: scale.W));
            var scaled = this with {
                L = (L * inverse),
                A = (A * inverse),
                B = (B * inverse),
                E = (E * inverse),
                F = (F * inverse),
                DistanceScale = distance,
                ScaleOperations = (ScaleOperations + 1),
            };

            return scaled.AddRound(cost: SdfTapeCertificate.Integer(value: 4), addition: Zero);
        }
        private TapeMetric AddRound(FixedInterval cost, FixedInterval addition) => this with {
            E = (E + (cost * (A + (RoundUnit * E)))),
            F = (F + (cost * (((B + addition) + (RoundUnit * F)) + One))),
        };

        public SdfTapeBall TransformBall(float centreMagnitude, float radius) {
            if (!Known || !float.IsFinite(f: centreMagnitude) || !float.IsFinite(f: radius)
                || (centreMagnitude < 0f) || (radius < 0f) || ((((double)centreMagnitude) + radius) > SdfTapeCertificate.CoordinateLimit)) { return default; }
            var centre = SdfTapeCertificate.EncloseFloat(value: centreMagnitude);
            var extent = SdfTapeCertificate.EncloseFloat(value: radius);
            var error = (((E * (centre + extent)) + F) / SdfTapeCertificate.Integer(value: 16777216));
            var transformedRadius = ((L * extent) + (SdfTapeCertificate.Integer(value: 2) * error));
            var transformedMagnitude = (((A * centre) + B) + error);

            if (transformedRadius.IsUnbounded || transformedMagnitude.IsUnbounded
                || (SdfTapeCertificate.UpperFloat(value: (transformedRadius + transformedMagnitude)) > SdfTapeCertificate.CoordinateLimit)) { return default; }
            return new SdfTapeBall(Radius: SdfTapeCertificate.UpperFloat(value: transformedRadius),
                Magnitude: SdfTapeCertificate.UpperFloat(value: transformedMagnitude), IsCertified: true);
        }
        public SdfTapeCertificate Certify(SdfInstruction instruction, bool omissible) {
            if (!Known || !TryShape(cost: out var shapeCost, gain: out var shapeGain, instruction: instruction,
                lipschitz: out var shapeL, normGap: out var normGap, normRadius: out var normRadius, parameters: out var parameters,
                rawGain: out var rawGain, rawParameters: out var rawParameters)) { return default; }
            var magnitudeA = (shapeGain * A);
            var magnitudeB = ((shapeGain * B) + parameters);
            var roundedInputGain = FixedInterval.Max(first: One, second: shapeGain);
            var errorA = ((shapeL * E) + (shapeCost * (magnitudeA + (RoundUnit * (roundedInputGain * E)))));
            var errorB = ((shapeL * F) + (shapeCost * ((magnitudeB + (RoundUnit * (roundedInputGain * F))) + One)));
            var scaleCost = SdfTapeCertificate.Integer(value: (2 * (ScaleOperations + 1)));

            errorA = ((errorA + (scaleCost * magnitudeA)) * DistanceScale);
            errorB = ((errorB + (scaleCost * (magnitudeB + One))) * DistanceScale);
            var fieldL = ((L * shapeL) * DistanceScale);
            var centeredNorm = (normGap.Upper > FixedQ4816.Zero);

            if (centeredNorm) {
                // Let N be the scaled L2 norm, R the scaled minimum radius and E the arithmetic error. The clamp
                // gives d+R-E <= Ncentre <= (d+R+E)/alpha and |Nsample-Ncentre| <= L*r. Substituting these into
                // alpha*Nsample-R-E <= dsample <= Nsample-R+E bounds either distance change by
                // L*r + (gap/alpha)*(|d|+R) + 2*E/alpha. The shader adds the centre term; these coefficients
                // contain the other terms. No world-coordinate estimate of the norm band is needed.
                var alpha = (One - normGap);
                var radiusUnits = ((SdfTapeCertificate.Integer(value: 8388608) * normGap) * (normRadius * DistanceScale));

                errorA /= alpha;
                errorB = ((errorB + radiusUnits) / alpha);
            }
            var limit = SdfTapeCertificate.EncloseFloat(value: SdfTapeCertificate.CoordinateLimit);
            var maxMagnitude = ((magnitudeA * limit) + magnitudeB);
            var maxRawMagnitude = ((rawGain * ((A * limit) + B)) + rawParameters);
            var maxError = ((errorA * limit) + errorB);
            var l = SdfTapeCertificate.UpperFloat(value: fieldL);
            var e = SdfTapeCertificate.UpperFloat(binaryScale: -24, value: errorA);
            var f = SdfTapeCertificate.UpperFloat(binaryScale: -24, value: errorB);

            if (!float.IsFinite(f: l) || !float.IsFinite(f: e) || !float.IsFinite(f: f)
                || (SdfTapeCertificate.UpperFloat(value: maxMagnitude) > SdfTapeCertificate.IntermediateLimit)
                || (SdfTapeCertificate.UpperFloat(value: maxRawMagnitude) > SdfTapeCertificate.IntermediateLimit)
                || (SdfTapeCertificate.UpperFloat(binaryScale: -24, value: maxError) > SdfTapeCertificate.IntermediateLimit)) { return default; }
            return new SdfTapeCertificate(ErrorOffset: f, ErrorSlope: e, Flags: SdfTapeCertificate.Certified | (omissible ? SdfTapeCertificate.OmissibleSegment : 0u)
                | (DynamicFrame ? SdfTapeCertificate.DynamicFrame : 0u) | (centeredNorm ? SdfTapeCertificate.CenteredNormEnvelope : 0u),
                Lipschitz: l);
        }

        // Arithmetic budgets cover abs/min/max, the norm's dot product and sqrt, and the final multiply. A power
        // gauge is admitted only with its enforced norm envelope; its full band supplies normGap separately.
        // Projection with a baked reciprocal retains that reciprocal in its bound.
        private static bool TryShape(SdfInstruction instruction, out FixedInterval lipschitz, out FixedInterval gain,
            out FixedInterval parameters, out FixedInterval cost, out FixedInterval normGap, out FixedInterval normRadius,
            out FixedInterval rawGain, out FixedInterval rawParameters) {
            lipschitz = One;
            gain = One;
            normGap = Zero;
            normRadius = Zero;
            parameters = (Magnitude(value: instruction.Data0) + Magnitude(value: instruction.Data1));
            rawParameters = parameters;
            rawGain = One;
            cost = SdfTapeCertificate.Integer(value: 256);
            switch ((SdfShapeType)(instruction.Shape & ShapeTypeMask)) {
                case SdfShapeType.Sphere:
                case SdfShapeType.Box:
                case SdfShapeType.Torus:
                case SdfShapeType.Cylinder:
                case SdfShapeType.ScreenSlab:
                    return true;
                case SdfShapeType.Plane:
                    lipschitz = FixedInterval.Magnitude(x: SdfTapeCertificate.EncloseFloat(value: instruction.Data0.X),
                        y: SdfTapeCertificate.EncloseFloat(value: instruction.Data0.Y), z: SdfTapeCertificate.EncloseFloat(value: instruction.Data0.Z));
                    gain = lipschitz;
                    return true;
                case SdfShapeType.Capsule: {
                        var endpoint = Magnitude(value: new Vector4(instruction.Data0.X, instruction.Data0.Y, instruction.Data0.Z, 0f));

                        gain = (One + (FixedInterval.Square(value: endpoint) * FixedInterval.Abs(value: SdfTapeCertificate.EncloseFloat(value: instruction.Data1.Y))));
                        lipschitz = gain;
                        return true;
                    }
                case SdfShapeType.Superellipsoid when ((instruction.Data0.W >= 2f) && (instruction.Data0.W <= 3f)): {
                        if ((instruction.Data0.X <= 0f) || (instruction.Data0.Y <= 0f) || (instruction.Data0.Z <= 0f)
                            || (instruction.Data1.Y <= 0f) || (instruction.Data1.Z <= 0f) || (instruction.Data1.W <= 0f)) { return false; }
                        var inverse = FixedInterval.Max(first: SdfTapeCertificate.EncloseFloat(value: instruction.Data1.Y),
                            second: FixedInterval.Max(first: SdfTapeCertificate.EncloseFloat(value: instruction.Data1.Z), second: SdfTapeCertificate.EncloseFloat(value: instruction.Data1.W)));
                        var minimum = FixedInterval.Min(first: SdfTapeCertificate.EncloseFloat(value: instruction.Data0.X),
                            second: FixedInterval.Min(first: SdfTapeCertificate.EncloseFloat(value: instruction.Data0.Y), second: SdfTapeCertificate.EncloseFloat(value: instruction.Data0.Z)));

                        lipschitz = (inverse * minimum);
                        // Every norm endpoint is multiplied by minimum before it becomes a field value. Its
                        // arithmetic error therefore scales by minimum too; the baked inverse constants are not
                        // additive field terms. The unscaled q norm retains its own overflow-domain check.
                        gain = lipschitz;
                        parameters = minimum;
                        rawGain = inverse;
                        if (instruction.Data0.W > 2f) {
                            normGap = (One - SdfTapeCertificate.SuperellipsoidNormFactor(exponent: instruction.Data0.W));
                            normRadius = minimum;
                            cost = SdfTapeCertificate.Integer(value: 512);
                        }
                        return true;
                    }
                case SdfShapeType.RoundedRectangle:
                case SdfShapeType.ChamferedRectangle:
                case SdfShapeType.Trapezoid:
                    lipschitz = FixedInterval.Max(first: One, second: (FixedInterval.Sqrt(value: SdfTapeCertificate.Integer(value: 2)) * SdfTapeCertificate.EncloseFloat(value: SqrtHalf)));
                    gain = SdfTapeCertificate.Integer(value: 2);
                    parameters *= SdfTapeCertificate.Integer(value: 4);
                    if (((SdfShapeType)(instruction.Shape & ShapeTypeMask)) == SdfShapeType.Trapezoid) {
                        if ((instruction.Data0.X < 0f) || (instruction.Data0.Y < 0f) || (instruction.Data0.Z <= 0f)) { return false; }
                        var sideX = (SdfTapeCertificate.EncloseFloat(value: instruction.Data0.Y) - SdfTapeCertificate.EncloseFloat(value: instruction.Data0.X));
                        var sideY = (SdfTapeCertificate.Integer(value: 2) * SdfTapeCertificate.EncloseFloat(value: instruction.Data0.Z));
                        var denominator = (FixedInterval.Square(value: sideX) + FixedInterval.Square(value: sideY));
                        // sdfTrapezoid2D divides its projection by dot(k2,k2). A positive fixed lower endpoint is
                        // at least 2^-16, far above float flush-to-zero. A bounded upper endpoint is below 2^47;
                        // with |p| <= 1e12 the numerator and quotient then stay below float overflow too.
                        if (denominator.IsUnbounded || (denominator.Lower <= FixedQ4816.Zero)) { return false; }
                        // The checked slant never vanishes. Dot/divide/project errors cancel its length from the
                        // distance error: projection error O(u*|p|/|edge|) is multiplied by |edge| again. A sign
                        // uncertainty lies on a cap/side boundary, whose minimum edge distance has that same bound.
                        cost = SdfTapeCertificate.Integer(value: 512);
                    }
                    return true;
                default:
                    return false;
            }
        }
        private static FixedInterval Magnitude(Vector4 value) =>
            ((FixedInterval.Abs(value: SdfTapeCertificate.EncloseFloat(value: value.X))
                + FixedInterval.Abs(value: SdfTapeCertificate.EncloseFloat(value: value.Y)))
                + (FixedInterval.Abs(value: SdfTapeCertificate.EncloseFloat(value: value.Z))
                    + FixedInterval.Abs(value: SdfTapeCertificate.EncloseFloat(value: value.W))));
    }
}
