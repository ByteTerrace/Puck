using System.Numerics;
using Puck.Assets.Documents;

namespace Puck.World.Authoring;

public static partial class CreationCanonicalizer {
    // The band around a squared length of 1 inside which a direction or rotation is canonical as written. Canonical
    // form must be a fixed point, and renormalizing a float unit vector moves its last bits, so a value inside the band
    // is never renormalized. A value outside it is divided by its length in double and each component rounded once,
    // which leaves the squared length within 2^-22 of 1: well inside the band, so the result is canonical.
    private const double UnitBand = (1.0 / (1 << 20));

    private static double LengthSquared(Vector3 value) => (((((double)value.X) * value.X) + (((double)value.Y) * value.Y)) + (((double)value.Z) * value.Z));
    private static double LengthSquared(Quaternion value) => (((((double)value.X) * value.X) + (((double)value.Y) * value.Y)) + ((((double)value.Z) * value.Z) + (((double)value.W) * value.W)));
    // A non-finite/zero-length direction has no fold plane to normalize to, so it floors to the retired Mirror:
    // true flag's exact plane (UnitX) rather than reaching SdfProgramBuilder.SymmetryPlane's own throwing guard.
    private static Vector3 NormalizeDirection(Vector3 value) {
        var lengthSquared = LengthSquared(value: value);

        if (!double.IsFinite(d: lengthSquared) || (lengthSquared == 0.0)) {
            return Vector3.UnitX;
        }

        if (Math.Abs(value: (lengthSquared - 1.0)) <= UnitBand) {
            return value;
        }

        var length = Math.Sqrt(d: lengthSquared);

        return new Vector3(
            x: ((float)(value.X / length)),
            y: ((float)(value.Y / length)),
            z: ((float)(value.Z / length))
        );
    }
    // A bound rotation keeps its reference: normalizing it would store the cell's value of the moment as a literal
    // and drop the binding from every later canonical write-back. A zero or non-finite literal has no unit rotation and
    // is kept as written. Written as statements: a conditional whose arms are the holder and a Quaternion takes
    // Quaternion as its natural type and re-wraps a literal.
    private static DocumentQuaternion NormalizeRotation(DocumentQuaternion? rotation) {
        if (rotation is null) {
            return Quaternion.Identity;
        }

        if (rotation.Reference is not null) {
            return rotation;
        }

        var value = rotation.Value;
        var lengthSquared = LengthSquared(value: value);

        if (!double.IsFinite(d: lengthSquared) || (lengthSquared == 0.0) || (Math.Abs(value: (lengthSquared - 1.0)) <= UnitBand)) {
            return rotation;
        }

        var length = Math.Sqrt(d: lengthSquared);

        return new Quaternion(
            w: ((float)(value.W / length)),
            x: ((float)(value.X / length)),
            y: ((float)(value.Y / length)),
            z: ((float)(value.Z / length))
        );
    }
}
