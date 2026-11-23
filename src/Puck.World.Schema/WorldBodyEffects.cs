using System.Globalization;
using System.Numerics;
using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>The scope a body-motion effect is authored in.</summary>
public enum WorldBodyEffectScope : byte {
    /// <summary>A kit's action program: the effect acts on the body whose trigger fired, or on the participant its
    /// <see cref="ActionTarget"/> picks.</summary>
    Kit,

    /// <summary>A world rule: the rule has no body of its own, so the effect names one with <c>key</c>.</summary>
    Rule,
}
/// <summary>One body-motion effect lowered to the operands both scopes run: the kit's compiled instruction and the
/// rule's compiled body action are each built from this, never from the authored record directly.</summary>
/// <param name="Verb">The effect's <c>$type</c> discriminator, for refusal and trace text.</param>
/// <param name="Operation">The motion operation.</param>
/// <param name="Value">The operation's velocity, factor, speed, or relative yaw in radians.</param>
/// <param name="Direction">The planar impulse's unit direction or the timed move's body-frame displacement.</param>
/// <param name="DurationTicks">The duration of a planar impulse or timed move, in engine ticks.</param>
/// <param name="Target">The participant a kit's action addresses; always <see cref="ActionTarget.Self"/> in a rule.</param>
/// <param name="Register">The target register a designation writes, or <see langword="null"/>.</param>
/// <param name="Designation">Whether a designation fills or clears its register.</param>
/// <param name="Key">The body a rule names, or <see langword="null"/> in a kit.</param>
/// <param name="TargetKey">The body a rule designates, or <see langword="null"/>.</param>
/// <param name="UseMoveDirection">Whether a planar impulse prefers the normalized current movement command.</param>
/// <param name="Curve">The timed move's progress curve.</param>
/// <param name="Placement">The placement a point designation is based at, or <see langword="null"/> for the
/// origin.</param>
/// <param name="Offset">The literal point of a point designation, or its offset from <paramref name="Placement"/>;
/// zero when the point is computed.</param>
public readonly record struct WorldBodyEffectForm(
    string Verb,
    BodyMotionOp Operation,
    FixedQ4816 Value,
    FixedVector3 Direction,
    ulong DurationTicks,
    ActionTarget Target,
    string? Register,
    WorldBodyDesignationKind Designation,
    StateChannelRef? Key,
    StateChannelRef? TargetKey,
    bool UseMoveDirection = false,
    BodyMotionCurve Curve = BodyMotionCurve.Linear,
    string? Placement = null,
    FixedVector3 Offset = default
);
/// <summary>The one check and lowering of the body-motion effects — <see cref="WorldEffect.SetVerticalVelocity"/>,
/// <see cref="WorldEffect.ScaleVerticalVelocity"/>, <see cref="WorldEffect.PlanarImpulse"/> and
/// <see cref="WorldEffect.Designate"/> and <see cref="WorldEffect.TimedMove"/> — that a kit's action and a world rule both author. The document validator
/// refuses a kit's effect through it, the kit compiler lowers through it, and the rule compiler refuses and lowers a
/// rule's effect through it, so an effect admitted in one scope means the same thing in the other.
/// <para>A kit's effect names no body: it acts on its own, or on the participant its <c>target</c> picks. A rule's
/// effect names one with <c>key</c> and addresses no <c>target</c>. Every number is exact: a value outside Q48.16, a
/// duration that is not a whole number of engine ticks, or a planar direction that is not unit length is refused by
/// name, never rounded.</para></summary>
public static class WorldBodyEffects {
    /// <summary>The tolerance on a planar impulse direction's length: the runtime rides the direction as authored,
    /// never normalized, so a longer or shorter one would silently rescale the speed. The direction quantizes to
    /// Q48.16 (step 2^-16) before it reaches the simulation, which moves a unit vector's length by at most ~1.3e-5; the
    /// tolerance sits well above that floor and far below any unnormalized axis ((3, 0, 4) is off by 4).</summary>
    public const float UnitDirectionTolerance = 1e-4f;

