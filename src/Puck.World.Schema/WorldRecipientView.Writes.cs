using Puck.Commands;
using Puck.Maths;
using Puck.State.Rules;
using Puck.World.Protocol;

namespace Puck.World;

public sealed partial class WorldRecipientView {
    private sealed class WriteScratch {
        public HashSet<string> Rows { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Placements { get; } = new(StringComparer.Ordinal);
        public List<int> Sets { get; } = [];
    }
    // Read-only capabilities are minted during delivery. Writing scratch belongs only to capabilities that write;
    // weak keys keep both its allocation and its lifetime off the ordinary read path.
    [SideTable(reason: "per-view scratch reused across admissions")]
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldRecipientView, WriteScratch> Writes = new();

    /// <summary>Whether this capability belongs to the operator or the world's authored program.</summary>
    public bool IsAuthority => Unrestricted;

    /// <summary>Creates a capability at a trusted, detached composition boundary. Live ingress uses the server's
    /// minted capability so session ceilings and live Observe admission cannot be raised.</summary>
    public static WorldRecipientView ForComposition(WorldDefinition document, Principal actor, StateArena? store = null, ArenaTime? time = null, Func<bool>? operates = null) =>
        new(() => document, () => store ?? WorldStateReader.Arena(document), actor, WorldDisclosureTier.Replica,
            static () => true, new WorldDisclosureImages(), string.Empty, () => time ?? ArenaTime.Origin, operates);

    /// <summary>Admits a row's declared write footprint from policy alone: the row, its token domain, and every row
    /// keyed over it (<see cref="StateCatalog.DependentRows"/>). A write into a keyed row is refused by the transaction
    /// when its key is outside the domain, and a write that removes a domain key is refused when a dependent row still
    /// holds it (<see cref="StateArena.TryValidateRelations(ReadOnlySpan{bool}, out string)"/>), so the write's verdict
    /// reads the current members of each; every one must read whole or the verdict would be an oracle over hidden
    /// members. A declared set admits only if every row its expression reads admits. A name that resolves to no row
    /// and no set is refused exactly as a withheld row is, so the write door never tells a declared row the recipient
    /// cannot read from one that does not exist.</summary>
    /// <remarks>Every write question this view answers is the recipient's declared policy alone
    /// (<see cref="WritesWhole"/>): neither the delivery ceiling nor whether the recipient observes now enters it, so a
    /// session admitted at <see cref="WorldDisclosureTier.Frames"/> writes a public row its grants name, and no refusal
    /// reads observation state.</remarks>
    public bool Admits(string row) {
        if (Unrestricted) { return true; }
        if (!Arena.Catalog.TryResolve(StateLane.Document, row, out var handle)) { return AdmitsSet(row); }
        var ordinal = handle.Ordinal;
        var domain = Arena.Layout[ordinal].DomainOrdinal;
        if (!WritesWhole(ordinal) || ((domain >= 0) && !WritesWhole(domain))) { return false; }
        foreach (var dependent in Arena.Catalog.DependentRows(ordinal)) {
            if (!WritesWhole(dependent)) { return false; }
        }
        return true;
    }
    // The one write-side policy question: whether the row's declarations can withhold any member from this recipient.
    // It is the read door's question without the ceiling and live observation (Includes), which restrict what the
    // recipient's stream carries, never what its policy lets it read.
    private bool WritesWhole(int row) => Observer.ReadsWhole(row);
    /// <summary>Admits an incoming row declaration: the row it replaces (<see cref="Admits(string)"/>, which refuses a
    /// name no row holds, so a recipient replaces a row it reads whole and adds none) and every row the declaration
    /// itself names (its token domain, its live audience, the token and code rows of a derived board, the phase
    /// protocol it follows), since validating the declaration reads their current members. A named row that does not
    /// exist is refused as a withheld one is.</summary>
    /// <param name="declaration">The submitted row.</param>
    /// <param name="row">The first withheld row, or empty.</param>
    public bool AdmitsDeclaration(WorldStateRow declaration, out string row) {
        row = declaration.Name.Value;
        if (Unrestricted) { return true; }
        if (!Admits(row)) { return false; }
        return AdmitsNamed(declaration.Domain is StateDomain.KeysOf keysOf ? keysOf.Row.Value : null, out row) &&
            AdmitsNamed(declaration.Visibility?.ReadersFrom, out row) &&
            AdmitsNamed(declaration.Inverse?.Tokens.Value, out row) &&
            AdmitsNamed(declaration.Inverse?.Codes.Value, out row) &&
            AdmitsNamed(declaration.PhaseOf, out row);
    }
    private bool AdmitsNamed(string? name, out string row) {
        row = name ?? string.Empty;
        return (name is null) || (Arena.Catalog.TryResolve(StateLane.Document, name, out var handle) && WritesWhole(handle.Ordinal));
    }
    // Keep the set-name predicate's capture off successful direct-row admission, including bound guest writes.
    private bool AdmitsSet(string row) {
        if (Source.Sets.FirstOrDefault(set => set.Name.Value == row) is not { } set) { return false; }
        var reads = Writes.GetValue(this, static _ => new()).Sets;
        reads.Clear();
        RuleCompiler.CollectCellSetReads(WorldFactsCompiler.Context(Source), set, reads);
        foreach (var ordinal in reads) {
            if (!WritesWhole(ordinal)) { return false; }
        }
        return true;
    }

