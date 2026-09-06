using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>An authored effect row a rule fires when its gate holds. The arms declared here are the ones this
/// library owns — every one addresses a state row and nothing else; a document project appends its own derived arms
/// (a participant's kinematics, a presentation cue, a whole-row document upsert) through a
/// <see cref="RuleVocabulary"/> (see <see cref="RuleVocabulary.ExtendJson"/>) rather than by editing this list.</summary>
[JsonDerivedType(typeof(ActionEffect.SetState), typeDiscriminator: "setState")]
[JsonDerivedType(typeof(ActionEffect.AddState), typeDiscriminator: "addState")]
[JsonDerivedType(typeof(ActionEffect.PushState), typeDiscriminator: "pushState")]
[JsonDerivedType(typeof(ActionEffect.TransformState), typeDiscriminator: "transformState")]
[JsonDerivedType(typeof(ActionEffect.CountdownState), typeDiscriminator: "countdownState")]
[JsonDerivedType(typeof(ActionEffect.Generate), typeDiscriminator: "generate")]
[JsonDerivedType(typeof(ActionEffect.RemoveStateCell), typeDiscriminator: "removeStateCell")]
[JsonDerivedType(typeof(ActionEffect.ScheduleState), typeDiscriminator: "scheduleState")]
[JsonDerivedType(typeof(ActionEffect.Transaction), typeDiscriminator: "transaction")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record ActionEffect {
    /// <summary>Applies a bounded state transform through the ordinary mutation pipeline.</summary>
    /// <param name="Transform">The typed operation.</param>
    public sealed record TransformState(StateTransform Transform) : ActionEffect;
    /// <summary>Pushes one numeric value into a history row's ring (see <see cref="StateDomain.Ring"/>), the same
    /// source spellings as <see cref="SetState"/> minus text: exactly one of <paramref name="Value"/>,
    /// <paramref name="FromState"/>, or <paramref name="Expression"/>.</summary>
    /// <param name="State">The history row.</param>
    /// <param name="Value">An exact decimal literal in the row's kind.</param>
    /// <param name="FromState">A state row or reserved channel read live at every firing.</param>
    /// <param name="FromKey">The cell of <paramref name="FromState"/>, or null for its slot.</param>
    /// <param name="Expression">A bounded numeric expression evaluated in the row's kind.</param>
    public sealed record PushState(
        string State,
        decimal? Value = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ValueExpression? Expression = null
    ) : ActionEffect;
    /// <summary>Writes a named state cell — a <c>state</c>-section row's cell at rule scope, a counter slot inside a
    /// host's per-participant action program.</summary>
    /// <param name="State">The state row name (or the host's counter slot).</param>
    /// <param name="Value">The literal value to write, or <see langword="null"/> when <paramref name="FromState"/>
    /// spells a live operand to copy instead — exactly one of the source spellings is authored (refused by name
    /// when both or neither are present, the same duality <see cref="ActionPredicate.CompareState"/>'s own comparand
    /// carries).</param>
    /// <param name="Target">The addressed participant — meaningful only inside a host's per-participant program; a
    /// non-<see cref="ActionTarget.Self"/> target is refused at rule scope, where there is no participant to select.</param>
    /// <param name="Key">The cell inside <paramref name="State"/> — <see langword="null"/> writes the row's slot cell,
    /// which a keyed row does not have (refused by name).</param>
    /// <param name="FromState">Another declared <c>state</c>-section row name, or a reserved channel, read live at
    /// fire time and copied in place of an authored <paramref name="Value"/> — the row that resets to another row's
    /// own current value (a shadow row mirroring a counter someone else advances), never only a standing literal.
    /// Resolved through the same operand walk <see cref="ActionPredicate.CompareState"/>'s own <c>ComparandState</c>
    /// uses; mixing a <c>fixed</c> row into an <c>int</c> destination (or the reverse) is refused by name rather than
    /// coerced.</param>
    /// <param name="FromKey">The cell inside <paramref name="FromState"/>, on the same (row, key) terms as
    /// <paramref name="Key"/>. Refused when <paramref name="FromState"/> names a reserved channel or is absent.</param>
    /// <param name="ValueSeconds">An alternative to <paramref name="Value"/> for a <c>kind=int</c> state row a
    /// companion <see cref="CountdownState"/> effect decrements once per simulation tick (a countdown/cooldown).
    /// Authored in seconds — a physical unit, not a tick count, so a document's rate can change without silently
    /// retuning every cooldown — and converted once at rule compile time to an exact whole engine-tick count via
    /// <see cref="Puck.Maths.FixedTickConversion.TryDurationEngineTicksExact"/>, never re-derived at runtime and never
    /// rounded: a duration that is not an exact whole engine-tick count is refused rather than silently rounded away
    /// (<see cref="RuleRefusal.DurationNotExactEngineTicks"/>). Typed <see cref="decimal"/> rather than
    /// <see langword="float"/> because JSON deserializes a number token to <see cref="decimal"/> exactly (base-10, no
    /// binary-float intermediate), and most terminating decimals — the only ones an author can spell — have no exact
    /// binary float or fixed-point spelling either.</param>
    /// <param name="Text">The literal a <c>kind=text</c> state row's cell takes — the fourth spelling beside
    /// <paramref name="Value"/>/<paramref name="FromState"/>/<paramref name="ValueSeconds"/>, exactly one authored.
    /// Every state-bound document value re-resolves on the write, so this is how a rule restyles what a state-bound
    /// document row names.</param>
    /// <param name="Expression">A bounded numeric expression evaluated in the destination row's integer or
    /// fixed-point domain. Exactly one source spelling is authored.</param>
    public sealed record SetState(
        string State,
        decimal? Value = null,
        ActionTarget Target = ActionTarget.Self,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? ValueSeconds = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ValueExpression? Expression = null
    ) : ActionEffect;
    /// <summary>Adds to a named state cell — the same shape as <see cref="SetState"/>, here the source is the addend
    /// rather than the replacement.</summary>
    /// <param name="State">The state row name (or the host's counter slot).</param>
    /// <param name="Value">The literal addend, or <see langword="null"/> when <paramref name="FromState"/> spells a
    /// live addend instead — see <see cref="SetState.Value"/>'s remarks.</param>
    /// <param name="Target">The addressed participant — see <see cref="SetState.Target"/>.</param>
    /// <param name="Key">The cell inside <paramref name="State"/> — see <see cref="SetState.Key"/>.</param>
    /// <param name="FromState">See <see cref="SetState.FromState"/>'s remarks; here the addend is read live rather
    /// than the replacement.</param>
    /// <param name="FromKey">The cell inside <paramref name="FromState"/> — see <see cref="SetState.FromKey"/>.</param>
    /// <param name="ValueSeconds">See <see cref="SetState.ValueSeconds"/>'s remarks; here the converted tick count is
    /// the addend rather than the replacement.</param>
    /// <param name="Expression">See <see cref="SetState.Expression"/>.</param>
    public sealed record AddState(
        string State,
        decimal? Value = null,
        ActionTarget Target = ActionTarget.Self,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FromKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? ValueSeconds = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ValueExpression? Expression = null
    ) : ActionEffect;
    /// <summary>Decrements a state countdown by the current simulation step's engine-tick width, saturating at zero.
    /// The destination must be a <c>kind=int nonNegative=true</c> row. Unlike an authored <see cref="AddState"/>
    /// constant, this effect consumes the runtime step width, so changing the document's authored tick rate never
    /// retunes the duration. When the remaining duration is shorter than one step, the computed decrement is exactly
    /// the remaining value; it reaches zero without asking the explicit-write door to admit a negative candidate.</summary>
    /// <param name="State">The countdown state-row name.</param>
    /// <param name="Key">The cell inside <paramref name="State"/>; <see langword="null"/> addresses its slot.</param>
    public sealed record CountdownState(
        string State,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null
    ) : ActionEffect;
    /// <summary>Removes one addressed cell from a declared state row.</summary>
    /// <param name="State">The row to remove from.</param>
    /// <param name="Key">The optional cell key.</param>
    public sealed record RemoveStateCell(
        string State,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null
    ) : ActionEffect;
    /// <summary>Writes an absolute simulation due tick into an integer state cell. The delay is converted against
    /// the document's authored simulation rate and rounded up, so it never fires early. A companion rule compares
    /// <c>$tick</c> against the cell and removes it after handling, forming a bounded, document-backed scheduler.</summary>
    /// <param name="State">The integer destination row.</param>
    /// <param name="DelaySeconds">The non-negative delay, rounded up to simulation ticks.</param>
    /// <param name="Key">The optional cell key.</param>
    public sealed record ScheduleState(
        string State,
        decimal DelaySeconds,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Key = null
    ) : ActionEffect;
    /// <summary>Applies a bounded list of effects atomically after preflight. When any effect refuses, none apply and
    /// <paramref name="OnFailure"/> runs instead. The compiler refuses nested transactions and effects
    /// a document project has not admitted through <see cref="EffectFamily.AllowsTransaction"/>.</summary>
    /// <param name="Effects">The main transaction branch.</param>
    /// <param name="OnFailure">The optional branch run after a main-branch refusal.</param>
    public sealed record Transaction(
        IReadOnlyList<ActionEffect> Effects,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ActionEffect>? OnFailure = null
    ) : ActionEffect;
    /// <summary>Redraws a draw site (a <c>state</c> row declaring a <see cref="Draw"/>). A draw's moment is authored
    /// through the rule that fires this: a <see cref="DrawTiming.TickPeriod"/> site redraws on an ordinary
    /// <c>$tick</c>-scheduled rule and a <see cref="DrawTiming.Event"/> site on an event-gated one, so timing costs no
    /// mutation ordinal.</summary>
    /// <param name="Row">The draw site's row name. One name, not a (source, destination) pair: a site's source is its
    /// own facet and a site is a scalar slot, so there is nothing else to address.</param>
    public sealed record Generate(string Row) : ActionEffect;
}
