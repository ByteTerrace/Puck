using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Tests;

public sealed partial class SdfTapePredictionLawTests {
    private static FixedInterval I(float value) => SdfTapeCertificate.EncloseFloat(value: value);
    private static FixedInterval P(FixedQ4816 value) => FixedInterval.FromPoint(value: value);
    private static float Upper(FixedInterval value) => SdfTapeCertificate.UpperFloat(value: value);
    private static bool Above(FixedQ4816? lower, FixedQ4816? upper, FixedInterval gap) =>
        (lower.HasValue && upper.HasValue && !(P(value: upper.Value) + gap).IsUnbounded && (lower.Value > (P(value: upper.Value) + gap).Upper));

    // Nullable endpoints retain one-sided information after an unsupported field: min(unknown, finite) has a finite
    // upper bound, while max has a finite lower bound. FixedInterval still owns every bounded arithmetic operation.
    private readonly record struct Range(FixedQ4816? Lower, FixedQ4816? Upper) {
        public static Range Unknown => new(Lower: null, Upper: null);

        public static Range Point(float value) => Of(value: I(value: value));
        public static Range Of(FixedInterval value) => (value.IsUnbounded ? Unknown : new(Lower: value.Lower, Upper: value.Upper));
        public Range Negate() => new(Lower: -Upper, Upper: -Lower);
        public Range Scale(float scale) {
            if (scale < 0f) { return Negate().Scale(scale: -scale); }
            if (scale == 0f) { return Point(value: 0); }
            var result = new Range(Lower: Expand(value: Lower, amount: I(value: scale), product: true, upper: false),
                Upper: Expand(value: Upper, amount: I(value: scale), product: true, upper: true));

            return result.Pad(amount: RoundMargin(value: result.Magnitude));
        }
        public Range Pad(FixedInterval amount) => new(
            Lower: Expand(value: Lower, amount: -amount, product: false, upper: false),
            Upper: Expand(value: Upper, amount: amount, product: false, upper: true));
        public Range Add(FixedInterval amount) {
            var result = new Range(Lower: Expand(value: Lower, amount: amount, product: false, upper: false),
                Upper: Expand(value: Upper, amount: amount, product: false, upper: true));

            return result.Pad(amount: RoundMargin(value: (Magnitude + FixedInterval.Abs(value: amount))));
        }
        public Range Absolute() {
            if (Lower.HasValue && Upper.HasValue) { return Of(value: FixedInterval.Abs(value: new FixedInterval(lower: Lower.Value, upper: Upper.Value))); }
            return new Range(Lower: FixedQ4816.Zero, Upper: null);
        }

        public FixedInterval Magnitude {
            get {
                var lower = (Lower.HasValue ? FixedInterval.Abs(value: P(value: Lower.Value)) : Zero);
                var upper = (Upper.HasValue ? FixedInterval.Abs(value: P(value: Upper.Value)) : Zero);

                return FixedInterval.Max(first: lower, second: upper);
            }
        }

        private static FixedQ4816? Expand(FixedQ4816? value, FixedInterval amount, bool product, bool upper) {
            if (!value.HasValue) { return null; }
            var result = (product ? (P(value: value.Value) * amount) : (P(value: value.Value) + amount));

            return (result.IsUnbounded ? null : (upper ? result.Upper : result.Lower));
        }
    }

