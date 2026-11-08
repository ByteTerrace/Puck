using Puck.Commands;
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
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldRecipientView, WriteScratch> Writes = new();

    /// <summary>Whether this capability belongs to the operator or the world's authored program.</summary>
    public bool IsAuthority => Unrestricted;

    /// <summary>Creates a capability at a trusted, detached composition boundary. Live ingress uses the server's
    /// minted capability so session ceilings and live Observe admission cannot be raised.</summary>
    public static WorldRecipientView ForComposition(WorldDefinition document, Principal actor, StateArena? store = null, ArenaTime? time = null) =>
        new(() => document, () => store ?? WorldStateReader.Arena(document), actor, WorldDisclosureTier.Replica,
            static () => true, new WorldDisclosureImages(), string.Empty, () => time ?? ArenaTime.Origin);

    /// <summary>Admits a row's declared write footprint from policy alone: the row, its token domain, and every row
    /// keyed over it (<see cref="StateCatalog.DependentRows"/>). A write into a keyed row is refused by the transaction
    /// when its key is outside the domain, and a write that removes a domain key is refused when a dependent row still
    /// holds it (<see cref="StateArena.TryValidateRelations(ReadOnlySpan{bool}, out string)"/>), so the write's verdict
    /// reads the current members of each; every one must read whole or the verdict would be an oracle over hidden
    /// members. Absent declarations carry no restriction; ordinary composition diagnoses them. A declared set admits
    /// only if every row its expression reads admits.</summary>
    public bool Admits(string row) {
        if (Unrestricted) { return true; }
        if (!Arena.Catalog.TryResolve(StateLane.Document, row, out var handle)) { return AdmitsSet(row); }
        var ordinal = handle.Ordinal;
        var domain = Arena.Layout[ordinal].DomainOrdinal;
        if (!ReadsWhole(ordinal) || ((domain >= 0) && !ReadsWhole(domain))) { return false; }
        foreach (var dependent in Arena.Catalog.DependentRows(ordinal)) {
            if (!ReadsWhole(dependent)) { return false; }
        }
        return true;
    }
    /// <summary>Admits an incoming row declaration: the row it replaces or adds (<see cref="Admits(string)"/>) and every
    /// row the declaration itself names (its token domain, its live audience, the token and code rows of a derived
    /// board, the phase protocol it follows), since validating the declaration reads their current members whether or
    /// not the row exists yet. A name that resolves to no row adds nothing; composition diagnoses it.</summary>
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
        return (name is null) || !Arena.Catalog.TryResolve(StateLane.Document, name, out var handle) || ReadsWhole(handle.Ordinal);
    }
    // Keep the set-name predicate's capture off successful direct-row admission, including bound guest writes.
    private bool AdmitsSet(string row) {
        if (Source.Sets.FirstOrDefault(set => set.Name.Value == row) is not { } set) { return true; }
        var reads = Writes.GetValue(this, static _ => new()).Sets;
        reads.Clear();
        RuleCompiler.CollectCellSetReads(WorldFactsCompiler.Context(Source), set, reads);
        foreach (var ordinal in reads) {
            if (!ReadsWhole(ordinal)) { return false; }
        }
        return true;
    }

    /// <summary>Checks every row a transform touches once resolved, the rows its subjects do not name included (a
    /// live key's row, a zone's token domain, a pool's fields), since the arena's audience check reads their current
    /// cells before the kernel runs. A transform that does not resolve adds nothing here: its refusal comes from the
    /// declarations alone, at composition.</summary>
    /// <param name="transform">The authored transform.</param>
    /// <param name="row">The first withheld row, or empty.</param>
    /// <returns><see langword="true"/> when every resolved row reads whole, or the transform does not resolve.</returns>
    public bool AdmitsTransform(StateTransform transform, out string row) {
        row = string.Empty;
        if (Unrestricted) { return true; }
        var rows = Writes.GetValue(this, static _ => new()).Sets;
        rows.Clear();
        if (!RuleCompiler.TryCollectTransformRows(transform, WorldFactsCompiler.Context(Source), rows, out _)) { return true; }
        foreach (var ordinal in rows) {
            if (!ReadsWhole(ordinal)) { row = Arena.Rows[ordinal].Name.Value; return false; }
        }
        return true;
    }

    /// <summary>The one write refusal, independent of verb, key, membership, value and grants.</summary>
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

    /// <summary>Checks a complete or selected definition dependency before fingerprinting it.</summary>
    public bool AdmitsDefinition(IReadOnlyList<string>? rows, out string row) {
        row = string.Empty;
        if (Unrestricted) { return true; }
        if (rows is not null) {
            foreach (var name in rows) { if (!Admits(name)) { row = name; return false; } }
        } else {
            foreach (var declaration in Source.State) {
                if (!Admits(declaration.Name.Value)) { row = declaration.Name.Value; return false; }
            }
        }
        return true;
    }

    /// <summary>Checks the declared state dependencies, deal sources and parents included, of every placement any
    /// bounded spatial read could observe (<see cref="WorldDefinitionFingerprint.EntersSpatialCensus"/>). Which
    /// placements one region holds can itself depend on state (a bound centre or extent, a parent's bound frame), so
    /// the answer takes no region: a refusal never tells the recipient where withheld geometry lies.</summary>
    /// <param name="row">The first withheld row, or empty when every dependency is admitted.</param>
    /// <returns><see langword="true"/> when every census-eligible placement's dependencies are admitted.</returns>
    public bool AdmitsSpatial(out string row) {
        row = string.Empty;
        if (Unrestricted) { return true; }
        foreach (var placement in Source.Placements) {
            if (WorldDefinitionFingerprint.EntersSpatialCensus(placement) && !AdmitsPlacement(placement, out row)) { return false; }
        }
        return true;
    }

    /// <summary>Checks a named placement's dependencies without reading its current values, for a removal or a read
    /// of it: the installed placement's footprint (see <see cref="AdmitsPlacement(WorldPlacement, out string)"/>).</summary>
    /// <param name="id">The placement id, which may name no placement.</param>
    /// <param name="row">The first withheld row, or empty.</param>
    /// <returns><see langword="true"/> when every row the footprint holds reads whole.</returns>
    public bool AdmitsPlacement(string id, out string row) => AdmitsPlacementWrite(incoming: null, id, out row);
    /// <summary>Checks an upsert of a placement: the incoming declaration's state inputs and parent chain, and the
    /// installed placement under the same id, which the upsert replaces.</summary>
    /// <param name="placement">The incoming placement.</param>
    /// <param name="row">The first withheld row, or empty.</param>
    /// <returns><see langword="true"/> when every row the footprint holds reads whole.</returns>
    public bool AdmitsPlacement(WorldPlacement placement, out string row) => AdmitsPlacementWrite(placement, placement.Id, out row);
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
    /// <param name="row">The first withheld row, or empty.</param>
    /// <returns><see langword="true"/> when every bound row admits.</returns>
    public bool AdmitsMachine(WorldMachine? incoming, string name, out string row) {
        row = string.Empty;
        if (Unrestricted) { return true; }
        return AdmitsBindings(incoming, out row) && AdmitsBindings(WorldDefinitionRows.FindMachine(Source.Machines, name), out row);
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
    /// <param name="row">The first withheld row, or empty.</param>
    /// <returns><see langword="true"/> when every redrawn row admits.</returns>
    public bool AdmitsKit(WorldKit? incoming, string name, out string row) {
        row = string.Empty;
        if (Unrestricted) { return true; }
        return AdmitsGenerates(incoming, out row) && AdmitsGenerates(WorldDefinitionRows.FindKit(Source.Kits, name), out row);
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
    public string Fingerprint(in WorldSpatialReadRegion region) => WorldDefinitionFingerprint.ComputeSpatial(Definition, region);
    /// <summary>Checks a numeric/text guard against the live value this capability discloses.</summary>
    public bool Fingerprint(WorldStateExpectation cell) {
        if (!Discloses(cell.Row, cell.Key) || !WorldStateReader.TryRead(Source, cell.Row, cell.Key, Time.Tick, Time.EngineTick,
            out var row, out var raw, out var text, Arena) || raw is not { } value) { return false; }
        return (cell.Kind is not { } kind || row.Kind == kind) && (cell.Text is null || text == cell.Text) &&
            (row.Kind == CellKind.Text || cell.Kind == CellKind.Text || cell.Comparison.Holds(
                Puck.Maths.FixedQ4816.FromRawBits(value), Puck.Maths.FixedQ4816.FromRawBits(cell.Value)));
    }
}
