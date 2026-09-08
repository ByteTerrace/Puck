using System.Text.Json.Serialization;
using Puck.Assets.Documents;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>The predicate arms the world adds to <see cref="ActionPredicate"/>: body facts and per-body action
/// state a kit's action programs gate on. <see cref="WorldRuleVocabulary"/> registers them under their
/// <c>$type</c> discriminators; none has a world-rule meaning, so a rule gate spelling one refuses by name.</summary>
public static class WorldPredicate {
    /// <summary>The fact holds this tick.</summary>
    /// <param name="Fact">The body fact.</param>
    public sealed record Now(ActionFact Fact) : ActionPredicate;
    /// <summary>The fact held within the last <paramref name="WindowSeconds"/> — a per-instance recency clock,
    /// refreshed while the fact holds and decaying otherwise (coyote time is <c>Recently(Grounded, w)</c>).</summary>
    /// <param name="Fact">The body fact.</param>
    /// <param name="WindowSeconds">The recency window.</param>
    public sealed record Recently(ActionFact Fact, float WindowSeconds) : ActionPredicate;
    /// <summary>Whether a named timer slot has drained.</summary>
    /// <param name="State">The timer slot.</param>
    public sealed record TimerElapsed(string State) : ActionPredicate;
    /// <summary>The named composition channel's own live read is at or above its declared threshold. Legitimate
    /// only inside a kit's <c>shaping</c>-row gate, where the world's channel table resolves
    /// <paramref name="Channel"/> to an ordinal at kit-compile time.</summary>
    /// <param name="Channel">The declared composition channel name.</param>
    public sealed record Held(string Channel) : ActionPredicate;
}

