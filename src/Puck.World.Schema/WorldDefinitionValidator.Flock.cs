using System.Numerics;
using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Motion;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateFlockProfile(WorldFlockProfile? flock, CompiledBodyMotionProgram program, string path, List<string> errors) {
        if (program.Contains(operation: BodyMotionOp.ProduceFlockIntent) != (flock is not null)) {
            errors.Add(item: $"{path}.flock is required exactly when ProduceFlockIntent is selected.");
        }
        if (flock is null) { return; }
        path += ".flock";
        // This numerical envelope keeps all downstream sensing norms and world-field queries representable.
        RequireRange(
            flock.Range,
            (1f / 65536f),
            1_000_000f,
            $"{path}.range",
            errors
        );
        RequireRange(
            flock.SeparationRadius,
            0f,
            flock.Range,
            $"{path}.separationRadius",
            errors
        );
        RequireRange(
            flock.ArrivalDistance,
            0f,
            flock.Range,
            $"{path}.arrivalDistance",
            errors
        );
        RequireRange(
            flock.UpdateSeconds,
            0f,
            60f,
            $"{path}.updateSeconds",
            errors
        );
        RequireRange(
            flock.HalfAngleDegrees,
            0f,
            180f,
            $"{path}.halfAngleDegrees",
            errors,
            minExclusive: true
        );
        RequireRange(
            flock.Separation,
            0f,
            1f,
            $"{path}.separation",
            errors
        );
        RequireRange(
            flock.Alignment,
            0f,
            1f,
            $"{path}.alignment",
            errors
        );
        RequireRange(
            flock.Cohesion,
            0f,
            1f,
            $"{path}.cohesion",
            errors
        );
        RequireRange(
            flock.Goal,
            0f,
            1f,
            $"{path}.goal",
            errors
        );
        RequireRange(
            flock.Inertia,
            0f,
            1f,
            $"{path}.inertia",
            errors
        );
        RequireRange(
            flock.CandidateBudget,
            1,
            WorldBodiesLimits.CapacityCeiling,
            $"{path}.candidateBudget",
            errors
        );
        RequireRange(
            flock.MaxNeighbors,
            1,
            flock.CandidateBudget,
            $"{path}.maxNeighbors",
            errors
        );
        if (!Enum.IsDefined(value: flock.Space)) { errors.Add(item: $"{path}.space is not defined."); }
    }
    private static void ValidateFlockMotion(WorldDefinition definition, WorldKit kit, CompiledBodyMotionProgram? motion, string path, List<string> errors) {
        foreach (var (name, parameters) in kit.Producers) {
            if (
                (parameters?.Flock is not { } flock) ||
                (motion is null)
            ) { continue; }
            ValidateFlockAffinity(
                flock.CohesionAffinity,
                definition,
                $"{path}.producers[{name}].flock.cohesionAffinity",
                errors
            );
            ValidateFlockAffinity(
                flock.AlignmentAffinity,
                definition,
                $"{path}.producers[{name}].flock.alignmentAffinity",
                errors
            );
            if (flock.MovementDomain is { } domainName) {
                var domain = definition.Navigation.Rows.FirstOrDefault(predicate: row => string.Equals(
                    a: row.Name,
                    b: domainName,
                    comparisonType: StringComparison.Ordinal
                ));

                if (
                    (domain is null) ||
                    (domain.Kind == WorldNavigationKind.Surface) ||
                    (flock.Space != WorldFlockSpace.Volume)
                ) {
                    errors.Add(item: $"{path}.producers[{name}].flock.movementDomain requires a declared volume/medium domain and Volume flock space.");
                } else if (
                    (errors.Count == 0) &&
                    !FlockColliderFitsDomain(
                    definition: definition,
                    domain: domain,
                    kit: kit
                )
                ) {
                    errors.Add(item: $"{path}.producers[{name}].flock.movementDomain '{domainName}' agentRadius must enclose every collider volume about the body root, including its local offsets.");
                }
            }
            if (
                !motion.RequiresRole(role: ChannelRole.MoveAdvance) ||
                !motion.RequiresRole(role: ChannelRole.MoveStrafe)
            ) {
                errors.Add(item: $"{path}.producers[{name}].flock requires motion consuming both MoveAdvance and MoveStrafe.");
            }
            if (
                (flock.Space == WorldFlockSpace.Volume) &&
                (!definition.Channels.Any(predicate: channel => (channel.Role == ChannelRole.MoveUp)) ||
                 !(motion.Contains(operation: BodyMotionOp.ComputeLocalTargetVelocity) ||
                   (motion.Contains(operation: BodyMotionOp.ApplyHold) && (kit.Motion?.Holds ?? []).Any(predicate: hold => ((hold.Bond == BodyHoldBond.Medium) || (hold.Thrust > 0f))))))
            ) {
                errors.Add(item: $"{path}.producers[{name}].flock volume motion requires a MoveUp channel and a compatible vertical consumer.");
            }
        }
    }
    private static void ValidateFlockAffinity(ValueExpression? expression, WorldDefinition definition, string path, List<string> errors) {
        if (expression is null) { return; }
        try { _ = WorldRuleCompiler.CompileFlockAffinity(
            definition: definition,
            expression: expression
        ); } catch (RuleException exception) { errors.Add(item: $"{path}: {exception.Message}"); }
    }
    private static bool FlockColliderFitsDomain(WorldKit kit, WorldDefinition definition, WorldNavigationDomain domain) {
        try {
            if (FixedWorldCollider.Compile(
                collider: kit.Collider,
                creations: definition.Creations
            ) is not { } collider) { return true; }
            var radius = ((BigInteger)FixedQ4816.FromDouble(value: domain.AgentRadius).Value);

            static BigInteger Squared(FixedVector3 value) => (((((BigInteger)value.X.Value) * value.X.Value) +
                (((BigInteger)value.Y.Value) * value.Y.Value)) + (((BigInteger)value.Z.Value) * value.Z.Value));
            foreach (var volume in collider.Volumes) {
                var centerSquared = Squared(value: volume.Center);

                if (volume.Kind == FixedBodyColliderKind.Box) {
                    // A rotation-independent sphere around the box center, then around the body root.
                    // R >= sqrt(C) + sqrt(E), compared exactly without narrowing or irrational rounding.
                    var extentSquared = Squared(value: volume.HalfExtents);
                    var remainder = (((radius * radius) - centerSquared) - extentSquared);

                    if (
                        (remainder < 0) ||
                        ((remainder * remainder) < ((4 * centerSquared) * extentSquared))
                    ) { return false; }
                } else {
                    var remainder = (radius - volume.Radius.Value);

                    if (
                        (remainder < 0) ||
                        ((remainder * remainder) < centerSquared) ||
                        ((volume.Kind == FixedBodyColliderKind.Capsule) && ((remainder * remainder) < Squared(value: volume.Endpoint)))
                    ) { return false; }
                }
            }
            return true;
        } catch (Exception exception) when ((exception is ArgumentException or OverflowException or InvalidOperationException)) {
            // Malformed/unrepresentable collider inputs are refused at admission, never allowed to make the
            // conservative navigation proxy smaller than the actual body. The collider gate adds its own detail.
            return false;
        }
    }
}
