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
/// <summary>Names why a rule was refused during compilation, for every refusal the state-neutral compiler raises
/// itself. Every member is tagged <see cref="RefusalAttribute"/> under the <c>state.rule.compile</c> door, so a
/// refusal catalog lists the whole family beside the categories a document project's own families add (each declares
/// its own tagged enum and throws a <see cref="RuleException"/> carrying it). The vector transforms in
/// <c>Puck.State.Vectors</c> repeat the compiler's shape checks when they fire and report a failed one with the same
/// member; a refusal only a firing can decide is a <see cref="RuleEffectRefusal"/>.</summary>
public enum RuleRefusal : byte {
    /// <summary>The rule declares no name.</summary>
    [Refusal(door: "state.rule.compile", condition: "a rule declares no name", kind: RefusalKind.Verdict)]
    NameMissing,

    /// <summary>Another rule already declares this name.</summary>
    [Refusal(door: "state.rule.compile", condition: "another rule already declares this name", kind: RefusalKind.Verdict)]
    NameDuplicated,

    /// <summary>The rule's name carries the reserved <see cref="StateRow.ReservedNamePrefix"/> prefix, which marks
    /// what the engine mints — and the engine mints no rule.</summary>
    [Refusal(door: "state.rule.compile", condition: "a rule's name carries the reserved '$' prefix, which marks what the engine mints", kind: RefusalKind.Verdict)]
    NameReserved,

    /// <summary>A predicate kind that has no meaning at rule scope.</summary>
    [Refusal(door: "state.rule.compile", condition: "a gate uses a predicate kind that reads a per-body fact a section has none of, or nests past the token ceiling", kind: RefusalKind.Verdict)]
    PredicateKindInadmissible,

    /// <summary>An effect kind that has no meaning at rule scope, or a declaration past one of the rule ceilings.</summary>
    [Refusal(door: "state.rule.compile", condition: "an effect uses a kind rule scope has no meaning for, or a rule exceeds its binding, effect, or transaction ceiling", kind: RefusalKind.Verdict)]
    EffectKindInadmissible,

    /// <summary>A named state row is not declared.</summary>
    [Refusal(door: "state.rule.compile", condition: "an operand names a state row the document does not declare, and is not a reserved channel", kind: RefusalKind.Verdict)]
    StateRowUnknown,

    /// <summary>A named cell is not addressable on the row named (a null key on a keyed row, or a declared row whose
    /// kind cannot carry the operation).</summary>
    [Refusal(door: "state.rule.compile", condition: "a cell is not addressable on the row named (a null key on a keyed row, a text row compared/written as a number, or a dotted 'row.key' spelling)", kind: RefusalKind.Verdict)]
    StateCellUnaddressable,

    /// <summary>A <c>compareState</c> names both an authored <c>value</c> and a <c>comparandState</c>, or neither.</summary>
    [Refusal(door: "state.rule.compile", condition: "a compareState names both 'value' and 'comparandState' (or neither), or a bare 'comparandKey' with no 'comparandState'", kind: RefusalKind.Verdict)]
    ComparandAmbiguous,

    /// <summary>A <c>compareState</c>'s two sides resolve to incompatible cell kinds.</summary>
    [Refusal(door: "state.rule.compile", condition: "a compareState's two sides resolve to incompatible cell kinds", kind: RefusalKind.Verdict)]
    ComparandKindMismatch,

    /// <summary>An effect carries a participant target, which rule scope has none of.</summary>
    [Refusal(door: "state.rule.compile", condition: "an effect carries a non-Self target, which rule scope has no entity to resolve", kind: RefusalKind.Verdict)]
    TargetInadmissible,

    /// <summary>A named generator row is not declared, or declares no generator.</summary>
    [Refusal(door: "state.rule.compile", condition: "a 'generate' effect names a row that is not declared, or declares no draw", kind: RefusalKind.Verdict)]
    GeneratorUnknown,

    /// <summary>A <c>setState</c>/<c>addState</c> effect names both an authored <c>value</c> and a
    /// <c>fromState</c>, or neither.</summary>
    [Refusal(door: "state.rule.compile", condition: "a setState/addState names both 'value' and 'fromState' (or neither), or a bare 'fromKey' with no 'fromState'", kind: RefusalKind.Verdict)]
    EffectSourceAmbiguous,