    private static FixedInterval Band(SdfBlendOp blend, float smooth) =>
        ((blend is SdfBlendOp.SmoothUnion or SdfBlendOp.SmoothIntersection or SdfBlendOp.SmoothSubtraction)
            ? FixedInterval.Max(first: I(value: smooth), second: I(value: 0.0001f)) : Zero);
    private static FixedInterval RoundMargin(FixedInterval value) => ((value * I(value: 64)) / I(value: 16777216));
    private static FixedInterval Margin(Range current, Range candidate, FixedInterval band) =>
        RoundMargin(value: (FixedInterval.Max(first: current.Magnitude, second: candidate.Magnitude) + band));
    private static FixedInterval Separation(Range current, Range candidate, SdfBlendOp blend, float smooth) {
        var band = Band(blend: blend, smooth: smooth);

        return (band + Margin(band: band, candidate: candidate, current: current));
    }
    private static bool Loses(Range current, Range candidate, SdfBlendOp blend, float smooth) {
        var union = (blend is SdfBlendOp.Union or SdfBlendOp.SmoothUnion);
        var band = Band(blend: blend, smooth: smooth);

        if (current.Lower.HasValue && current.Upper.HasValue && candidate.Lower.HasValue && candidate.Upper.HasValue) {
            var smoothBlend = (blend is SdfBlendOp.SmoothUnion or SdfBlendOp.SmoothIntersection or SdfBlendOp.SmoothSubtraction);

            return SdfTapeCertificate.CandidateLoses(
                current: new FixedInterval(lower: current.Lower.Value, upper: current.Upper.Value),
                candidate: new FixedInterval(lower: candidate.Lower.Value, upper: candidate.Upper.Value),
                blend: (union ? (smoothBlend ? SdfBlendOp.SmoothUnion : SdfBlendOp.Union)
                    : (smoothBlend ? SdfBlendOp.SmoothIntersection : SdfBlendOp.Intersection)),
                radius: band.Upper);
        }
        var separation = Separation(blend: blend, candidate: candidate, current: current, smooth: smooth);

        return (union ? Above(lower: candidate.Lower, upper: current.Upper, gap: separation)
            : Above(lower: current.Lower, upper: candidate.Upper, gap: separation));
    }
    private static Range Combine(Range current, Range candidate, SdfBlendOp blend, float smooth, bool candidateSigned = false) {
        var union = (blend is SdfBlendOp.Union or SdfBlendOp.SmoothUnion);

        if (!union && (blend is not (SdfBlendOp.Intersection or SdfBlendOp.SmoothIntersection or SdfBlendOp.Subtraction or SdfBlendOp.SmoothSubtraction))) {
            return Range.Unknown;
        }
        if (!candidateSigned && (blend is SdfBlendOp.Subtraction or SdfBlendOp.SmoothSubtraction)) { candidate = candidate.Negate(); }
        var lower = (union ? Lesser(first: current.Lower, second: candidate.Lower, missingSmaller: true)
            : Greater(first: current.Lower, second: candidate.Lower, missingLarger: false));
        var upper = (union ? Lesser(first: current.Upper, second: candidate.Upper, missingSmaller: false)
            : Greater(first: current.Upper, second: candidate.Upper, missingLarger: true));
        var band = Band(blend: blend, smooth: smooth);
        var result = new Range(Lower: lower, Upper: upper);

        return ((blend is SdfBlendOp.SmoothUnion or SdfBlendOp.SmoothIntersection or SdfBlendOp.SmoothSubtraction)
            ? result.Pad(amount: ((band * I(value: 0.25f)) + Margin(band: band, candidate: candidate, current: current))) : result);
    }
    private static FixedQ4816? Lesser(FixedQ4816? first, FixedQ4816? second, bool missingSmaller) =>
        ((first.HasValue && second.HasValue) ? FixedQ4816.Min(x: first.Value, y: second.Value) : (missingSmaller ? null : (first ?? second)));
    private static FixedQ4816? Greater(FixedQ4816? first, FixedQ4816? second, bool missingLarger) =>
        ((first.HasValue && second.HasValue) ? FixedQ4816.Max(x: first.Value, y: second.Value) : (missingLarger ? null : (first ?? second)));

    private readonly record struct V(FixedInterval X, FixedInterval Y, FixedInterval Z) {
        public static V Of(Vector3 value) => new(X: I(value: value.X), Y: I(value: value.Y), Z: I(value: value.Z));

        public static V operator +(V a, V b) => new(X: (a.X + b.X), Y: (a.Y + b.Y), Z: (a.Z + b.Z));
        public static V operator -(V a, V b) => new(X: (a.X - b.X), Y: (a.Y - b.Y), Z: (a.Z - b.Z));
        public static V operator *(V a, FixedInterval b) => new(X: (a.X * b), Y: (a.Y * b), Z: (a.Z * b));
        public static V operator /(V a, FixedInterval b) => new(X: (a.X / b), Y: (a.Y / b), Z: (a.Z / b));

        public FixedInterval Length => FixedInterval.Magnitude(x: X, y: Y, z: Z);

        public static FixedInterval Dot(V a, V b) => (((a.X * b.X) + (a.Y * b.Y)) + (a.Z * b.Z));
        public static V Cross(V a, V b) => new(X: ((a.Y * b.Z) - (a.Z * b.Y)), Y: ((a.Z * b.X) - (a.X * b.Z)), Z: ((a.X * b.Y) - (a.Y * b.X)));
        public V Rotate(Quaternion rotation) {
            var q = Of(value: new Vector3(x: rotation.X, y: rotation.Y, z: rotation.Z));

            return (this + (Cross(a: q, b: (Cross(a: q, b: this) - (this * I(value: rotation.W)))) * I(value: 2)));
        }
        public V Divide(Vector3 scale) => new(X: (X / I(value: scale.X)), Y: (Y / I(value: scale.Y)), Z: (Z / I(value: scale.Z)));
    }
    private readonly record struct TileCone(Vector3 Direction, float Chord, V CertifiedDirection);
    private readonly record struct BallDomain(Vector3 Centre, float Magnitude, float Radius);

