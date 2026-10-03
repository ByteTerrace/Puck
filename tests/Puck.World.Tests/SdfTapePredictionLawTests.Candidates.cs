using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;

namespace Puck.World.Tests;

public sealed partial class SdfTapePredictionLawTests {
    private readonly record struct Entry(int Index, SdfOp Op, Candidate? Candidate, float Scale, SdfBlendOp Blend, float Smooth);
    private enum Rejection { None, Certificate, Transform, Domain, Primitive }
    // This is an interval adapter to the production primitive enclosure and packed execution plan. It neither
    // samples an independently implemented float primitive nor predicts a backend's centre-evaluation bits.
    private sealed partial record Candidate(SdfInstruction Instruction, SdfTapeCertificate Certificate, Affine Point,
        FixedInterval DistanceScale, DynamicTransform? Pose) {
        public string Category { get; } = ($"{((SdfShapeType)Instruction.Shape)} flags={Certificate.Flags}"
            + ((Instruction.Shape == ((uint)SdfShapeType.Superellipsoid)) ? $" exponent={Instruction.Data0.W}" : ""));

        public FixedInterval Bounds(BallDomain ball, out Rejection rejection) {
            if (!Domain(ball: ball, magnitude: out var magnitude, radius: out var radius, rejection: out rejection)) {
                return FixedInterval.Entire;
            }
            var centre = Primitive(point: V.Of(value: ball.Centre));
            var error = Error(magnitude: magnitude, radius: radius);
            // Primitive encloses the full clamped norm band. Its unknown float centre is inside that band plus A;
            // the production radius adds two further A terms and the centre-dependent paired norm allowance.
            var expansion = (((I(value: Certificate.Lipschitz) * I(value: radius)) + (error * I(value: 3)))
                + NormAllowance(error: error, ideal: centre));
            var result = FixedInterval.Union(first: (centre - expansion), second: (centre + expansion));

            rejection = (result.IsUnbounded ? Rejection.Primitive : Rejection.None);
            return result;
        }

        private FixedInterval Primitive(V point) {
            var local = Point.At(value: point);

            return (SdfFieldEvaluator.EnclosePrimitive(instruction: Instruction, x: local.X, y: local.Y, z: local.Z) * DistanceScale);
        }
        private FixedInterval Error(float radius, float magnitude) =>
            ((I(value: Certificate.ErrorSlope) * (I(value: magnitude) + I(value: radius))) + I(value: Certificate.ErrorOffset));
        private FixedInterval NormAllowance(FixedInterval ideal, FixedInterval error) =>
            (((Certificate.Flags & SdfTapeCertificate.CenteredNormEnvelope) == 0) ? Zero
                : ((SdfTapeCertificate.SuperellipsoidCenterGain(exponent: Instruction.Data0.W) * I(value: 2))
                    * (FixedInterval.Abs(value: ideal) + error)));
        private bool Domain(BallDomain ball, out float radius, out float magnitude, out Rejection rejection) {
            radius = ball.Radius;
            magnitude = ball.Magnitude;
            rejection = Rejection.Certificate;
            if ((Certificate.Flags & SdfTapeCertificate.Certified) == 0) { return false; }
            rejection = Rejection.Transform;
            if (!Point.Known) { return false; }
            rejection = Rejection.Domain;

            if ((Certificate.Flags & SdfTapeCertificate.DynamicFrame) != 0) {
                if (Pose is not { } pose) { return false; }
                var transformed = SdfTapeCertificate.TransformBall(centreMagnitude: magnitude, radius: radius,
                    position: pose.Position, orientation: pose.Orientation);
                var centreError = SdfTapeCertificate.TransformBall(centreMagnitude: magnitude, radius: 0,
                    position: pose.Position, orientation: pose.Orientation);

                if (!transformed.IsCertified || !centreError.IsCertified) { return false; }
                radius = Upper(value: (I(value: transformed.Radius) + I(value: centreError.Radius)));
                magnitude = transformed.Magnitude;
            }
            return ((((double)magnitude) + radius) <= SdfTapeCertificate.CoordinateLimit);
        }
    }
    private readonly record struct Affine(V X, V Y, V Z, V Offset, bool Known) {
        public static Affine Identity => new(X: V.Of(value: Vector3.UnitX), Y: V.Of(value: Vector3.UnitY),
            Z: V.Of(value: Vector3.UnitZ), Offset: V.Of(value: Vector3.Zero), Known: true);

        public V At(V value) => ((((X * value.X) + (Y * value.Y)) + (Z * value.Z)) + Offset);
        public Affine Translate(Vector3 offset) => this with { Offset = (Offset - V.Of(value: offset)) };
        public Affine Rotate(Quaternion rotation) => new(X: X.Rotate(rotation: rotation), Y: Y.Rotate(rotation: rotation),
            Z: Z.Rotate(rotation: rotation), Offset: Offset.Rotate(rotation: rotation), Known: Known);
        public Affine Scale(Vector3 scale) => new(X: X.Divide(scale: scale), Y: Y.Divide(scale: scale),
            Z: Z.Divide(scale: scale), Offset: Offset.Divide(scale: scale), Known: Known);
        public Affine Transform(DynamicTransform pose) => Translate(offset: pose.Position).Rotate(rotation: pose.Orientation);
    }

