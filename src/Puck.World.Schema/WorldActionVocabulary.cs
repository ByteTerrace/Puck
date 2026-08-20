using Puck.Assets.Documents;
using System.Text.Json.Serialization;
using Puck.Physics.Motion;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>A data-composable gate over body facts and named action state. A trigger fires only while its gate holds.
/// The <c>$type</c> string is
/// the JSON discriminator, the same convention every polymorphic row family uses; a new predicate kind is a new
/// derived record plus its <see cref="JsonDerivedTypeAttribute"/> line.</summary>
[JsonDerivedType(typeof(ActionPredicate.Now), typeDiscriminator: "now")]
[JsonDerivedType(typeof(ActionPredicate.Recently), typeDiscriminator: "recently")]
[JsonDerivedType(typeof(ActionPredicate.CompareState), typeDiscriminator: "compareState")]
[JsonDerivedType(typeof(ActionPredicate.TimerElapsed), typeDiscriminator: "timerElapsed")]
[JsonDerivedType(typeof(ActionPredicate.All), typeDiscriminator: "all")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record ActionPredicate {
    /// <summary>The fact holds this tick.</summary>
    public sealed record Now(ActionFact Fact) : ActionPredicate;
    /// <summary>The fact held within the last <paramref name="WindowSeconds"/> — a per-instance recency clock,
    /// refreshed while the fact holds and decaying otherwise (coyote time is <c>Recently(Grounded, w)</c>).</summary>
    public sealed record Recently(ActionFact Fact, float WindowSeconds) : ActionPredicate;
    /// <summary>Compares a named state cell against either a fixed authored value, or — world scope only — another
    /// named state cell/reserved channel read live at the same evaluation. Both spellings are authorable; exactly one
    /// of <paramref name="Value"/> and <paramref name="ComparandState"/> may be present (refused by name when both or
    /// neither are). The comparand-row spelling is what lets a gate track a moving threshold — <c>$tick</c> compared
    /// against a schedule row the rule's own effects advance is "every N ticks"; a round row compared against a
    /// declared length row is a round boundary — composition over the same two-sided comparison, never a new
    /// mechanism.</summary>
    /// <param name="State">At body scope, a named counter slot the kit declares. At world scope (see
    /// <see cref="WorldRule"/>), a declared <c>state</c>-section row name, or one of
    /// <see cref="WorldRuleFacts"/>'s reserved channels.</param>
    /// <param name="Comparison">The comparison to apply.</param>
    /// <param name="Value">The authored constant comparand, or <see langword="null"/> when
    /// <paramref name="ComparandState"/> spells the comparand instead. Required (non-null) at body scope, where a
    /// comparand row reference is refused.</param>
    /// <param name="Key">At world scope, the cell inside <paramref name="State"/> to read —
    /// <see langword="null"/> reads the row's slot cell, which a keyed row does not have (refused by name rather
    /// than silently reading <c>cells[0]</c>). At body scope a non-null key is refused: a per-body action-state slot
    /// is not keyed, and a parsed-and-discarded field is worse than no field.</param>
    /// <param name="ComparandState">world scope only (refused at body scope, on the same terms as
    /// <paramref name="Key"/>): another declared <c>state</c>-section row name, or one of
    /// <see cref="WorldRuleFacts"/>'s reserved channels, read live and compared instead of <paramref name="Value"/>.
    /// A dotted spelling (an author reaching for <c>row.key</c> in one string) is refused by name — address the cell
    /// with <paramref name="ComparandKey"/> instead. Comparing across incompatible cell kinds (an <c>int</c> row
    /// against a <c>fixed</c> row, say) is refused by name — mixing scales silently is worse than naming the
    /// mismatch.</param>
    /// <param name="ComparandKey">The cell inside <paramref name="ComparandState"/>, on the same (row, key) terms as
    /// <paramref name="Key"/>. Refused when <paramref name="ComparandState"/> names a reserved channel or is absent.</param>
    public sealed record CompareState(
        string State,
        ActionStateComparison Comparison,
        float? Value = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ComparandState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ComparandKey = null
    ) : ActionPredicate;
    /// <summary>Whether a named timer slot has drained.</summary>
    public sealed record TimerElapsed(string State) : ActionPredicate;
    /// <summary>Every inner predicate holds (conjunction).</summary>
    public sealed record All(IReadOnlyList<ActionPredicate> Predicates) : ActionPredicate;
}
/// <summary>An authored operand row lowered to a <see cref="BodyMotionOp"/> and executed by the body instruction
/// interpreter when its trigger fires.</summary>
[JsonDerivedType(typeof(ActionEffect.SetVerticalVelocity), typeDiscriminator: "setVerticalVelocity")]
[JsonDerivedType(typeof(ActionEffect.ScaleVerticalVelocity), typeDiscriminator: "scaleVerticalVelocity")]
[JsonDerivedType(typeof(ActionEffect.PlanarImpulse), typeDiscriminator: "planarImpulse")]
[JsonDerivedType(typeof(ActionEffect.SetState), typeDiscriminator: "setState")]
[JsonDerivedType(typeof(ActionEffect.AddState), typeDiscriminator: "addState")]
[JsonDerivedType(typeof(ActionEffect.CountdownState), typeDiscriminator: "countdownState")]
[JsonDerivedType(typeof(ActionEffect.StartTimer), typeDiscriminator: "startTimer")]
[JsonDerivedType(typeof(ActionEffect.Designate), typeDiscriminator: "designate")]
[JsonDerivedType(typeof(ActionEffect.Generate), typeDiscriminator: "generate")]
[JsonDerivedType(typeof(ActionEffect.Judge), typeDiscriminator: "judge")]
[JsonDerivedType(typeof(ActionEffect.UpsertHudPanel), typeDiscriminator: "upsertHudPanel")]
[JsonDerivedType(typeof(ActionEffect.RemoveHudPanel), typeDiscriminator: "removeHudPanel")]
[JsonDerivedType(typeof(ActionEffect.UpsertPlacement), typeDiscriminator: "upsertPlacement")]
[JsonDerivedType(typeof(ActionEffect.RemovePlacement), typeDiscriminator: "removePlacement")]
[JsonDerivedType(typeof(ActionEffect.Save), typeDiscriminator: "save")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record ActionEffect {
    /// <summary>Writes the body's vertical-velocity channel (the jump launch / the surge). Under the grounded model
    /// gravity owns its decay; under the free model it bleeds to zero at the tuning's rise gravity (no fall phase).</summary>
    public sealed record SetVerticalVelocity(float Velocity, ActionTarget Target = ActionTarget.Self) : ActionEffect;
    /// <summary>Multiplies the body's vertical velocity (the jump cut; gate on <see cref="ActionFact.Rising"/>).</summary>
    public sealed record ScaleVerticalVelocity(float Factor, ActionTarget Target = ActionTarget.Self) : ActionEffect;
    /// <summary>A timed planar velocity overlay (the dash): <paramref name="BodyDirection"/> is rotated by the body's
    /// attitude at fire time and ridden at <paramref name="Speed"/> for <paramref name="DurationSeconds"/>, integrated
    /// through its own accumulator on top of the model's motion — integration itself is untouched.</summary>
    public sealed record PlanarImpulse(DocumentVector3 BodyDirection, float Speed, float DurationSeconds, ActionTarget Target = ActionTarget.Self) : ActionEffect;
    /// <summary>Writes a named state cell — a kit counter slot at body scope, a <c>state</c>-section row's cell at
    /// world scope (see <see cref="WorldRule"/>).</summary>
    /// <param name="State">The counter slot (body scope) or state row name (world scope).</param>
    /// <param name="Value">The literal value to write, or <see langword="null"/> when <paramref name="FromState"/>
    /// spells a live operand to copy instead — world scope only, exactly one of the two is authored (refused by name
    /// when both or neither are present, the same duality <see cref="ActionPredicate.CompareState"/>'s own comparand
    /// carries). Required (non-null) at body scope, where a live copy source is refused.</param>
    /// <param name="Target">The addressed entity — body scope only; a non-<see cref="ActionTarget.Self"/> target is
    /// refused at world scope, where there is no entity to select.</param>
    /// <param name="Key">The cell inside <paramref name="State"/> at world scope — <see langword="null"/> writes the
    /// row's slot cell, which a keyed row does not have (refused by name). Refused at body scope.</param>
    /// <param name="FromState">world scope only (refused at body scope, on the same terms as <paramref name="Value"/>):
    /// another declared <c>state</c>-section row name, or one of <see cref="WorldRuleFacts"/>'s reserved channels,
    /// read live at fire time and copied in place of an authored <paramref name="Value"/> — the row that resets to
    /// another row's own current value (a shadow row mirroring a counter someone else advances), never only a
    /// standing literal. Resolved through the same operand walk <see cref="ActionPredicate.CompareState"/>'s own
    /// <c>ComparandState</c> uses; mixing a <c>fixed</c> row into an <c>int</c> destination (or the reverse) is
    /// refused by name rather than coerced.</param>
    /// <param name="FromKey">The cell inside <paramref name="FromState"/>, on the same (row, key) terms as
    /// <paramref name="Key"/>. Refused when <paramref name="FromState"/> names a reserved channel or is absent.</param>
    /// <param name="ValueSeconds">world scope only (refused at body scope, on the same terms as <paramref name="Value"/>
    /// and <paramref name="FromState"/> — exactly one of the three is authored): an alternative to
    /// <paramref name="Value"/> for a <c>kind=int</c> state row a companion <see cref="CountdownState"/> effect
    /// decrements once per simulation tick (a countdown/cooldown). Authored in seconds — a physical unit, not a tick count,
    /// so a world's rate can change without silently retuning every cooldown — and converted once at rule compile
    /// time to an exact whole engine-tick count via <see cref="Puck.Maths.FixedTickConversion.TryDurationEngineTicksExact"/>,
    /// never re-derived at runtime and never rounded: a duration that is not an exact whole engine-tick count is
    /// refused rather than silently rounded away (<see cref="WorldRuleRefusal.DurationNotExactEngineTicks"/>). Typed
    /// <see cref="decimal"/> rather than <see langword="float"/> because JSON deserializes a number token to
    /// <see cref="decimal"/> exactly (base-10, no binary-float intermediate), and most terminating decimals — the
    /// only ones an author can spell — have no exact binary float or fixed-point spelling either. See
    /// <see cref="WorldRuleCompiler"/>.</param>
    public sealed record SetState(
        string State,
        float? Value = null,
        ActionTarget Target = ActionTarget.Self,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? ValueSeconds = null
    ) : ActionEffect;
    /// <summary>Adds to a named state cell — a kit counter slot at body scope, a <c>state</c>-section row's cell at
    /// world scope (see <see cref="WorldRule"/>).</summary>
    /// <param name="State">The counter slot (body scope) or state row name (world scope).</param>
    /// <param name="Value">The literal addend, or <see langword="null"/> when <paramref name="FromState"/> spells a
    /// live addend instead — see <see cref="SetState.Value"/>'s remarks; the same value/from duality, required
    /// (non-null) at body scope.</param>
    /// <param name="Target">The addressed entity — body scope only.</param>
    /// <param name="Key">The cell inside <paramref name="State"/> at world scope; refused at body scope.</param>
    /// <param name="FromState">world scope only — see <see cref="SetState.FromState"/>'s remarks; here the addend is
    /// read live rather than the replacement.</param>
    /// <param name="FromKey">The cell inside <paramref name="FromState"/> — see <see cref="SetState.FromKey"/>.</param>
    /// <param name="ValueSeconds">world scope only — see <see cref="SetState.ValueSeconds"/>'s remarks; here the
    /// converted tick count is the addend rather than the replacement.</param>
    public sealed record AddState(
        string State,
        float? Value = null,
        ActionTarget Target = ActionTarget.Self,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? ValueSeconds = null
    ) : ActionEffect;
    /// <summary>Decrements a world-state countdown by the current simulation step's engine-tick width, saturating at
    /// zero. world scope only: the destination must be a <c>kind=int nonNegative=true</c> row. Unlike an authored
    /// <see cref="AddState"/> constant, this effect consumes the runtime step width, so changing the world's authored
    /// tick rate never retunes the duration. When the remaining duration is shorter than one step, the computed
    /// decrement is exactly the remaining value; it reaches zero without asking the explicit-write door to admit a
    /// negative candidate.</summary>
    /// <param name="State">The countdown state-row name.</param>
    /// <param name="Key">The cell inside <paramref name="State"/>; <see langword="null"/> addresses its slot.</param>
    public sealed record CountdownState(
        string State,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null
    ) : ActionEffect;
    /// <summary>Starts a named timer slot with an authored duration.</summary>
    public sealed record StartTimer(string State, float Seconds, ActionTarget Target = ActionTarget.Self) : ActionEffect;
    /// <summary>Submits the selected subject into a named target register.</summary>
    /// <param name="Register">The authored target-register name.</param>
    /// <param name="Target">The subject source.</param>
    public sealed record Designate(string Register, ActionTarget Target = ActionTarget.AffectingSubject) : ActionEffect;
    /// <summary>Redraws a draw site (a <c>state</c> row declaring a <see cref="WorldDraw"/>) — the one effect
    /// admissible at both scopes, and the join that makes authored randomness and world rules one arc rather than
    /// two: a kit action, a world rule, and the <c>world.generate</c> console verb all reduce to composing the same
    /// <c>WorldMutation.Generate</c> and letting it drain through the ordinary tick boundary, so journal/undo cover a
    /// draw for free wherever it was fired from. This is also how a draw's moment is authored: a
    /// <see cref="WorldDrawTiming.TickPeriod"/> site redraws on an ordinary <c>$tick</c>-scheduled rule and an
    /// <see cref="WorldDrawTiming.Event"/> site on an event-gated one, so timing costs no mutation ordinal. At body
    /// scope the firing is staged during the body's advance and enqueued for the next tick's drain (an honestly-
    /// reported one-tick latency: this is the first <see cref="ActionEffect"/> to write the document rather than
    /// per-body state, so it is the first to pay the pipeline's own round trip).</summary>
    /// <param name="Row">The draw site's row name. One name, not a (source, destination) pair: a site's source is its
    /// own facet and a site is a scalar slot, so there is nothing else to address.</param>
    public sealed record Generate(string Row) : ActionEffect;
    /// <summary>Stages a rhythm-judge grading fact for the body whose trigger fired — the entity index, the judge
    /// window set, and the firing tick are collected during the body's advance (the same staged-output shape
    /// <see cref="Generate"/> uses) and drained by <c>WorldServer.Step</c> immediately after the body step, where
    /// they are graded against the world's musical clock. The grade is never computed here — this effect only names
    /// which judge row applies.</summary>
    /// <param name="JudgeRef">The declared <c>judges</c> row name (a <c>puck.judge.v1</c> reference) whose hit
    /// windows grade the press.</param>
    public sealed record Judge(string JudgeRef) : ActionEffect;
    /// <summary>Upserts a whole HUD panel row — world scope only (refused at body scope: a per-body action has no HUD
    /// panel of its own to author). Admits <c>WorldMutation.UpsertHudPanel</c> into the world-rule effect set
    /// through the same seam <see cref="Generate"/> uses: the compiled effect submits the mutation stamped
    /// <see cref="WorldPrincipal.World"/>, which <c>WorldServer.TryAdmitMutation</c> admits structurally, so the
    /// panel's own validation (capacity, unknown binding) is the ordinary whole-document revalidation every
    /// <see cref="UpsertHudPanel"/> submission — console, addon, or rule — already passes through.</summary>
    /// <param name="Panel">The whole panel row, elements included.</param>
    public sealed record UpsertHudPanel(WorldHudPanel Panel) : ActionEffect;
    /// <summary>Removes a HUD panel row by id — world scope only. See <see cref="UpsertHudPanel"/>'s remarks.</summary>
    /// <param name="Id">The panel id to remove.</param>
    public sealed record RemoveHudPanel(string Id) : ActionEffect;
    /// <summary>Upserts a whole placement row — world scope only (refused at body scope: a per-body action has no
    /// placement of its own to author). Admits <c>WorldMutation.UpsertPlacement</c> into the world-rule effect
    /// set through the same seam <see cref="Generate"/> uses.</summary>
    /// <param name="Placement">The whole placement row.</param>
    public sealed record UpsertPlacement(WorldPlacement Placement) : ActionEffect;
    /// <summary>Removes a placement row by id — world scope only. See <see cref="UpsertPlacement"/>'s remarks.</summary>
    /// <param name="Id">The placement id to remove.</param>
    public sealed record RemovePlacement(string Id) : ActionEffect;
    /// <summary>Writes a session snapshot of the world to its own loaded file — world scope only (refused at body
    /// scope: a per-body action has no world file of its own to save). A rule gate now decides when a save happens (an
    /// every-N-ticks cadence, a boss-defeated edge), closing the one gap the mutation substrate could not: a rule
    /// could already express any cadence over <c>$tick</c> or a state fact, but had nothing to fire that composed a
    /// save — every prior save was a human typing <c>world.save</c>, so a crashed server rewound to the last manual
    /// one.</summary>
    /// <remarks>
    /// <para><b>Not a door — the one effect with no <c>WorldMutation</c> kind.</b> Every other admitted effect
    /// (<see cref="SetState"/>, <see cref="Generate"/>, <see cref="UpsertHudPanel"/>, <see cref="UpsertPlacement"/>, …)
    /// composes an ordinary mutation and rides <c>WorldServer.TryApplyMutation</c>: compose, whole-document validate,
    /// install, journal. <c>Save</c> does none of that — it writes no sim state, composes no candidate document, and
    /// journals nothing. It is deterministic in when it fires (an ordinary rule gate over tick/state facts, evaluated
    /// the same way on every run) and projection-only in what it does: the same settle-at-save capture
    /// <c>world.save</c> itself runs (<c>WorldSessionCapture.Capture</c>), which folds live session state into a
    /// snapshot it serializes — it never mutates the in-memory definition. The sim state after a tick carrying a fired
    /// save effect is bit-identical to a tick without one; a replay hash cannot see it, because there is nothing for a
    /// hash to see. That is why this effect needed no <c>KindMask</c> ordinal at all: it is not a mutation. It rides
    /// <c>WorldServer.FireWorldRuleEffect</c> directly instead — the one effect that does.</para>
    /// <para><b>No authored path — the world's own canonical home only.</b> A document that could point a rule's save
    /// at an arbitrary filesystem path is a hazard for no authoring benefit a fixed target does not already cover, so
    /// this effect carries no path field: it always writes to <c>WorldDefinitionSource.SourcePath</c>, the same
    /// resolution the console's own no-argument <c>world.save</c> uses (the file the world was loaded from — an
    /// explicit <c>--world</c> path or the shipped default file, both always file-backed at boot; there is no
    /// "homeless world" boot shape in this engine, so this effect has no compile-time path refusal to author).</para>
    /// <para><b>Throttle honesty — no hidden guard.</b> A <see cref="ActionTriggerMode.Level"/> rule gating this
    /// effect fires it every tick the gate holds — 240 saves/second of disk I/O at the fixed step. This effect adds no
    /// throttle beyond the ordinary <see cref="ActionTriggerMode"/> vocabulary every other effect already uses: that
    /// is the author's own footgun, the same one <see cref="WorldRule.Mode"/>'s own remarks document for a
    /// level-triggered <c>addState</c> ("wrote 503 journal entries across 500 ticks before this mode existed, which is
    /// a measurement, not a style preference") — <see cref="ActionTriggerMode.Edge"/> is what an autosave cadence
    /// wants, for the identical reason. A hidden per-effect guard would be exactly the config surface this repository
    /// does not have.</para>
    /// <para><b>Failure is narrated, never fatal.</b> A write that fails (disk full, the target's directory gone, a
    /// read-only file) is caught at the composition-root seam that performs it and printed on stderr by name; the tick
    /// that fired it continues normally, and nothing about the sim is rolled back — there was nothing to roll back.
    /// </para>
    /// </remarks>
    public sealed record Save : ActionEffect;
}
/// <summary>One trigger channel of a lane binding: a gate, a press latch (the buffer — a press stays pending until the
/// gate opens or the latch expires; the release channel latches nothing), and the effects a fire applies in order.</summary>
/// <param name="Gate">The predicate that must hold to fire, or <see langword="null"/> for always.</param>
/// <param name="LatchSeconds">How long a press stays pending waiting for the gate. <c>0</c> means this tick only —
/// the press fires if the gate is open on its own edge tick and is dropped otherwise. Legitimate only on
/// <see cref="ActionSpec.OnPress"/>: the release channel latches nothing, so a non-zero value on
/// <see cref="ActionSpec.OnRelease"/> is refused by name at validation rather than parsed and discarded.</param>
/// <param name="Effects">The effects applied on fire, in order.</param>
public sealed record ActionTrigger(IReadOnlyList<ActionEffect> Effects, ActionPredicate? Gate = null, float LatchSeconds = 0f);
/// <summary>A lane's full binding: the press trigger and the release trigger. What a channel does is this data — the
/// engine implements only the facts, predicates, and effects.</summary>
/// <param name="OnPress">The rising-edge trigger, or <see langword="null"/>.</param>
/// <param name="OnRelease">The falling-edge trigger (evaluated immediately, never latched), or <see langword="null"/>.</param>
/// <param name="OnFact">Engine-fact-triggered effect lists evaluated independently of channel edges.</param>
public sealed record ActionSpec(ActionTrigger? OnPress = null, ActionTrigger? OnRelease = null, IReadOnlyList<ActionFactTrigger>? OnFact = null);
/// <summary>An authored effect list fired by one engine fact pulse — gated and edged by the same
/// <see cref="ActionTriggerMode"/> vocabulary a world rule uses.</summary>
/// <param name="Fact">The fact that fires the rule.</param>
/// <param name="Effects">The effects applied in order.</param>
/// <param name="Gate">An additional predicate that must hold beside <paramref name="Fact"/>, or
/// <see langword="null"/> for none.</param>
/// <param name="Mode">Whether the trigger fires every tick the condition holds (<see cref="ActionTriggerMode.Level"/>,
/// the default) or once per crossing (<see cref="ActionTriggerMode.Edge"/>). The
/// condition is <paramref name="Fact"/> and <paramref name="Gate"/> together — an edge trigger re-arms only when
/// that conjunction stops holding.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ActionFactTrigger(
    ActionFact Fact,
    IReadOnlyList<ActionEffect> Effects,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActionPredicate? Gate = null,
    ActionTriggerMode Mode = ActionTriggerMode.Level
);
