using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

public static partial class CreationCanonicalizer {
    // A convex profile lists its vertices from SdfPrismProfile.CanonicalStart: the same polygon from another start
    // vertex is one profile and one identity.
    private static SdfPrismProfile? NormalizeProfile(SdfPrismProfile? profile) => profile?.Canonicalize();
    private static void ValidatePathProfile(ShapeDocument shape, int index, List<DocumentValidationError> errors) {
        if (shape.Profile?.Kind != SdfPrismProfileKind.Path) { return; }
        if (((shape.Lift ?? SdfLift.Extrude) != SdfLift.Extrude) || ((shape.Rounding ?? 0f) != 0f) ||
            ((shape.Chamfer ?? 0f) != 0f) || (shape.Panel is not null) || (shape.Trims is { Count: > 0 })) {
            errors.Add(item: new(Message: "Path is an extrusion; rounding, chamfer, panel and trims are not supported.", Path: $"shapes[{index}].profile"));
        }
        if (shape.Profile.Path is { } path) {
            try { _ = path.Compile(); } catch (ArgumentException error) { errors.Add(item: new($"shapes[{index}].profile.path", error.Message)); }
        }
    }
}
