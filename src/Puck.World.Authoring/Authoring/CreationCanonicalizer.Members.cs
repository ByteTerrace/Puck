using System.Numerics;
using Puck.Assets.Documents;

namespace Puck.World.Authoring;

public static partial class CreationCanonicalizer {
    // A document that omits a member every entry of its kind must author deserializes with the member null, and every
    // later check reads its value, so the omission is named here rather than left to throw inside the first read.
    private static bool ReportsAbsentMembers(List<DocumentValidationError> errors, (string Name, object? Value)[] members, string noun, string path, string subject) {
        var absent = false;

        foreach (var (name, value) in members) {
            if (value is null) {
                absent = true;
                errors.Add(item: new(
                    Message: $"{subject} omits {name}, which every {noun} must author.",
                    Path: $"{path}.{name}"
                ));
            }
        }

        return absent;
    }
    private static bool IsFinite(Quaternion quaternion) =>
        (float.IsFinite(f: quaternion.X) && float.IsFinite(f: quaternion.Y) && float.IsFinite(f: quaternion.Z) && float.IsFinite(f: quaternion.W));
    // A zero quaternion is finite but carries no rotation to normalize into: Quaternion.Normalize divides by its
    // zero magnitude and returns (NaN, NaN, NaN, NaN), so the emitter that trusts a validated rotation reads NaN
    // silently unless this is refused here, before normalization ever runs.
    private static bool IsZero(Quaternion quaternion) =>
        ((quaternion.X == 0f) && (quaternion.Y == 0f) && (quaternion.Z == 0f) && (quaternion.W == 0f));
    // Shared by every authored rotation a shape, text run, or animation frame transform carries.
    private static void ValidateRotation(Quaternion rotation, string path, List<DocumentValidationError> errors) {
        if (!IsFinite(quaternion: rotation)) {
            errors.Add(item: new(
                Message: "rotation is non-finite.",
                Path: path
            ));
        } else if (IsZero(quaternion: rotation)) {
            errors.Add(item: new(
                Message: "rotation is a zero quaternion; a zero rotation isn't a rotation.",
                Path: path
            ));
        }
    }
}
