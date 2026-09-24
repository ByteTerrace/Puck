namespace Puck.State.Rules;

/// <summary>Names why a state transform did not apply. One group of members per
/// <see cref="ArenaTransform"/> case, named after the transform it belongs to, so the twelve scalar transforms'
/// refusals are enumerated here and nowhere else.</summary>
/// <remarks>The four vector transforms report the catalogued codes
/// <see cref="ArenaVectorTransforms"/> already draws (<see cref="RuleRefusal"/> for a shape refusal,
/// <see cref="RuleEffectRefusal"/> for a refused write), which this enum passes through unchanged rather than
/// restating.</remarks>
public enum TransformRefusal : byte {
    /// <summary>A transform addresses a row the arena does not carry, or one whose shape it cannot act on.</summary>
    [Refusal(door: "state.transform", condition: "a transform addresses a row the arena does not carry or cannot act on", kind: RefusalKind.Verdict)]
    RowUnaddressable,

    /// <summary>A transfer's ends are not two ordered zones over one token domain, or a live end selected no
    /// row.</summary>
    [Refusal(door: "state.transform", condition: "a transfer's ends are not two ordered zones over one token domain, or a live end selects no row", kind: RefusalKind.Verdict)]
    TransferEndsMismatched,

    /// <summary>A transfer's selector arguments or count do not match its selector.</summary>
    [Refusal(door: "state.transform", condition: "a transfer's selector arguments or count do not match its selector", kind: RefusalKind.Verdict)]
    TransferSelectorArguments,

    /// <summary>A transfer's source zone holds fewer tokens than the transfer moves.</summary>
    [Refusal(door: "state.transform", condition: "a transfer's source zone holds fewer tokens than the transfer moves", kind: RefusalKind.Verdict)]
    TransferSourceShort,

    /// <summary>A transfer's destination zone has no room for the tokens.</summary>
    [Refusal(door: "state.transform", condition: "a transfer's destination zone has no room for the tokens", kind: RefusalKind.Verdict)]
    TransferDestinationFull,

    /// <summary>A transfer's source zone does not hold the selected token.</summary>
    [Refusal(door: "state.transform", condition: "a transfer's source zone does not hold the selected token", kind: RefusalKind.Verdict)]
    TransferTokenAbsent,

    /// <summary>The arena's member door refused a transfer — a duplicate key, a kind disagreement, or a value the
    /// destination does not admit.</summary>
    [Refusal(door: "state.transform", condition: "the arena's member door refuses a transfer", kind: RefusalKind.Verdict)]
    TransferRejected,

    /// <summary>A transfer's draw site is not a redrawable integer stream draw, or its draw refused.</summary>
    [Refusal(door: "state.transform", condition: "a transfer's draw site is not a redrawable integer stream draw, or its draw refuses", kind: RefusalKind.Verdict)]
    TransferDrawSite,

    /// <summary>A setRay names no board cell, no direction its topology steps in, or no compiled pattern.</summary>
    [Refusal(door: "state.transform", condition: "a setRay names no board cell, no direction its topology steps in, or no compiled pattern", kind: RefusalKind.Verdict)]
    SetRayAddressing,

    /// <summary>A setRay's pattern accepts no non-empty prefix of the ray.</summary>
    [Refusal(door: "state.transform", condition: "a setRay's pattern accepts no non-empty prefix of the ray", kind: RefusalKind.Verdict)]
    SetRayEmptyPrefix,

    /// <summary>A setRay writes a value its board row does not admit.</summary>
    [Refusal(door: "state.transform", condition: "a setRay writes a value its board row does not admit", kind: RefusalKind.Verdict)]
    SetRayValueInadmissible,

    /// <summary>A shuffle addresses a row that holds no members to reorder.</summary>
    [Refusal(door: "state.transform", condition: "a shuffle addresses a row that holds no members to reorder", kind: RefusalKind.Verdict)]
    ShuffleRowShape,

    /// <summary>A shuffle's draw site is not a redrawable integer stream draw, or its draw refused.</summary>
    [Refusal(door: "state.transform", condition: "a shuffle's draw site is not a redrawable integer stream draw, or its draw refuses", kind: RefusalKind.Verdict)]
    ShuffleDrawSite,

    /// <summary>A sortZone addresses no ordered zone, or names no sort keys.</summary>
    [Refusal(door: "state.transform", condition: "a sortZone addresses no ordered zone, or names no sort keys", kind: RefusalKind.Verdict)]
    SortZoneShape,

    /// <summary>A sortZone's attribute row is not a numeric row keyed over the zone's token domain.</summary>
    [Refusal(door: "state.transform", condition: "a sortZone's attribute row is not a numeric row keyed over the zone's token domain", kind: RefusalKind.Verdict)]
    SortZoneAttribute,