    private static Entry[] Candidates(SdfFrame frame) {
        var program = frame.Program;
        var candidates = new Candidate?[program.InstructionCount];
        var point = Affine.Identity;
        var scale = One;
        DynamicTransform? pose = null;

        for (var index = 0; (index < program.InstructionCount); index++) {
            var instruction = program.Instructions[index];
            var data = instruction.Data0;

            switch (instruction.Op) {
                case SdfOp.ResetPoint:
                    point = Affine.Identity; scale = One; pose = null;
                    break;
                case SdfOp.Translate:
                    point = point.Translate(offset: new Vector3(x: data.X, y: data.Y, z: data.Z));
                    break;
                case SdfOp.Rotate:
                    point = point.Rotate(rotation: new Quaternion(w: data.W, x: data.X, y: data.Y, z: data.Z));
                    break;
                case SdfOp.Scale:
                    point = point.Scale(scale: new Vector3(x: data.X, y: data.Y, z: data.Z));
                    scale *= I(value: data.W);
                    break;
                case SdfOp.TransformDynamic:
                    pose = frame.DynamicTransforms[((int)data.X)];
                    point = point.Transform(pose: pose.Value);
                    break;
                case SdfOp.ShapeBlend:
                    candidates[index] = new Candidate(Instruction: instruction, Certificate: program.TapeCertificate(instruction: index),
                        Point: point, DistanceScale: scale, Pose: pose);
                    break;
                case SdfOp.PushField or SdfOp.PopField or SdfOp.Dilate or SdfOp.Onion or SdfOp.Displace or SdfOp.CellDisplace or SdfOp.NoiseDisplace:
                    break;
                default:
                    point = default;
                    break;
            }
        }
        RigidCandidates(candidates: candidates, frame: frame);
        var result = new List<Entry>();

        for (var index = 0; (index < program.InstructionCount); index++) {
            var instruction = program.Instructions[index];

            if (instruction.Op is SdfOp.ShapeBlend or SdfOp.PushField or SdfOp.PopField or SdfOp.Dilate or SdfOp.Onion
                or SdfOp.Displace or SdfOp.CellDisplace or SdfOp.NoiseDisplace) {
                result.Add(item: new Entry(Index: index, Op: instruction.Op, Candidate: candidates[index],
                    Scale: ((instruction.Op == SdfOp.PopField) ? instruction.Data1.Y : instruction.Data0.X),
                    Blend: ((SdfBlendOp)instruction.Blend), Smooth: instruction.Data1.X));
            }
        }
        return [.. result];
    }
    private static void RigidCandidates(SdfFrame frame, Candidate?[] candidates) {
        var program = frame.Program;
        var words = program.Words;
        var segmentHeader = checked((((int)((words[SdfProgram.ProgramMaterialOffsetLane]
            + (((uint)SdfProgram.MaterialVectorsPerEntry) * words[SdfProgram.ProgramMaterialCountLane]))
            + (((uint)SdfProgram.BoundRecordVectors) * words[SdfProgram.ProgramInstructionCountLane]))) * 4));
        var directory = checked((((int)words[(segmentHeader + SdfProgram.SegmentRigidPlanLane)]) * 4));

        for (var segment = 0; (segment < program.SkipSegmentCount); segment++) {
            var first = checked((((int)words[(directory + (segment * 4))]) * 4));
            var count = ((int)words[((directory + (segment * 4)) + 1)]);
            var slot = (((int)words[((directory + (segment * 4)) + 2)]) - 1);
            DynamicTransform? pose = ((slot < 0) ? null : frame.DynamicTransforms[slot]);

            for (var leaf = 0; (leaf < count); leaf++) {
                var offset = (first + (leaf * 12));
                var packed = words[(offset + 3)];
                var shape = ((int)(packed & SdfProgram.RigidLeafShapeMask));
                var candidate = candidates[shape];

                if (candidate is null) { throw new InvalidOperationException(message: "Rigid leaf does not name a candidate."); }
                if ((packed & SdfProgram.RigidLeafFoldedFlag) != 0) {
                    candidates[shape] = candidate with { Point = default };
                    leaf++;
                    continue;
                }
                var point = (pose.HasValue ? Affine.Identity.Transform(pose: pose.Value) : Affine.Identity);

                point = point.Translate(offset: new Vector3(x: F(word: words[offset]), y: F(word: words[(offset + 1)]), z: F(word: words[(offset + 2)])));
                if ((packed & SdfProgram.RigidLeafIdentityRotationFlag) == 0) {
                    point = point.Rotate(rotation: new Quaternion(x: F(word: words[(offset + 4)]), y: F(word: words[(offset + 5)]),
                        z: F(word: words[(offset + 6)]), w: F(word: words[(offset + 7)])));
                }
                candidates[shape] = candidate with { Point = point, DistanceScale = One, Pose = pose };
            }
        }
    }
    private static float F(uint word) => BitConverter.UInt32BitsToSingle(value: word);
}
