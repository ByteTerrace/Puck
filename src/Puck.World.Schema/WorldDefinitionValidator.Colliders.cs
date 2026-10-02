using Puck.Maths;
using Puck.Physics;
using Puck.SignedDistance;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateCollider(WorldCollider? collider, IReadOnlyList<WorldPrototype> creations, string path, List<string> errors) {
        if (collider is null) {
            return;
        }

        switch (collider) {
            case WorldCollider.Sphere sphere:
                RequirePositive(
                    value: sphere.Radius,
                    name: $"{path}.radius",
                    errors: errors
                );
                break;
            case WorldCollider.Capsule capsule:
                RequirePositive(
                    value: capsule.Radius,
                    name: $"{path}.radius",
                    errors: errors
                );

                if (
                    !VectorFunctions.IsFinite(vector: capsule.Endpoint) ||
                    (capsule.Endpoint.LengthSquared() <= 0f)
                ) {
                    errors.Add(item: $"{path}.endpoint must be finite and nonzero; use a sphere for a zero-length capsule.");
                } else if (
                    float.IsFinite(f: capsule.Radius) &&
                    (capsule.Radius > 0f) &&
                    !FixedFieldContactSolver.CapsuleCoreFitsSweep(
                        core: FixedVector3.FromVector3(value: capsule.Endpoint),
                        pieces: out _,
                        radius: FixedQ4816.FromDouble(value: capsule.Radius)
                    )
                ) {
                    // A moving body's certified sweep covers a capsule's core with one sphere per piece, and caps the
                    // pieces at the bounds queries one core may spend; a longer core in radii would make every step of
                    // its body refused.
                    errors.Add(item: $"{path} is a capsule whose core, {capsule.Endpoint.Length()} long at radius {capsule.Radius}, needs more than the {FixedFieldContactSolver.MaximumCapsuleSweepPieces} sweep pieces a moving body's certified sweep covers; shorten the endpoint or widen the radius.");
                }
                break;
            case WorldCollider.Box box:
                if (
                    !VectorFunctions.IsFinite(vector: box.HalfExtents) ||
                    (box.HalfExtents.X <= 0f) ||
                    (box.HalfExtents.Y <= 0f) ||
                    (box.HalfExtents.Z <= 0f)
                ) {
                    errors.Add(item: $"{path}.halfExtents must contain finite positive coordinates.");
                }

                var rotationLength = box.Rotation.LengthSquared();
                if (
                    !float.IsFinite(f: rotationLength) ||
                    (rotationLength <= 0f)
                ) {
                    errors.Add(item: $"{path}.rotation must be finite and nonzero.");
                }
                break;
            case WorldCollider.FromCreation fromCreation:
                if (
                    string.IsNullOrWhiteSpace(value: fromCreation.PrototypeId) ||
                    (WorldDefinitionRows.FindCreation(
                    creations: creations,
                    id: fromCreation.PrototypeId
                ) is not { } creation)
                ) {
                    errors.Add(item: $"{path}.prototypeId '{fromCreation.PrototypeId}' names no creation row.");
                    break;
                }

                var shapes = (creation.Document.Shapes ?? []);
                if (shapes.Count < 1) {
                    errors.Add(item: $"{path} creation '{fromCreation.PrototypeId}' emits no body-collider volumes.");
                } else if (shapes.Count > WorldCollider.MaxVolumes) {
                    errors.Add(item: $"{path} creation '{fromCreation.PrototypeId}' emits {shapes.Count} volumes, exceeding the {WorldCollider.MaxVolumes}-volume body-collider ceiling.");
                }

                for (var index = 0; (index < shapes.Count); index++) {
                    if (shapes[index].Type == SdfSolidPrimitive.Plane) {
                        errors.Add(item: $"{path} creation '{fromCreation.PrototypeId}' shape {index} is an unbounded plane, not a finite body volume.");
                    }

                    // The body collider reads each copy's per-axis-scaled local box; a revolve reads scale.z as a
                    // radial offset, so that box would sit inside the solid a body must stop at (the same refusal a
                    // solid placement row carries).
                    if (shapes[index].Lift is SdfLift.Revolve) {
                        errors.Add(item: $"{path} creation '{fromCreation.PrototypeId}' shape {index} lifts by revolve; a revolve reads scale.z as a radial offset, which the body collider's per-axis local box does not describe. Use the extrude lift on a body-collider creation.");
                    }
                }
                break;
            default:
                errors.Add(item: $"{path} has an unknown collider kind.");
                break;
        }

    }
}