    /// <summary>A <c>setState</c>/<c>addState</c> effect's live <c>fromState</c> resolves to a cell kind that does
    /// not match the destination row's own kind.</summary>
    [Refusal(door: "state.rule.compile", condition: "a setState/addState's live 'fromState' resolves to a cell kind that does not match the destination row's own kind", kind: RefusalKind.Verdict)]
    EffectSourceKindMismatch,

    /// <summary>A <c>setState</c>/<c>addState</c> effect's <c>valueSeconds</c> is not an exact whole engine-tick
    /// count: <see cref="Puck.Maths.FixedTickConversion.TryDurationEngineTicksExact"/> found no whole multiple of
    /// <c>1/50400</c> second equal to the authored value. The compiler refuses rather than rounds, and the message
    /// names the nearest exact durations on either side.</summary>
    [Refusal(door: "state.rule.compile", condition: "a setState/addState's 'valueSeconds' is not an exact whole engine-tick count (not a whole multiple of 1/50400 s), or is negative", kind: RefusalKind.Verdict)]
    DurationNotExactEngineTicks,

    /// <summary>A non-negative <c>valueSeconds</c> or delay would compile beyond the signed 64-bit raw carrier.</summary>
    [Refusal(door: "state.rule.compile", condition: "a non-negative 'valueSeconds' or scheduled delay exceeds the signed 64-bit tick carrier", kind: RefusalKind.Verdict)]
    DurationEngineTicksOutOfRange,

    /// <summary>A read operand — a <c>compareState</c> subject, a <c>comparandState</c>, or a <c>fromState</c> —
    /// addresses a cell its declared row does not carry. Such a read would answer 0 forever, so the compiler refuses
    /// it; a write destination is exempt, because a write mints its cell.</summary>
    [Refusal(door: "state.rule.compile", condition: "a READ operand addresses a cell its declared row does not carry", kind: RefusalKind.Verdict)]
    StateCellUndeclared,

    /// <summary>A <c>$reduce:</c> channel does not spell <c>$reduce:&lt;max|min|sum|count|arrangementRank&gt;:&lt;row&gt;</c>,
    /// or names a row that is not declared or is kind=Text.</summary>
    [Refusal(door: "state.rule.compile", condition: "a '$reduce:' channel does not spell '$reduce:<max|min|sum|count|arrangementRank>:<row>' against a declared, non-text row", kind: RefusalKind.Verdict)]
    ReduceChannelMalformed,

    /// <summary>A rule's <c>zones</c> table is empty, names something other than distinct ordered zones over one
    /// token domain and kind, or is iterated by a rule that declares none.</summary>
    [Refusal(door: "state.rule.compile", condition: "a rule's 'zones' table is empty, names something other than distinct ordered zones over one token domain and kind, or is iterated by a rule that declares none", kind: RefusalKind.Verdict)]
    ZoneTableMalformed,

    /// <summary>A <c>$symmetry:</c> channel does not spell
    /// <c>$symmetry:&lt;function&gt;[:&lt;argument&gt;]:&lt;row&gt;</c>.</summary>
    [Refusal(door: "state.rule.compile", condition: "a '$symmetry:' channel does not spell '$symmetry:<function>[:<argument>]:<row>' with a known function, a well-formed argument and a declared numeric source row", kind: RefusalKind.Verdict)]
    SymmetryChannelMalformed,

    /// <summary>A key-yielding read names a row that is not keyed.</summary>
    [Refusal(door: "state.rule.compile", condition: "an argmax/argmin body reference, a reduction filter, or a forEach names a row that is not keyed", kind: RefusalKind.Verdict)]
    ArgRowNotKeyed,

    /// <summary>A rule group's declaration is malformed: no members, a member no rule declares, a member claimed by
    /// two groups, a pass ceiling outside its bounds, or a staged group with no step.</summary>
    [Refusal(door: "state.rule.compile", condition: "a rule group declares no members, names a rule no section declares, claims a rule another group already claims, declares a pass ceiling outside its bounds, or is staged with no step", kind: RefusalKind.Verdict)]
    RuleGroupMalformed,

    /// <summary>An effect whose arm cannot be rewound is read back inside the same firing.</summary>
    [Refusal(door: "state.rule.compile", condition: "a later effect of the same firing reads a cell an irreversible arm writes, whose write lands only after the firing commits", kind: RefusalKind.Verdict)]
    IrreversibleResultRead,