    /// <summary>Returns whether <paramref name="effect"/> is a body-motion effect.</summary>
    /// <param name="effect">The authored effect.</param>
    /// <returns><see langword="true"/> for a body-motion effect.</returns>
    public static bool IsBodyMotion(ActionEffect? effect) => (effect is WorldEffect.SetVerticalVelocity or WorldEffect.ScaleVerticalVelocity or WorldEffect.PlanarImpulse or WorldEffect.TimedMove or WorldEffect.Designate);
    /// <summary>Checks one body-motion effect against its scope and lowers it.</summary>
    /// <param name="effect">The authored effect, as admitted by <see cref="IsBodyMotion"/>.</param>
    /// <param name="scope">The scope it is authored in.</param>
    /// <param name="registerDeclared">Answers whether a target-register name is declared, or <see langword="null"/>
    /// when the caller holds no register table because the document validator already checked it.</param>
    /// <param name="form">The lowered operands, on success.</param>
    /// <param name="refusal">The rule refusal the failure maps to, on failure.</param>
    /// <param name="reason">The refusal sentence, naming the effect and the member at fault, or empty on success.</param>
    /// <returns><see langword="true"/> when the effect is admissible in <paramref name="scope"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="effect"/> is not a body-motion effect.</exception>
    public static bool TryLower(ActionEffect effect, WorldBodyEffectScope scope, Predicate<string>? registerDeclared, out WorldBodyEffectForm form, out RuleRefusal refusal, out string reason) {
        form = default;
        refusal = RuleRefusal.EffectKindInadmissible;

        var (verb, key, target) = effect switch {
            WorldEffect.SetVerticalVelocity set => ("setVerticalVelocity", set.Key, set.Target),
            WorldEffect.ScaleVerticalVelocity scale => ("scaleVerticalVelocity", scale.Key, scale.Target),
            WorldEffect.PlanarImpulse impulse => ("planarImpulse", impulse.Key, impulse.Target),
            WorldEffect.TimedMove move => ("timedMove", move.Key, ActionTarget.Self),
            WorldEffect.Designate designate => ("designate", designate.Key, ActionTarget.AffectingSubject),
            _ => throw new ArgumentException(
                message: $"'{effect?.GetType().Name}' is not a body-motion effect",
                paramName: nameof(effect)
            ),
        };

        if (!TryScope(
            key: key,
            reason: out reason,
            refusal: ref refusal,
            scope: scope,
            target: ((effect is WorldEffect.Designate) ? null : target),
            verb: verb
        )) {
            return false;
        }

        var value = FixedQ4816.Zero;
        var direction = default(FixedVector3);
        var durationTicks = 0UL;
        var curve = BodyMotionCurve.Linear;
        string? register = null;
        var designation = WorldBodyDesignationKind.Body;
        string? placement = null;
        var offset = default(FixedVector3);
        StateChannelRef? targetKey = null;
        BodyMotionOp operation;

        switch (effect) {
            case WorldEffect.SetVerticalVelocity set:
                operation = BodyMotionOp.SetVerticalVelocity;

                if (!TryFixed(value: set.Velocity, field: "velocity", verb: verb, result: out value, reason: out reason)) {
                    return false;
                }

                break;
            case WorldEffect.ScaleVerticalVelocity scale:
                operation = BodyMotionOp.ScaleVerticalVelocity;

                if (!TryFixed(value: scale.Factor, field: "factor", verb: verb, result: out value, reason: out reason)) {
                    return false;
                }

                break;
            case WorldEffect.PlanarImpulse impulse:
                operation = BodyMotionOp.PlanarImpulse;

                var lengthSquared = impulse.BodyDirection.LengthSquared();

                if (
                    !float.IsFinite(f: lengthSquared) ||
                    (lengthSquared <= 0f) ||
                    (MathF.Abs(x: (MathF.Sqrt(x: lengthSquared) - 1f)) > UnitDirectionTolerance)
                ) {
                    reason = $"'{verb}' bodyDirection {impulse.BodyDirection} has magnitude {MathF.Sqrt(x: lengthSquared).ToString(provider: CultureInfo.InvariantCulture)}, not 1 — the runtime rides bodyDirection as authored, never normalized, so a direction that is not finite and unit length would silently rescale speed";

                    return false;
                }

                direction = FixedVector3.FromVector3(value: impulse.BodyDirection);

                if (!TryFixed(value: impulse.Speed, field: "speed", verb: verb, result: out value, reason: out reason)) {
                    return false;
                }
                if (!TryDuration(impulse.DurationSeconds, verb, allowZero: true, out durationTicks, ref refusal, out reason)) {
                    return false;
                }

                break;
            case WorldEffect.TimedMove move:
                operation = BodyMotionOp.TimedMove;
                curve = move.Curve;
                if (!Enum.IsDefined(curve)) {
                    reason = $"'{verb}' curve '{curve}' is not defined";
                    return false;
                }
                if (!TryFixed(move.Right, "right", verb, out var right, out reason)
                    || !TryFixed(-move.Forward, "forward", verb, out var back, out reason)
                    || !TryFixed(move.YawDegrees, "yawDegrees", verb, out var yawDegrees, out reason)
                    || !TryDuration(move.DurationSeconds, verb, allowZero: false, out durationTicks, ref refusal, out reason)) {
                    return false;
                }

                direction = new FixedVector3(X: right, Y: FixedQ4816.Zero, Z: back);
                value = FixedQ4816.FromDegrees(yawDegrees);
                break;
            default:
                var designate = ((WorldEffect.Designate)effect);

                operation = BodyMotionOp.Designate;
                register = designate.Register;
                designation = designate.Kind;
                targetKey = designate.TargetKey;

                if (!TryDesignation(
                    designate: designate,
                    offset: out offset,
                    reason: out reason,
                    registerDeclared: registerDeclared,
                    scope: scope,
                    verb: verb
                )) {
                    return false;
                }

                placement = designate.Placement;

                break;
        }

        form = new WorldBodyEffectForm(
            Curve: curve,
            Designation: designation,
            Direction: direction,
            UseMoveDirection: (effect is WorldEffect.PlanarImpulse { UseMoveDirection: true }),
            DurationTicks: durationTicks,
            Key: key,
            Offset: offset,
            Operation: operation,
            Placement: placement,
            Register: register,
            Target: target,
            TargetKey: targetKey,
            Value: value,
            Verb: verb
        );
        reason = string.Empty;

        return true;
    }

