namespace Puck.Abstractions.Documents;

/// <summary>The namespace a document name belongs to — which declaration a name-bearing field resolves against.</summary>
public enum WorldNameKind : byte {
    /// <summary>A record type in the state section.</summary>
    Record,
    /// <summary>A bounded pool in the state section.</summary>
    Pool,
    /// <summary>A <c>state.world</c> row.</summary>
    State,
    /// <summary>An ordered zone: a <c>state.world</c> row a rule's zone table, a transfer, or a search job names as a pile.</summary>
    Zone,
    /// <summary>A <c>rules</c> row.</summary>
    Rule,
    /// <summary>A <c>tables</c> row.</summary>
    Table,
    /// <summary>A <c>patterns</c> row.</summary>
    Pattern,
    /// <summary>A <c>ruleGroups</c> row.</summary>
    RuleGroup,
    /// <summary>A <c>sets</c> row.</summary>
    CellSet,
    /// <summary>A set of positions: a <c>state.world</c> board row or a <c>sets</c> row, whichever declares the name.
    /// The two namespaces never share a name.</summary>
    Positions,
    /// <summary>A <c>state.lattices</c> topology.</summary>
    Topology,
    /// <summary>A <c>generators</c> row.</summary>
    Generator,
    /// <summary>A <c>fields</c> row.</summary>
    Field,
    /// <summary>A <c>dynamics</c> row.</summary>
    Dynamics,
    /// <summary>A named machine instance.</summary>
    Machine,
    /// <summary>A named screen declaration.</summary>
    Screen,
    /// <summary>A hardware binding local to a machine instance.</summary>
    MachineBinding,
    /// <summary>A <c>placements</c> row: its own namespace, apart from every state-side name.</summary>
    Placement,
    /// <summary>A <c>prototypes</c> row: its own namespace, apart from every state-side name.</summary>
    Prototype,
    /// <summary>A <c>timeline</c> clock: its own namespace, so a state clock may share its row's name.</summary>
    Clock,
    /// <summary>A <c>screenMaterials</c> row: its own namespace, apart from every state-side name, which a screen row
    /// and a creation face name by id.</summary>
    ScreenMaterial,
    /// <summary>Any declaration the document makes, whatever its namespace: an entry of a module's export lists.</summary>
    Any,
}
/// <summary>How a document field carries a name.</summary>
public enum WorldNameRole : byte {
    /// <summary>The field mints the name: the row's own <c>name</c>.</summary>
    Declares,
    /// <summary>The field holds one name of its kind, or a reserved <c>$</c> channel whose colon-separated segments
    /// hold names; a list-typed field holds one per element.</summary>
    Names,
    /// <summary>The field is a cell key in the dynamic-key grammar: a literal key, or a <c>$cell:</c>/<c>$zone:</c>/
    /// <c>$expr:</c>/<c>cell:</c> spelling whose segments or expression hold names.</summary>
    Key,
    /// <summary>The field is a value expression, infix text or postfix tokens, whose state reads and topology
    /// arguments are names.</summary>
    Expression,
    /// <summary>The field is a <c>state.&lt;row&gt;[.&lt;key&gt;]</c> binding token.</summary>
    Binding,
    /// <summary>The field is a HUD template whose <c>{…}</c> placeholders are binding tokens.</summary>
    Template,
    /// <summary>A body selector such as <c>cell:row:key</c>, <c>argmax:row</c>, or <c>placement:id</c>.</summary>
    BodyReference,
}
/// <summary>The module export list that admits a host reference through this property.</summary>
public enum WorldExportFacet : byte {
    /// <summary>The field reads the name: a gate, a comparand, an expression operand, a key indirection, a domain,
    /// a zone table, a placement's board topology. Admitted by <c>Reads</c>.</summary>
    Read,
    /// <summary>The field drives the name: a state effect's destination, a transform's target row or zone, a
    /// generate row, a field write, a placement's occupancy row. Admitted by <c>Actions</c>.</summary>
    Action,
    /// <summary>The field binds presentation to the name: a HUD element, an overlay predicate, a camera program
    /// scalar, a binding wheel or bar row. Admitted by <c>Bindings</c>.</summary>
    Binding,
}
/// <summary>Declares the namespace, spelling, and import access of a document property.</summary>
/// <param name="kind">The namespace the property names.</param>
/// <param name="role">How the property spells its names.</param>
/// <param name="facet">The export list that admits a reference.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DocumentNameAttribute(WorldNameKind kind, WorldNameRole role, WorldExportFacet facet = WorldExportFacet.Read) : Attribute {
    /// <summary>Gets the referenced namespace.</summary>
    public WorldNameKind Kind { get; } = kind;
    /// <summary>Gets the spelling.</summary>
    public WorldNameRole Role { get; } = role;
    /// <summary>Gets the export list.</summary>
    public WorldExportFacet Facet { get; } = facet;
}

/// <summary>Identifies literal text or a name in a vocabulary outside module qualification.</summary>
/// <param name="purpose">What the property holds, including the vocabulary of a local name.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DocumentTextAttribute(string purpose) : Attribute {
    /// <summary>Gets the meaning of this text in its document.</summary>
    public string Purpose { get; } = purpose;
}