    /// <summary>Vector operands do not share the same space or length.</summary>
    [Refusal(door: "state.rule.compile", condition: "vector operands do not belong to the same space", kind: RefusalKind.Verdict)]
    VectorSpaceMismatch,

    /// <summary>An operand expected to be a vector is not of kind Vector.</summary>
    [Refusal(door: "state.rule.compile", condition: "an operand is not a vector", kind: RefusalKind.Verdict)]
    VectorOperandNotVector,

    /// <summary>Vector mix term count or weight is out of range.</summary>
    [Refusal(door: "state.rule.compile", condition: "vector mix terms count or weight is out of range", kind: RefusalKind.Verdict)]
    VectorMixTerms,

    /// <summary>A vector filter's <c>where</c> row is not a keyed Bool row.</summary>
    [Refusal(door: "state.rule.compile", condition: "vector where row is not a keyed Bool row", kind: RefusalKind.Verdict)]
    VectorFilterShape,

    /// <summary>A vector exclude key is malformed.</summary>
    [Refusal(door: "state.rule.compile", condition: "vector exclude key is malformed", kind: RefusalKind.Verdict)]
    VectorExcludeKey,

    /// <summary>A vector nearest destination shape or parameters are invalid.</summary>
    [Refusal(door: "state.rule.compile", condition: "vector nearest transform shape or parameters are invalid", kind: RefusalKind.Verdict)]
    VectorNearestShape,

    /// <summary>A vector remember destination shape or parameters are invalid.</summary>
    [Refusal(door: "state.rule.compile", condition: "vector remember transform shape or parameters are invalid", kind: RefusalKind.Verdict)]
    VectorRememberShape,

    /// <summary>A vector cell was targeted by an effect kind that does not admit vectors.</summary>
    [Refusal(door: "state.rule.compile", condition: "a vector cell was targeted by an unsupported effect kind", kind: RefusalKind.Verdict)]
    VectorEffectNotAdmitted,

    /// <summary>A transform writes a row a reader may see who may not see a row the transform reads, so the written
    /// order or value would disclose what that reader is refused (<see cref="StateVisibility.Encloses"/>).</summary>
    [Refusal(door: "state.rule.compile", condition: "a transform writes a row whose audience a row it reads does not enclose", kind: RefusalKind.Verdict)]
    TransformWidensAudience,
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
    /// <param name="path">Where inside the rule the refusal was drawn, as the document spells the member —
    /// <c>gate</c>, <c>locals[0]</c>, <c>decision.options[1]</c> — or empty when it is about the rule itself.</param>
    public RuleException(Enum refusal, string ruleName, string detail, string subject = "rule", string path = "")
        : base(message: $"{subject} '{ruleName}' refused {refusal}: {detail}") {
        Detail = detail;
        Path = path;
        Refusal = refusal;
        RuleName = ruleName;
        Subject = subject;
    }

    /// <summary>Gets what was wrong, in the author's own vocabulary.</summary>
    public string Detail { get; }
    /// <summary>Gets where inside the rule the refusal was drawn, relative to the rule's own document node, or the
    /// empty string when it is about the rule itself.</summary>
    /// <remarks>A caller that knows the rule's index prepends <c>rules[i].</c> to name the authored line: the
    /// refusal itself has no document in hand.</remarks>
    public string Path { get; }
    /// <summary>Gets the refusal category.</summary>
    public Enum Refusal { get; }
    /// <summary>Gets the refusing rule's name.</summary>
    public string RuleName { get; }
    /// <summary>Gets the authored-row noun this refusal names in its message.</summary>
    public string Subject { get; }

    /// <summary>Returns this refusal located inside <paramref name="segment"/>, leaving one already located
    /// there.</summary>
    /// <param name="segment">The member the refusal was drawn under, as the document spells it.</param>
    /// <returns>The located refusal.</returns>
    public RuleException Within(string segment) => ((Path.Length == 0)
        ? new RuleException(
            detail: Detail,
            path: segment,
            refusal: Refusal,
            ruleName: RuleName,
            subject: Subject
        )
        : new RuleException(
            detail: Detail,
            path: $"{segment}.{Path}",
            refusal: Refusal,
            ruleName: RuleName,
            subject: Subject
        )
    );
}