    /// <summary>A sortKeyed addresses no keyed or ordered numeric row.</summary>
    [Refusal(door: "state.transform", condition: "a sortKeyed addresses no keyed or ordered numeric row", kind: RefusalKind.Verdict)]
    SortKeyedShape,

    /// <summary>A writeSet addresses no board row, or reads a mask onto a board of more than
    /// <see cref="BoardMask.MaxCells"/> cells.</summary>
    [Refusal(door: "state.transform", condition: "a writeSet addresses no board row, or reads a mask onto a board of more than 64 cells", kind: RefusalKind.Verdict)]
    WriteSetBoard,

    /// <summary>A writeSet's mask is not readable from an integer cell, or its declared set does not lower at the
    /// board's width.</summary>
    [Refusal(door: "state.transform", condition: "a writeSet's mask is not readable from an integer cell, or its declared set does not lower at the board's width", kind: RefusalKind.Verdict)]
    WriteSetSource,

    /// <summary>A writeSet writes a value its board row does not admit.</summary>
    [Refusal(door: "state.transform", condition: "a writeSet writes a value its board row does not admit", kind: RefusalKind.Verdict)]
    WriteSetValueInadmissible,

    /// <summary>A boardCombine addresses no board row.</summary>
    [Refusal(door: "state.transform", condition: "a boardCombine addresses no board row", kind: RefusalKind.Verdict)]
    BoardCombineBoard,

    /// <summary>A boardCombine's source board does not lie over the written board's topology, or its declared set
    /// does not lower at the board's width.</summary>
    [Refusal(door: "state.transform", condition: "a boardCombine's source board does not lie over the written board's topology, or its declared set does not lower at the board's width", kind: RefusalKind.Verdict)]
    BoardCombineOperands,

    /// <summary>An arrange addresses no ordered zone of at most <see cref="StateReader.MaxArrangementTokens"/> tokens
    /// every one of which its domain declares.</summary>
    [Refusal(door: "state.transform", condition: "an arrange addresses no ordered zone of at most 20 tokens every one of which its domain declares", kind: RefusalKind.Verdict)]
    ArrangeShape,

    /// <summary>An arrange reads its rank from a cell that holds no integer.</summary>
    [Refusal(door: "state.transform", condition: "an arrange reads its rank from a cell that holds no integer", kind: RefusalKind.Verdict)]
    ArrangeRankSource,

    /// <summary>An arrange's rank lies outside 0..k!-1 for the zone's token count.</summary>
    [Refusal(door: "state.transform", condition: "an arrange's rank lies outside 0..k!-1 for the zone's token count", kind: RefusalKind.Verdict)]
    ArrangeRankRange,

    /// <summary>A pushRay has no live origin token, reaches an edge or cycle before an admitted terminator, or its pattern rejects the run.</summary>
    [Refusal(door: "state.transform", condition: "a pushRay has no live origin token or no admitted terminating cell", kind: RefusalKind.Verdict)]
    PushRayBlocked,

    /// <summary>A pushRay addresses an invalid pool, field, topology, or direction.</summary>
    [Refusal(door: "state.transform", condition: "a pushRay addresses an invalid pool, field, topology, or direction", kind: RefusalKind.Verdict)]
    PushRayAddressing,

    /// <summary>A clearEnclosed addresses no integer board row.</summary>
    [Refusal(door: "state.transform", condition: "a clearEnclosed addresses no integer board row", kind: RefusalKind.Verdict)]
    ClearEnclosedBoard,

    /// <summary>A clearEnclosed's origin names no cell of the board's topology.</summary>
    [Refusal(door: "state.transform", condition: "a clearEnclosed's origin names no cell of the board's topology", kind: RefusalKind.Verdict)]
    ClearEnclosedOrigin,

    /// <summary>A clearEnclosed's range is inverted, or it contains the board's empty value.</summary>
    [Refusal(door: "state.transform", condition: "a clearEnclosed's range is inverted, or it contains the board's empty value", kind: RefusalKind.Verdict)]
    ClearEnclosedRange,

    /// <summary>An observe addresses no knowledge board.</summary>
    [Refusal(door: "state.transform", condition: "an observe addresses no knowledge board", kind: RefusalKind.Verdict)]
    ObserveBoard,

    /// <summary>An observe's declared source or visibility mask is not a board over the knowledge board's
    /// topology.</summary>
    [Refusal(door: "state.transform", condition: "an observe's declared source or visibility mask is not a board over the knowledge board's topology", kind: RefusalKind.Verdict)]
    ObserveSources,
}
