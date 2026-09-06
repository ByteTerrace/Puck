namespace Puck.State;

/// <summary>Whether a cataloged refusal is a protocol fault (the received bytes/shape are not legible input at all —
/// malformed JSON, a foreign magic, an unrecognized wire discriminant) or a verdict (the input parsed fine; a rule
/// examined its content or the caller's authority and refused what it means). A refusal read-back reports this per
/// row so a reader can separate "your input is not even legible" from "your input is legible and rejected".</summary>
public enum RefusalKind : byte {
    /// <summary>The bytes/shape do not parse as this door's input at all.</summary>
    ProtocolFault,

    /// <summary>The input parsed; a rule examined it (or the caller) and refused what it means.</summary>
    Verdict,
}
/// <summary>Declares one refusal a door can produce — attached directly to the enum member the door's refusal path
/// requires a caller to name. A host's refusal catalog discovers every so-tagged member by reflection across the
/// assemblies it names, so the enumeration it prints is read off the same finite set a door's refusal constructor
/// requires a caller to pick from — never a hand-kept second list a door's real throw sites can drift out of step
/// with. A door that grows a new refusal adds an enum member (or a new enum) and tags it here.
/// <para>What this does not guarantee: that a listed member is still reachable. Tagging is one-directional — it
/// proves a door cannot refuse with an unlisted reason (the constructor has no other way to be called), not that
/// every listed reason still has a live call site producing it.</para></summary>
/// <param name="door">The door's stable name (dotted, e.g. <c>sdf.decode</c>).</param>
/// <param name="condition">The one-line condition that triggers this refusal.</param>
/// <param name="kind">Whether this is a protocol fault or a verdict.</param>
[AttributeUsage(validOn: AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class RefusalAttribute(string door, string condition, RefusalKind kind) : Attribute {
    /// <summary>Gets the door's stable name.</summary>
    public string Door { get; } = door;
    /// <summary>Gets the one-line condition that triggers this refusal.</summary>
    public string Condition { get; } = condition;
    /// <summary>Gets a value indicating whether this is a protocol fault or a verdict.</summary>
    public RefusalKind Kind { get; } = kind;
}
/// <summary>One cataloged refusal row: a door, the stable id (the enum member's own name — never a second string kept
/// in sync with it by hand), the kind, and the one-line triggering condition.</summary>
/// <param name="Door">The door's stable name.</param>
/// <param name="Id">The refusal's stable id — the tagged enum member's own name.</param>
/// <param name="Kind">Whether this is a protocol fault or a verdict.</param>
/// <param name="Condition">The one-line triggering condition.</param>
public readonly record struct RefusalCatalogEntry(string Door, string Id, RefusalKind Kind, string Condition);
/// <summary>Names why a rule was refused during compilation, for every refusal the state-neutral compiler itself
/// raises. Every member is tagged <see cref="RefusalAttribute"/> under the <c>world.rule.compile</c> door, so a
/// refusal catalog lists the whole family beside the categories a document project's own families add (each
/// declares its own tagged enum and throws a <see cref="RuleException"/> carrying it).</summary>
public enum RuleRefusal : byte {
    /// <summary>The rule declares no name.</summary>
    [Refusal(door: "world.rule.compile", condition: "a rule declares no name", kind: RefusalKind.Verdict)]
    NameMissing,

    /// <summary>Another rule already declares this name.</summary>
    [Refusal(door: "world.rule.compile", condition: "another rule already declares this name", kind: RefusalKind.Verdict)]
    NameDuplicated,

    /// <summary>The rule's name carries the reserved <see cref="StateRow.ReservedNamePrefix"/> prefix, which
    /// marks what the engine mints — and nothing mints a rule.</summary>
    [Refusal(door: "world.rule.compile", condition: "a rule's name carries the reserved '$' prefix, which marks what the engine mints", kind: RefusalKind.Verdict)]
    NameReserved,

    /// <summary>A predicate kind that has no meaning at rule scope.</summary>
    [Refusal(door: "world.rule.compile", condition: "a gate uses a predicate kind ('now'/'recently'/'timerElapsed') that reads a per-body fact a world has none of", kind: RefusalKind.Verdict)]
    PredicateKindInadmissible,

    /// <summary>An effect kind that has no meaning at rule scope.</summary>
    [Refusal(door: "world.rule.compile", condition: "an effect uses a kind that addresses a body's own kinematic/register state, which world scope has none of", kind: RefusalKind.Verdict)]
    EffectKindInadmissible,

    /// <summary>A named state row is not declared.</summary>
    [Refusal(door: "world.rule.compile", condition: "an operand names a state row the document does not declare, and is not a reserved channel", kind: RefusalKind.Verdict)]
    StateRowUnknown,

    /// <summary>A named cell is not addressable on the row named (a null key on a keyed row, or a declared row whose
    /// kind cannot carry the operation).</summary>
    [Refusal(door: "world.rule.compile", condition: "a cell is not addressable on the row named (a null key on a keyed row, a text row compared/written as a number, or a dotted 'row.key' spelling)", kind: RefusalKind.Verdict)]
    StateCellUnaddressable,

    /// <summary>A <c>compareState</c> names both an authored 'value' and a 'comparandState', or neither — exactly
    /// one comparand spelling is admitted (a 'comparandKey' with no 'comparandState' is refused here too).</summary>
    [Refusal(door: "world.rule.compile", condition: "a compareState names both 'value' and 'comparandState' (or neither), or a bare 'comparandKey' with no 'comparandState'", kind: RefusalKind.Verdict)]
    ComparandAmbiguous,

    /// <summary>A <c>compareState</c>'s two sides resolve to incompatible cell kinds (an <c>int</c> row against a
    /// <c>fixed</c> row, say) — mixing scales silently is refused rather than coerced.</summary>
    [Refusal(door: "world.rule.compile", condition: "a compareState's two sides resolve to incompatible cell kinds", kind: RefusalKind.Verdict)]
    ComparandKindMismatch,

    /// <summary>An effect carries a participant target, which rule scope has none of.</summary>
    [Refusal(door: "world.rule.compile", condition: "an effect carries a non-Self target, which world scope has no entity to resolve", kind: RefusalKind.Verdict)]
    TargetInadmissible,

    /// <summary>A named generator row is not declared, or declares no generator.</summary>
    [Refusal(door: "world.rule.compile", condition: "a 'generate' effect names a row that is not declared, or declares no generator", kind: RefusalKind.Verdict)]
    GeneratorUnknown,

    /// <summary>A <c>setState</c>/<c>addState</c> effect names both an authored 'value' and a 'fromState', or
    /// neither — exactly one write-source spelling is admitted (a 'fromKey' with no 'fromState' is refused here too),
    /// the same duality <see cref="ComparandAmbiguous"/> enforces on the predicate side.</summary>
    [Refusal(door: "world.rule.compile", condition: "a setState/addState names both 'value' and 'fromState' (or neither), or a bare 'fromKey' with no 'fromState'", kind: RefusalKind.Verdict)]
    EffectSourceAmbiguous,

    /// <summary>A <c>setState</c>/<c>addState</c> effect's live <c>fromState</c> resolves to a cell kind that does
    /// not match the destination row's own kind (an <c>int</c> row fed from a <c>fixed</c> source, say) — mixing
    /// scales silently is refused rather than coerced, the effect-side sibling of
    /// <see cref="ComparandKindMismatch"/>.</summary>
    [Refusal(door: "world.rule.compile", condition: "a setState/addState's live 'fromState' resolves to a cell kind that does not match the destination row's own kind", kind: RefusalKind.Verdict)]
    EffectSourceKindMismatch,

    /// <summary>A <c>setState</c>/<c>addState</c> effect's <c>valueSeconds</c> is not an exact whole engine-tick
    /// count — <see cref="Puck.Maths.FixedTickConversion.TryDurationEngineTicksExact"/> found no whole multiple of
    /// <c>1/50400</c> second equal to the authored value — this refuses rather than rounds, so a duration that
    /// silently drifted from what was authored can never happen. The message names the nearest exact
    /// durations on either side; author one of those, or author the raw engine-tick count directly via 'value' when
    /// no terminating decimal spells the intended duration exactly.</summary>
    [Refusal(door: "world.rule.compile", condition: "a setState/addState's 'valueSeconds' is not an exact whole engine-tick count (not a whole multiple of 1/50400 s), or is negative", kind: RefusalKind.Verdict)]
    DurationNotExactEngineTicks,

    /// <summary>A <c>setState</c>/<c>addState</c> effect's non-negative <c>valueSeconds</c> would compile beyond the
    /// signed 64-bit raw carrier a <c>kind=int</c> state cell stores.</summary>
    [Refusal(door: "world.rule.compile", condition: "a setState/addState's non-negative 'valueSeconds' exceeds the signed 64-bit engine-tick carrier", kind: RefusalKind.Verdict)]
    DurationEngineTicksOutOfRange,

    /// <summary>A read operand — a <c>compareState</c> subject, a <c>comparandState</c>, or a <c>fromState</c> —
    /// addresses a cell its declared row does not carry. Reading an undeclared cell would be 0 forever with no
    /// refusal anywhere, so this refuses at compile instead; the mint-later pattern declares the cell first. Write
    /// destinations are exempt — a write mints its cell.</summary>
    [Refusal(door: "world.rule.compile", condition: "a READ operand addresses a cell its declared row does not carry", kind: RefusalKind.Verdict)]
    StateCellUndeclared,

    /// <summary>A <c>$reduce:</c> channel does not spell <c>$reduce:&lt;max|min|sum|count&gt;:&lt;row&gt;</c>, or
    /// names a row that is not declared or is kind=text.</summary>
    [Refusal(door: "world.rule.compile", condition: "a '$reduce:' channel does not spell '$reduce:<max|min|sum|count>:<row>' against a declared, non-text row", kind: RefusalKind.Verdict)]
    ReduceChannelMalformed,

    /// <summary>A rule's <c>zones</c> table is empty, names an undeclared row, a row that is not an ordered token
    /// zone, zones over different token domains or of different kinds, or one zone twice — or <c>forEach: "$zones"</c>
    /// iterates a table the rule does not declare.</summary>
    [Refusal(door: "world.rule.compile", condition: "a rule's 'zones' table is empty, names something other than distinct ordered zones over one token domain and kind, or is iterated by a rule that declares none", kind: RefusalKind.Verdict)]
    ZoneTableMalformed,

    /// <summary>A <c>$symmetry:</c> channel does not spell <c>$symmetry:&lt;function&gt;[:&lt;argument&gt;]:&lt;row&gt;</c>
    /// — an unknown function, a function given an argument it does not take (or missing one it needs), an argument
    /// that is neither a node literal nor <c>cell:&lt;row&gt;[.&lt;key&gt;]</c>, or a source row that is not a declared
    /// numeric row.</summary>
    [Refusal(door: "world.rule.compile", condition: "a '$symmetry:' channel does not spell '$symmetry:<function>[:<argument>]:<row>' with a known function, a well-formed argument and a declared numeric source row", kind: RefusalKind.Verdict)]
    SymmetryChannelMalformed,

    /// <summary>An <c>$argmax:</c>/<c>$argmin:</c> channel names no row, or a row that is not declared or is
    /// kind=text.</summary>
    [Refusal(door: "world.rule.compile", condition: "an '$argmax:'/'$argmin:' channel names no row, or a row that is not declared or is kind=text", kind: RefusalKind.Verdict)]
    ArgChannelMalformed,

    /// <summary>A key-yielding read — an <c>$argmax:</c>/<c>$argmin:</c> channel standalone or embedded in a
    /// participant reference, a reduction's <c>:where:</c> filter, a rule's <c>forEach</c> — names a row that is not
    /// keyed. A slot-shaped row's one cell carries the engine-minted <c>$value</c> key rather than an index — author
    /// a keyed row (a per-participant tally) instead.</summary>
    [Refusal(door: "world.rule.compile", condition: "an argmax/argmin body reference names a row that is not KEYED — a slot row's cell has no body-index key", kind: RefusalKind.Verdict)]
    ArgRowNotKeyed,
}
/// <summary>Reports a rule compilation refusal — caught and reported by name at validation. The category is a
/// <see cref="RuleRefusal"/> for the refusals the state-neutral compiler raises itself, or a member of a document
/// project family's own <see cref="RefusalAttribute"/>-tagged enum.</summary>
public sealed class RuleException : ArgumentException {
    /// <summary>Initializes a rule refusal.</summary>
    /// <param name="refusal">The refusal category — a member of a <see cref="RefusalAttribute"/>-tagged enum.</param>
    /// <param name="ruleName">The refusing rule's name.</param>
    /// <param name="detail">What was wrong, in the author's own vocabulary.</param>
    /// <param name="subject">The authored-row noun this refusal names in its message — <c>"rule"</c> (the default),
    /// or the noun of another authoring surface that compiles through the same rule machinery.</param>
    public RuleException(Enum refusal, string ruleName, string detail, string subject = "rule")
        : base(message: $"{subject} '{ruleName}' refused {refusal}: {detail}") {
        Refusal = refusal;
    }

    /// <summary>Gets the refusal category.</summary>
    public Enum Refusal { get; }
}

/// <summary>The runtime refusals the evaluator itself draws while firing; a document project's own effect arms
/// declare their own tagged enum and report through the same ledger.</summary>
public enum RuleEffectRefusal : byte {
    /// <summary>A binding, gate conjunct, or effect expression overflowed, divided by zero, left a function's
    /// domain, or read a fact with no number.</summary>
    [Refusal(door: "world.rule.effect", condition: "a rule expression overflows, divides by zero, leaves a function's domain, or produces an invalid stack result", kind: RefusalKind.Verdict)]
    Arithmetic,

    /// <summary>The host's mutation door refused the effect's composition, validation, or admission.</summary>
    [Refusal(door: "world.rule.effect", condition: "a rule-produced mutation refuses composition, validation, or admission", kind: RefusalKind.Verdict)]
    MutationRejected,

    /// <summary>A dynamic table key named an entry its table does not carry.</summary>
    [Refusal(door: "world.rule.effect", condition: "a '$table:' read names a key its table does not carry", kind: RefusalKind.Verdict)]
    TableKeyMissing,
}