    private static TileCone Cone(CameraSnapshot camera, int x, int y) {
        var right = Math.Min(val1: (x + Tile), val2: Width);
        var bottom = Math.Min(val1: (y + Tile), val2: Height);
        var direction = Ray(camera: camera, x: ((x + right) * 0.5f), y: ((y + bottom) * 0.5f));
        var middle = new Vector3(x: Mid(value: direction.X), y: Mid(value: direction.Y), z: Mid(value: direction.Z));

        middle = Vector3.Normalize(value: middle);
        var chord = Zero;

        foreach (var (px, py) in new[] { (x, y), (right, y), (x, bottom), (right, bottom) }) {
            chord = FixedInterval.Max(first: chord, second: (Ray(camera: camera, x: px, y: py) - V.Of(value: middle)).Length);
        }
        chord += (direction - V.Of(value: middle)).Length;
        return new TileCone(Direction: middle, Chord: Upper(value: chord), CertifiedDirection: direction);
    }
    private static V Ray(CameraSnapshot camera, float x, float y) {
        var ndcX = (((I(value: x) * I(value: 2)) / I(value: Width)) - One);
        var ndcY = (One - ((I(value: y) * I(value: 2)) / I(value: Height)));
        var raw = ((V.Of(value: camera.Forward) + (V.Of(value: camera.Right) * ((ndcX * I(value: camera.AspectRatio)) * I(value: camera.TanHalfFieldOfView))))
            + (V.Of(value: camera.Up) * (ndcY * I(value: camera.TanHalfFieldOfView))));

        return (raw / raw.Length);
    }
    private static float Mid(FixedInterval value) => ((float)((((double)value.Lower) + ((double)value.Upper)) * 0.5));
    private static BallDomain Ball(CameraSnapshot camera, TileCone cone, float far, int slab, int depthPower) {
        var s0 = (((float)slab) / Slabs);
        var s1 = (((float)(slab + 1)) / Slabs);

        if (depthPower >= 2) { s0 *= s0; s1 *= s1; }
        if (depthPower >= 4) { s0 *= s0; s1 *= s1; }
        return Ball(camera: camera, cone: cone, far: far, s0: s0, s1: s1);
    }
    private static BallDomain Ball(CameraSnapshot camera, TileCone cone, float far, float s0, float s1) {
        var entry = SdfFrameBlock.NearOf(camera: camera);
        var near = (entry + ((far - entry) * s0));
        var end = (entry + ((far - entry) * s1));
        var middle = ((near + end) * 0.5f);
        var centre = (camera.Position + (cone.Direction * middle));
        var halfDepth = ((I(value: end) - I(value: near)) * I(value: 0.5f));
        var squared = (FixedInterval.Square(value: halfDepth) + ((I(value: middle) * I(value: end)) * FixedInterval.Square(value: I(value: cone.Chord))));
        var guard = RoundMargin(value: ((I(value: Magnitude(value: camera.Position)) + I(value: end)) + One));
        var radius = Upper(value: (FixedInterval.Sqrt(value: squared) + guard));

        return new BallDomain(Centre: centre, Magnitude: Magnitude(value: centre), Radius: radius);
    }
    private static float Magnitude(Vector3 value) => Math.Max(val1: Math.Abs(value: value.X), val2: Math.Max(val1: Math.Abs(value: value.Y), val2: Math.Abs(value: value.Z)));

    private readonly record struct InstanceBound(int First, int End, V Centre, float Radius, bool Hidden, bool Unmaskable);

    private static InstanceBound[] Instances(SdfFrame frame) => [.. frame.Program.Instances.Select(selector: (instance, index) =>
        new InstanceBound(First: instance.First, End: instance.End,
            Centre: (V.Of(value: instance.Center) + V.Of(value: (instance.IsDynamic ? frame.DynamicTransforms[instance.Slot].Position : Vector3.Zero))),
            Radius: frame.Program.InspectInstance(index: index).BoundRadius, Hidden: instance.CameraHidden,
            Unmaskable: frame.Program.InspectInstance(index: index).Unmaskable))];
    private static bool Visible(InstanceBound bound, Vector3 origin, TileCone cone) {
        if (bound.Hidden || (bound.Radius < 0)) { return false; }
        if (bound.Unmaskable) { return true; }
        var toCentre = (bound.Centre - V.Of(value: origin));
        var along = FixedInterval.Max(first: V.Dot(a: toCentre, b: cone.CertifiedDirection), second: Zero);
        var distance = (toCentre - (cone.CertifiedDirection * along)).Length;
        var radius = ((I(value: bound.Radius) + (I(value: cone.Chord) * along)) / FixedInterval.Sqrt(value: (One - FixedInterval.Square(value: I(value: cone.Chord)))));

        return (distance.IsUnbounded || radius.IsUnbounded || (distance.Lower <= radius.Upper));
    }
}