    private static bool TryDuration(decimal seconds, string verb, bool allowZero, out ulong ticks, ref RuleRefusal refusal, out string reason) {
        if (FixedTickConversion.TryDurationEngineTicksExact(seconds: seconds, ticks: out ticks) && (allowZero || (ticks != 0))) {
            reason = string.Empty;
            return true;
        }

        refusal = RuleRefusal.DurationNotExactEngineTicks;
        var range = (allowZero ? "non-negative" : "positive");
        reason = $"'{verb}' durationSeconds {seconds.ToString(provider: CultureInfo.InvariantCulture)} is not a {range} exact whole-engine-tick duration";
        return false;
    }

    // Who the effect acts on. A kit's action names no body — its own is the one whose trigger fired — and a rule, which
    // has none, must name one and has no participant a target could pick.
    private static bool TryScope(string verb, StateChannelRef? key, ActionTarget? target, WorldBodyEffectScope scope, ref RuleRefusal refusal, out string reason) {
        reason = string.Empty;

        if (scope == WorldBodyEffectScope.Kit) {
            if (key is not null) {
                reason = $"'{verb}' names body '{key}' with 'key' — a kit's action acts on its own body, or on the participant 'target' picks; 'key' names the body a world rule acts on";

                return false;
            }
            if (
                (target is { } picked) &&
                !Enum.IsDefined(value: picked)
            ) {
                reason = $"'{verb}' target '{picked}' is not a defined ActionTarget";

                return false;
            }

            return true;
        }

        if (key is null) {
            reason = $"'{verb}' names no body — a world rule has no body of its own, so it names the body it acts on with 'key'";

            return false;
        }
        if (target is { } addressed and not ActionTarget.Self) {
            refusal = RuleRefusal.TargetInadmissible;
            reason = $"'{verb}' carries target '{addressed}' — a rule has no entity to address, so it names the body with 'key' instead";

            return false;
        }

        return true;
    }
    private static bool TryDesignation(WorldEffect.Designate designate, string verb, WorldBodyEffectScope scope, Predicate<string>? registerDeclared, out FixedVector3 offset, out string reason) {
        offset = default;
        reason = string.Empty;

        var hasPoint = (
            (designate.Placement is not null) ||
            (designate.Position is not null) ||
            (designate.PointX is not null) ||
            (designate.PointY is not null) ||
            (designate.PointZ is not null)
        );
        var computed = (
            (designate.PointX is not null) ||
            (designate.PointY is not null) ||
            (designate.PointZ is not null)
        );

        if (string.IsNullOrEmpty(value: designate.Register)) {
            reason = $"'{verb}' names no register";
        } else if (
            (registerDeclared is not null) &&
            !registerDeclared(obj: designate.Register)
        ) {
            reason = $"'{verb}' names undeclared register '{designate.Register}'";
        } else if (!Enum.IsDefined(value: designate.Kind)) {
            reason = $"'{verb}' kind '{designate.Kind}' is not defined";
        } else if (scope == WorldBodyEffectScope.Kit) {
            if (designate.Kind != WorldBodyDesignationKind.Body) {
                reason = $"'{verb}' kind={designate.Kind.ToString().ToLowerInvariant()} is a world rule's — a kit's action designates into its own register the participant that last affected it";
            } else if (designate.TargetKey is not null) {
                reason = $"'{verb}' names targetKey '{designate.TargetKey}' — a kit's action designates the participant that last affected its body; 'targetKey' names the body a world rule designates";
            } else if (hasPoint) {
                reason = $"'{verb}' names a point — a kit's action designates the participant that last affected its body; only a world rule designates a point";
            }
        } else if (designate.Kind == WorldBodyDesignationKind.Body) {
            if (designate.TargetKey is null) {
                reason = $"'{verb}' kind=body requires targetKey";
            } else if (hasPoint) {
                reason = $"'{verb}' kind=body does not admit a point; kind=point designates one";
            }
        } else if (designate.Kind == WorldBodyDesignationKind.Clear) {
            if (designate.TargetKey is not null) {
                reason = $"'{verb}' kind=clear does not admit targetKey";
            } else if (hasPoint) {
                reason = $"'{verb}' kind=clear does not admit a point";
            }
        } else if (designate.TargetKey is not null) {
            reason = $"'{verb}' kind=point does not admit targetKey";
        } else if (!hasPoint) {
            reason = $"'{verb}' kind=point requires a point: placement, position, or pointX, pointY and pointZ";
        } else if (
            computed &&
            ((designate.PointX is null) || (designate.PointY is null) || (designate.PointZ is null))
        ) {
            reason = $"'{verb}' computes its point with pointX, pointY and pointZ together; one or two of them name no point";
        } else if (computed && (designate.Position is not null)) {
            reason = $"'{verb}' names both position and pointX, pointY and pointZ — authors exactly one";
        } else if (
            (designate.Placement is { } placement) &&
            string.IsNullOrWhiteSpace(value: placement)
        ) {
            reason = $"'{verb}' names an empty placement";
        } else if (designate.Position is { } position) {
            var literal = ((Vector3)position);

            if (!TryFixedAxis(value: literal.X, field: "position x", verb: verb, result: out var x, reason: out reason) ||
                !TryFixedAxis(value: literal.Y, field: "position y", verb: verb, result: out var y, reason: out reason) ||
                !TryFixedAxis(value: literal.Z, field: "position z", verb: verb, result: out var z, reason: out reason)) {
                return false;
            }

            offset = new FixedVector3(
                X: x,
                Y: y,
                Z: z
            );
        }

        return (reason.Length == 0);
    }
    // An authored literal is a float: one that is not finite is refused by name, and the rest take TryFixed's exact range.
    private static bool TryFixedAxis(float value, string field, string verb, out FixedQ4816 result, out string reason) {
        result = FixedQ4816.Zero;
        if (!float.IsFinite(f: value)) {
            reason = $"'{verb}' {field} '{value.ToString(provider: CultureInfo.InvariantCulture)}' is not finite";

            return false;
        }

        return TryFixed(
            value: decimal.CreateSaturating(value: value),
            field: field,
            verb: verb,
            result: out result,
            reason: out reason
        );
    }
    private static bool TryFixed(decimal value, string field, string verb, out FixedQ4816 result, out string reason) {
        if (NumericLiteral.TryToFixed(
            result: out result,
            value: value
        )) {
            reason = string.Empty;

            return true;
        }

        reason = $"'{verb}' {field} '{value.ToString(provider: CultureInfo.InvariantCulture)}' is outside the Q48.16 range";

        return false;
    }
}
