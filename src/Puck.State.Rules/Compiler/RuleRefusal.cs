namespace Puck.State.Rules;

/// <summary>Names why a rule was refused during compilation, for every refusal the state-neutral compiler raises
/// itself. Every member is tagged <see cref="RefusalAttribute"/> under the <c>state.rule.compile</c> door, so a
/// refusal catalog lists the whole family beside the categories a document project's own families add (each declares
/// its own tagged enum and throws a <see cref="RuleException"/> carrying it).</summary>
public enum RuleRefusal : byte {
    /// <summary>The rule declares no name.</summary>
    [Refusal(door: "state.rule.compile", condition: "a rule declares no name", kind: RefusalKind.Verdict)]
    NameMissing,

    /// <summary>Another rule already declares this name.</summary>
    [Refusal(door: "state.rule.compile", condition: "another rule already declares this name", kind: RefusalKind.Verdict)]
    NameDuplicated,

    /// <summary>The rule's name carries the reserved <see cref="StateRow.ReservedNamePrefix"/> prefix, which marks
    /// what the engine mints — and nothing mints a rule.</summary>
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
    /// count.</summary>
    [Refusal(door: "state.rule.compile", condition: "a setState/addState's 'valueSeconds' is not an exact whole engine-tick count (not a whole multiple of 1/50400 s), or is negative", kind: RefusalKind.Verdict)]
    DurationNotExactEngineTicks,

    /// <summary>A non-negative <c>valueSeconds</c> or delay would compile beyond the signed 64-bit raw carrier.</summary>
    [Refusal(door: "state.rule.compile", condition: "a non-negative 'valueSeconds' or scheduled delay exceeds the signed 64-bit tick carrier", kind: RefusalKind.Verdict)]
    DurationEngineTicksOutOfRange,

    /// <summary>A read operand addresses a cell its declared row does not carry.</summary>
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
}