/// <summary>The effect arms the world adds to <see cref="ActionEffect"/>, registered by
/// <see cref="WorldRuleVocabulary"/>. The body-program arms (vertical velocity, planar impulse, timers, designation
/// by <see cref="ActionTarget"/>) belong to a kit's action programs and refuse in a world rule; the body-keyed,
/// document, cue, field, pose, and save arms belong to world rules and refuse in a kit.</summary>
public static class WorldEffect {
    /// <summary>Writes the body's vertical-velocity channel (the jump launch / the surge).</summary>
    public sealed record SetVerticalVelocity(float Velocity, ActionTarget Target = ActionTarget.Self) : ActionEffect;
    /// <summary>Multiplies the body's vertical velocity (the jump cut; gate on <see cref="ActionFact.Rising"/>).</summary>
    public sealed record ScaleVerticalVelocity(float Factor, ActionTarget Target = ActionTarget.Self) : ActionEffect;
    /// <summary>A timed planar velocity overlay (the dash): <paramref name="BodyDirection"/> is rotated by the body's
    /// attitude at fire time and ridden as authored, never normalized, at <paramref name="Speed"/> for
    /// <paramref name="DurationSeconds"/>.</summary>
    public sealed record PlanarImpulse(DocumentVector3 BodyDirection, float Speed, float DurationSeconds, ActionTarget Target = ActionTarget.Self) : ActionEffect;
    /// <summary>Starts a named timer slot with an authored duration.</summary>
    public sealed record StartTimer(string State, float Seconds, ActionTarget Target = ActionTarget.Self) : ActionEffect;
    /// <summary>Submits the selected subject into a named target register.</summary>
    /// <param name="Register">The authored target-register name.</param>
    /// <param name="Target">The subject source.</param>
    public sealed record Designate(string Register, ActionTarget Target = ActionTarget.AffectingSubject) : ActionEffect;
    /// <summary>Emits a deterministic presentation-neutral cue (<see cref="WorldGameplayCue"/>).</summary>
    /// <param name="Name">The cue name; see <see cref="WorldGameplayCue.IsValidName"/>.</param>
    /// <param name="Payload">An optional bounded payload.</param>
    /// <param name="Key">The body the cue is about — a body index, a <c>$cell:</c> indirection, or a bound key.</param>
    public sealed record EmitCue(
        string Name,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Payload = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null
    ) : ActionEffect;
    /// <summary>Writes a world-addressed body's vertical velocity.</summary>
    public sealed record SetBodyVerticalVelocity(string Key, decimal Velocity) : ActionEffect;
    /// <summary>Scales a world-addressed body's vertical velocity.</summary>
    public sealed record ScaleBodyVerticalVelocity(string Key, decimal Factor) : ActionEffect;
    /// <summary>Rides a unit <paramref name="BodyDirection"/> at <paramref name="Speed"/> on a world-addressed body
    /// for an exact whole-engine-tick duration.</summary>
    public sealed record ApplyBodyImpulse(string Key, DocumentVector3 BodyDirection, decimal Speed, decimal DurationSeconds) : ActionEffect;
    /// <summary>Applies the same instantaneous world-space rigid impulse <c>body.impulse</c> fires (Δv = impulse /
    /// mass, through the server's rigid-body solver — never a second impulse mechanism) to a
    /// <paramref name="Key"/>-addressed body, along <paramref name="HeadingKey"/>'s own body's forward facing,
    /// scaled by a live kind=Fixed state cell's magnitude. The cue gesture's one honest path: billiards charges
    /// <paramref name="MagnitudeState"/> by hold duration and fires this on release. Refused by name when the
    /// struck body resolves to no active body or carries no 'rigid' kit facet, the heading body resolves to no
    /// active body, or the resulting impulse is not representable or exceeds the world's declared rigid speed
    /// ceiling.</summary>
    /// <param name="Key">The struck body reference — <c>body:&lt;n&gt;</c>, <c>argmax:&lt;row&gt;</c>/
    /// <c>argmin:&lt;row&gt;</c>, or <c>placement:&lt;id&gt;</c> (see
    /// <see cref="WorldRuleCompileContext.BodyRefVocabulary"/>).</param>
    /// <param name="HeadingKey">The body reference whose forward facing supplies the impulse direction — the same
    /// reference grammar as <paramref name="Key"/>.</param>
    /// <param name="MagnitudeState">The declared kind=Fixed row naming the impulse's magnitude.</param>
    /// <param name="MagnitudeKey">The row's cell key, or <see langword="null"/> for an unkeyed row.</param>
    public sealed record ApplyRigidImpulse(
        string Key,
        string HeadingKey,
        string MagnitudeState,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MagnitudeKey = null
    ) : ActionEffect;
    /// <summary>Designates or clears a world-addressed body's target register.</summary>
    public sealed record DesignateBody(
        string Key,
        string Register,
        WorldBodyDesignationKind Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetKey = null
    ) : ActionEffect;
    /// <summary>Paints one lattice field cell, or the cube of <paramref name="Radius"/> around it.</summary>
    public sealed record PaintField(
        string Field,
        int X,
        int Y,
        int Z,
        decimal Value,
        WorldFieldWriteOp Operation = WorldFieldWriteOp.Set,
        int Radius = 0
    ) : ActionEffect;
    /// <summary>Upserts a HUD panel document row.</summary>
    public sealed record UpsertHudPanel(WorldHudPanel Panel) : ActionEffect;
    /// <summary>Removes a HUD panel document row by id.</summary>
    public sealed record RemoveHudPanel(string Id) : ActionEffect;
    /// <summary>Upserts a placement document row; inside a transaction it must sit in the closing suffix.</summary>
    public sealed record UpsertPlacement(WorldPlacement Placement) : ActionEffect;
    /// <summary>Removes a placement document row by id; inside a transaction it must sit in the closing suffix.</summary>
    public sealed record RemovePlacement(string Id) : ActionEffect;
    /// <summary>Saves the world through the host's save tap.</summary>
    public sealed record Save : ActionEffect;
    /// <summary>Writes one fact on the identity a world-addressed body drives under: the body's cell in the world's
    /// reserved <see cref="WorldIdentityFactLane"/> row and the identity's own persisted facts row, together. Exactly
    /// one of <paramref name="Value"/> and <paramref name="Expression"/> is authored; a body driving under no owned
    /// identity refuses the write rather than minting one.</summary>
    /// <param name="Key">The body — an index, a <c>$cell:</c> indirection, or a bound key.</param>
    /// <param name="Fact">The fact key on the identity's row.</param>
    /// <param name="Value">The integer literal.</param>
    /// <param name="Expression">A bounded integer expression evaluated at fire time.</param>
    public sealed record SetIdentityFact(
        string Key,
        string Fact,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Value = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ValueExpression? Expression = null
    ) : ActionEffect;
    /// <summary>Teleports a world-addressed body to a spawn point, or to a literal position with angles; exactly one
    /// of <paramref name="SpawnPoint"/> and <paramref name="Position"/> is authored.</summary>
    public sealed record Pose(
        string Key,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SpawnPoint = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Position = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float YawDegrees = 0f,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float PitchDegrees = 0f,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float RollDegrees = 0f
    ) : ActionEffect;
}

