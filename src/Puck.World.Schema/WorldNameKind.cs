namespace Puck.World;

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
    /// <summary>A presentation clock in <c>timeline.clocks</c>, independent of state-side names.</summary>
    Clock,
    /// <summary>Any declaration the document makes, whatever its namespace: an entry of a module's export lists.</summary>
    Any,
}
