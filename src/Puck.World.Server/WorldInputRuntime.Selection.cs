using Puck.Abstractions.Collections;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldInputRuntime {
    // What the installed document's selection reads from the arena, resolved once per document, input catalog and
    // register binding: the counter slot each body-state selector family reads (-1 for every other family, and for a
    // name the document declares no counter for), and the arena rows those registers and the kit assignment's
    // selector register are stored in. An owner's selection reads only its own body's cells of those rows, so a state
    // transaction moves the selection of exactly the owners whose bodies' cells it touched.
    private WorldDefinition? m_planDefinition;
    private WorldInputCatalog? m_planCatalog;
    private int m_planBinding = -1;
    private int[] m_selectorSlots = [];
    private int[] m_selectionRows = [];

    private readonly List<int> m_touchedBodies = [];
    private readonly MarkedIndexSet m_touchedSeen = new();
    private readonly List<Owner> m_touchedOwners = [];

    /// <summary>Collects the bodies whose selection registers the open state transaction touched: a body-state
    /// selector's counter or the kit assignment's selector register, written for that body or born with it.</summary>
    /// <param name="writtenRows">The catalog ordinals of the rows the open transaction wrote.</param>
    /// <returns>The touched bodies, each once, valid until the next call; empty when the transaction moves no owner's
    /// selection.</returns>
    public IReadOnlyList<int> TouchedSelectionBodies(IReadOnlyList<int> writtenRows) {
        ArgumentNullException.ThrowIfNull(argument: writtenRows);
        m_touchedBodies.Clear();
        if (m_ordered.Count == 0) { return m_touchedBodies; }
        EnsureSelectionPlan();
        var touched = false;

        foreach (var row in m_selectionRows) {
            for (var index = 0; (index < writtenRows.Count); index++) { touched |= (writtenRows[index] == row); }
        }
        if (!touched) { return m_touchedBodies; }
        m_host.Arena.CollectOpenLaneOrdinals(ordinals: m_touchedBodies, rows: m_selectionRows);
        m_touchedSeen.Clear();
        var kept = 0;

        for (var index = 0; (index < m_touchedBodies.Count); index++) {
            var body = m_touchedBodies[index];

            if (m_touchedSeen.Add(index: body)) { m_touchedBodies[kept++] = body; }
        }
        m_touchedBodies.RemoveRange(count: (m_touchedBodies.Count - kept), index: kept);
        return m_touchedBodies;
    }
    /// <summary>Prepares the selections of the owners routing <paramref name="bodies"/> while the state's journal can
    /// still rewind: each owner's candidate mapping is selected and held for <see cref="ApplyPreparedBodies"/>. Changes
    /// no input state.</summary>
    /// <param name="bodies">The bodies whose selection registers moved, each once.</param>
    /// <param name="reason">Why a selection is refused, or empty.</param>
    /// <returns><see langword="true"/> when every such owner selects an admitted tuple.</returns>
    public bool TryPrepareBodies(IReadOnlyList<int> bodies, out string reason) {
        foreach (var owner in OwnersOf(bodies: bodies)) {
            if (!TryPrepare(owner: owner, reason: out reason)) { return false; }
        }
        reason = string.Empty;
        return true;
    }
    /// <summary>Prepares the selection of every admitted owner, as <see cref="TryPrepareBodies"/> does for the owners of
    /// some bodies; the prelude to a gate change that resynchronizes them all.</summary>
    /// <param name="reason">Why a selection is refused, or empty.</param>
    /// <returns><see langword="true"/> when every owner selects an admitted tuple.</returns>
    public bool TryPrepareAll(out string reason) {
        foreach (var owner in m_ordered) {
            if (!TryPrepare(owner: owner, reason: out reason)) { return false; }
        }
        reason = string.Empty;
        return true;
    }
    /// <summary>Applies the selections <see cref="TryPrepareBodies"/> prepared to the owners routing
    /// <paramref name="bodies"/> once the state change has committed, in the principal order
    /// <see cref="ApplyPreparedAll"/> visits. It cannot refuse: every selection was admitted when it was prepared.</summary>
    /// <param name="bodies">The bodies whose selection registers moved, each once.</param>
    /// <exception cref="InvalidOperationException">An owner has no prepared selection: the transaction committed
    /// without its preparing phase, a defect in the committer and not a refusal.</exception>
    public void ApplyPreparedBodies(IReadOnlyList<int> bodies) {
        foreach (var owner in OwnersOf(bodies: bodies)) { ApplyPrepared(owner: owner); }
    }
    /// <summary>Applies the selection <see cref="TryPrepareAll"/> prepared to every admitted owner, as
    /// <see cref="ApplyPreparedBodies"/> does for the owners of some bodies.</summary>
    /// <exception cref="InvalidOperationException">An owner has no prepared selection.</exception>
    public void ApplyPreparedAll() {
        foreach (var owner in m_ordered) { ApplyPrepared(owner: owner); }
    }

    private bool TryPrepare(Owner owner, out string reason) {
        if (!TrySelect(owner, out var mapping, out reason)) { return false; }
        owner.Prepared = mapping;
        return true;
    }
    private void ApplyPrepared(Owner owner) {
        var mapping = (owner.Prepared ?? throw new InvalidOperationException(message: "A state transaction applied an input selection it never prepared."));

        owner.Prepared = null;
        RefreshPermissions(owner: owner);
        SynchronizeTo(activation: 0, mapping: mapping, owner: owner);
    }
    private List<Owner> OwnersOf(IReadOnlyList<int> bodies) {
        m_touchedOwners.Clear();
        foreach (var body in bodies) {
            if (m_bodyOwners.TryGetValue(key: body, value: out var owners)) { m_touchedOwners.AddRange(collection: owners); }
        }
        m_touchedOwners.Sort(comparison: static (a, b) => ComparePrincipal(a: a.Principal, b: b.Principal));
        return m_touchedOwners;
    }
    private int SelectorSlot(int family) {
        EnsureSelectionPlan();
        return m_selectorSlots[family];
    }
    private void EnsureSelectionPlan() {
        var definition = m_host.Document.Definition;
        var catalog = m_host.Population.InputCatalog;
        var lane = m_host.Population.ActionStateLane;

        if (ReferenceEquals(objA: m_planDefinition, objB: definition) && ReferenceEquals(objA: m_planCatalog, objB: catalog) &&
            (m_planBinding == lane.Binding)) { return; }
        var slots = new int[catalog.Selectors.Length];
        var rows = new List<int>();

        void Read(string? register, out int counterSlot) {
            counterSlot = -1;
            if ((register is null) || !WorldBodyStateReads.TryResolveSlot(definition: definition, kind: out var kind, name: register, slot: out var slot)) { return; }
            if (kind == ActionStateKind.Counter) { counterSlot = slot; }
            var row = lane.RowOrdinal(slot: slot);

            if ((row >= 0) && !rows.Contains(item: row)) { rows.Add(item: row); }
        }
        for (var family = 0; (family < slots.Length); family++) {
            var selector = catalog.Selectors[family];

            slots[family] = -1;
            if (selector.Kind == WorldInputModeSelectorKind.BodyState) { Read(register: selector.Name, counterSlot: out slots[family]); }
        }
        Read(register: definition.Assignment.Select?.BodyState, counterSlot: out _);
        m_selectorSlots = slots;
        m_selectionRows = [.. rows];
        m_planDefinition = definition;
        m_planCatalog = catalog;
        m_planBinding = lane.Binding;
    }
}