    /// <summary>Checks every row a transform touches once resolved, the rows its subjects do not name included (a
    /// live key's row, a zone's token domain, a pool's fields), since the arena's audience check reads their current
    /// cells before the kernel runs. A transform that does not resolve adds nothing here: its refusal comes from the
    /// declarations alone, at composition.</summary>
    /// <param name="transform">The authored transform.</param>
    /// <returns><see langword="true"/> when every resolved row reads whole, or the transform does not resolve.</returns>
    /// <remarks>The rows checked here are derived from the document, never named by the submitter, so this answers no
    /// row name for a refusal to carry.</remarks>
    public bool AdmitsTransform(StateTransform transform) {
        if (Unrestricted) { return true; }
        var rows = Writes.GetValue(this, static _ => new()).Sets;
        rows.Clear();
        if (!RuleCompiler.TryCollectTransformRows(transform, WorldFactsCompiler.Context(Source), rows, out _)) { return true; }
        foreach (var ordinal in rows) {
            if (!WritesWhole(ordinal)) { return false; }
        }
        return true;
    }

    /// <summary>The one write refusal, independent of verb, key, membership, value and grants. <paramref name="row"/> is
    /// a name the submission itself spelled (the mutation adapter, <c>WorldRecipientMutations.Admits</c>, names nothing else), so the
    /// text carries nothing the submitter did not already hold.</summary>
    public string Refusal(string verb, string row, string? key = null) => $"'{row}' is not disclosed to {Recipient?.Describe() ?? "public"}";

    /// <summary>Checks every state-bound document value through the same row policy.</summary>
    public bool AdmitsValues(object graph, out string row) {
        row = string.Empty;
        if (Unrestricted) { return true; }
        var reads = Writes.GetValue(this, static _ => new()).Rows;
        reads.Clear();
        WorldStateDocumentValues.CollectReferencedRows(graph, reads);
        foreach (var name in reads) {
            if (!Admits(name)) { row = name; return false; }
        }
        return true;
    }

    /// <summary>Checks a definition dependency before fingerprinting it. A recipient's dependency names its rows: a
    /// guard over the whole document would be admitted or refused by whether any declaration withholds from the
    /// recipient, which tells a world with a withheld row from one without it, so it is refused by its shape, named as
    /// the state section, whatever the document declares.</summary>
    /// <param name="rows">The selected rows, or <see langword="null"/> for the whole document.</param>
    /// <param name="row">The first refused name the submission spelled, or <c>state</c> for a whole-document guard.</param>
    /// <returns><see langword="true"/> when every selected row admits.</returns>
    public bool AdmitsDefinition(IReadOnlyList<string>? rows, out string row) {
        row = string.Empty;
        if (Unrestricted) { return true; }
        if (rows is null) { row = WholeDocument; return false; }
        foreach (var name in rows) { if (!Admits(name)) { row = name; return false; } }
        return true;
    }
    // The name a refused whole-document guard answers under: the section it reads, never a declaration in it.
    private const string WholeDocument = "state";