/// <summary>How a rule-triggered body designation chooses its target.</summary>
[JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<WorldBodyDesignationKind>))]
public enum WorldBodyDesignationKind : byte {
    /// <summary>Designate another active body.</summary>
    Body,
    /// <summary>Clear the register.</summary>
    Clear,
}

/// <summary>One deterministic presentation-neutral cue emitted by an authored rule.</summary>
/// <param name="Name">The cue's stable authored identifier.</param>
/// <param name="Payload">An optional bounded payload interpreted by the consumer.</param>
/// <param name="Body">An optional active-body index associated with the cue.</param>
/// <param name="Tick">The simulation tick that emitted it.</param>
public readonly record struct WorldGameplayCue(string Name, string? Payload, int? Body, ulong Tick) {
    /// <summary>Determines whether a cue name is a bounded dot-separated token suitable for a document and log.</summary>
    /// <param name="candidate">The candidate cue name.</param>
    /// <returns><see langword="true"/> when the name is non-empty, bounded, begins and ends with an ASCII letter or
    /// digit, and otherwise contains only ASCII letters, digits, dots, hyphens, or underscores.</returns>
    public static bool IsValidName(string? candidate) {
        if (
            (candidate is not { Length: > 0 }) ||
            (candidate.Length > WorldRuleCapacity.MaxCueNameLength) ||
            !char.IsAsciiLetterOrDigit(c: candidate[0]) ||
            !char.IsAsciiLetterOrDigit(c: candidate[^1])
        ) {
            return false;
        }

        foreach (var character in candidate) {
            if (
                !char.IsAsciiLetterOrDigit(c: character) &&
                (character != '.') &&
                (character != '-') &&
                (character != '_')
            ) {
                return false;
            }
        }

        return true;
    }
}

/// <summary>One trigger of a kit's action program: the effects that fire, the gate that must hold, and the latch
/// that keeps a press armed while the gate is closed.</summary>
/// <param name="Effects">The effects applied in order.</param>
/// <param name="Gate">The predicate that must hold, or <see langword="null"/> for always.</param>
/// <param name="LatchSeconds">How long a press stays armed waiting for the gate (the jump buffer).</param>
public sealed record ActionTrigger(IReadOnlyList<ActionEffect> Effects, ActionPredicate? Gate = null, float LatchSeconds = 0f);
/// <summary>One authored action: what fires on press, on release, and on a body fact's edge.</summary>
/// <param name="OnPress">The press trigger.</param>
/// <param name="OnRelease">The release trigger.</param>
/// <param name="OnFact">The fact-edge triggers.</param>
public sealed record ActionSpec(ActionTrigger? OnPress = null, ActionTrigger? OnRelease = null, IReadOnlyList<ActionFactTrigger>? OnFact = null);
/// <summary>A trigger that fires on a body fact, level or edge.</summary>
/// <param name="Fact">The body fact.</param>
/// <param name="Effects">The effects applied in order.</param>
/// <param name="Gate">The predicate that must hold, or <see langword="null"/> for always.</param>
/// <param name="Mode">Level or edge.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ActionFactTrigger(
    ActionFact Fact,
    IReadOnlyList<ActionEffect> Effects,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActionPredicate? Gate = null,
    ActionTriggerMode Mode = ActionTriggerMode.Level
);