    /// <summary>Checks the declared state dependencies, deal sources and parents included, of every placement any
    /// bounded spatial read could observe (<see cref="WorldDefinitionFingerprint.EntersSpatialCensus"/>). Which
    /// placements one region holds can itself depend on state (a bound centre or extent, a parent's bound frame), so
    /// the answer takes no region: a refusal never tells the recipient where withheld geometry lies.</summary>
    /// <returns><see langword="true"/> when every census-eligible placement's dependencies are admitted.</returns>
    public bool AdmitsSpatial() {
        if (Unrestricted) { return true; }
        foreach (var placement in Source.Placements) {
            if (WorldDefinitionFingerprint.EntersSpatialCensus(placement) && !AdmitsPlacement(placement)) { return false; }
        }
        return true;
    }

    /// <summary>Checks a named placement's dependencies without reading its current values, for a removal or a read
    /// of it: the installed placement's footprint (see <see cref="AdmitsPlacement(WorldPlacement)"/>).</summary>
    /// <param name="id">The placement id, which may name no placement.</param>
    /// <returns><see langword="true"/> when every row the footprint holds reads whole.</returns>
    public bool AdmitsPlacement(string id) => AdmitsPlacementWrite(incoming: null, id, out _);
    /// <summary>Checks an upsert of a placement: the incoming declaration's state inputs and parent chain, and the
    /// installed placement under the same id, which the upsert replaces.</summary>
    /// <param name="placement">The incoming placement.</param>
    /// <returns><see langword="true"/> when every row the footprint holds reads whole.</returns>
    public bool AdmitsPlacement(WorldPlacement placement) => AdmitsPlacementWrite(placement, placement.Id, out _);
    // The one placement admission. The installed placement under the id is admitted whether the write replaces or
    // removes it: its state inputs (its deal and variants rows, whose membership decides which dealt children exist,
    // present or absent), its parent chain's, and the template every id on that chain spells as a dealt child. Composition
    // keeps the installed placement's dealt children and validation judges them against what replaces it, so a check
    // over only the incoming declaration would let that verdict read the hidden deal. A batch composes members without
    // validating between them, so a chain may close on itself; each id is checked once and the walk ends there.
    private bool AdmitsPlacementWrite(WorldPlacement? incoming, string id, out string row) {
        row = string.Empty;
        if (Unrestricted) { return true; }
        var visited = Writes.GetValue(this, static _ => new()).Placements;
        visited.Clear();
        if ((incoming is not null) && !AdmitsInputs(incoming, out row)) { return false; }
        if (!AdmitsChain(id, visited, out row)) { return false; }
        return (incoming?.Parent is not { } parent) || AdmitsChain(parent, visited, out row);
    }

    /// <summary>Checks a machine write: the rows the incoming declaration's memory bindings read or write and those of
    /// the installed machine it replaces or removes. A binding runs as the world's authority, copying a cell into
    /// hardware a screen may show or hardware values into a cell, so each bound row is the write's footprint.</summary>
    /// <param name="incoming">The incoming machine, or <see langword="null"/> for a removal.</param>
    /// <param name="name">The machine's name.</param>
    /// <returns><see langword="true"/> when every bound row admits.</returns>
    public bool AdmitsMachine(WorldMachine? incoming, string name) {
        if (Unrestricted) { return true; }
        return AdmitsBindings(incoming, out _) && AdmitsBindings(WorldDefinitionRows.FindMachine(Source.Machines, name), out _);
    }
    private bool AdmitsBindings(WorldMachine? machine, out string row) {
        row = string.Empty;
        foreach (var binding in machine?.Memory ?? []) {
            if ((binding?.Row is { } bound) && !Admits(bound)) { row = bound; return false; }
        }
        return true;
    }

    /// <summary>Checks a kit write: the rows the incoming kit's actions redraw (<see cref="ActionEffect.Generate"/>, the one
    /// world-row effect a kit admits, fired as the world's authority) and those of the installed kit it replaces or
    /// removes.</summary>
    /// <param name="incoming">The incoming kit, or <see langword="null"/> for a removal.</param>
    /// <param name="name">The kit's name.</param>
    /// <returns><see langword="true"/> when every redrawn row admits.</returns>
    public bool AdmitsKit(WorldKit? incoming, string name) {
        if (Unrestricted) { return true; }
        return AdmitsGenerates(incoming, out _) && AdmitsGenerates(WorldDefinitionRows.FindKit(Source.Kits, name), out _);
    }
    private bool AdmitsGenerates(WorldKit? kit, out string row) {
        row = string.Empty;
        if (kit is null) { return true; }
        foreach (var action in kit.Actions.Values) {
            if (!AdmitsGenerates(action.OnPress?.Effects, out row) || !AdmitsGenerates(action.OnRelease?.Effects, out row)) { return false; }
            foreach (var trigger in action.OnFact ?? []) {
                if (!AdmitsGenerates(trigger.Effects, out row)) { return false; }
            }
        }
        return true;
    }
    private bool AdmitsGenerates(IReadOnlyList<ActionEffect>? effects, out string row) {
        row = string.Empty;
        foreach (var effect in effects ?? []) {
            if ((effect is ActionEffect.Generate { Row.Name: { } generated }) && !Admits(generated)) { row = generated; return false; }
        }
        return true;
    }
    private bool AdmitsTemplateOf(string id, HashSet<string> visited, out string row) {
        row = string.Empty;
        return !WorldPlacementDeal.TrySplitChildId(id, out var template, out _) || AdmitsChain(template, visited, out row);
    }
    // Walks ids, not placements: an id that names no placement still depends on the template it spells.
    private bool AdmitsChain(string? id, HashSet<string> visited, out string row) {
        row = string.Empty;
        while (id is not null && visited.Add(id)) {
            if (!AdmitsTemplateOf(id, visited, out row)) { return false; }
            if (WorldDefinitionRows.FindPlacement(Source.Placements, id) is not { } placement) { return true; }
            if (!AdmitsInputs(placement, out row)) { return false; }
            id = placement.Parent;
        }
        return true;
    }
    private bool AdmitsInputs(WorldPlacement placement, out string row) {
        row = string.Empty;
        var reads = Writes.GetValue(this, static _ => new()).Rows;
        reads.Clear();
        WorldDefinitionFingerprint.CollectPlacementStateInputs(placement, reads);
        foreach (var name in reads) { if (!Admits(name)) { row = name; return false; } }
        return true;
    }

    /// <summary>Hashes only this capability's definition at its ceiling.</summary>
    public string Fingerprint(IReadOnlyList<string>? rows = null) => WorldDefinitionFingerprint.Compute(Definition, rows);
    /// <summary>Hashes named inputs from this capability.</summary>
    public string Fingerprint(WorldDefinitionReadDependency inputs) => WorldDefinitionFingerprint.ComputeInputs(Definition, inputs.PlacementIds, inputs.StateRows);
    /// <summary>Hashes a spatial census from this capability's placements and rows.</summary>
    public string Fingerprint(in FixedAabb region) => WorldDefinitionFingerprint.ComputeSpatial(Definition, region);
    /// <summary>Checks a numeric/text guard against the live value this capability discloses.</summary>
    public bool Fingerprint(WorldStateExpectation cell) {
        if (!Discloses(cell.Row, cell.Key) || !WorldStateReader.TryRead(Source, cell.Row, cell.Key, Time.Tick, Time.EngineTick,
            out var row, out var raw, out var text, Arena) || raw is not { } value) { return false; }
        return (cell.Kind is not { } kind || row.Kind == kind) && (cell.Text is null || text == cell.Text) &&
            (row.Kind == CellKind.Text || cell.Kind == CellKind.Text || cell.Comparison.Holds(
                Puck.Maths.FixedQ4816.FromRawBits(value), Puck.Maths.FixedQ4816.FromRawBits(cell.Value)));
    }
}
