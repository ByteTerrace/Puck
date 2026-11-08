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

    /// <summary>Admits a row from declared policy alone. Absent declarations carry no restriction; ordinary
    /// composition diagnoses them. A declared set admits only if every row its expression reads admits. A row keyed
    /// over a token domain admits only if the domain reads whole too: the transaction refuses a key outside the
    /// domain (<see cref="StateArena.TryValidateRelations(out string)"/>), so a write's outcome reads the domain's membership.</summary>
    public bool Admits(string row) {
        if (Unrestricted) { return true; }
        if (Arena.Catalog.TryResolve(StateLane.Document, row, out var handle)) {
            var domain = Arena.Layout[handle.Ordinal].DomainOrdinal;
            return ReadsWhole(handle.Ordinal) && ((domain < 0) || ReadsWhole(domain));
        }
        return AdmitsSet(row);
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

    /// <summary>Checks a named placement's dependencies without reading its current values. An id spelling a dealt
    /// child (<see cref="WorldPlacementDeal.TrySplitChildId"/>) depends on its template whether or not the child exists
    /// now, since whether it exists is the deal row's membership.</summary>
    public bool AdmitsPlacement(string id, out string row) {
        row = string.Empty;
        if (Unrestricted) { return true; }
        var visited = Writes.GetValue(this, static _ => new()).Placements;
        visited.Clear();
        return AdmitsChain(WorldDefinitionRows.FindPlacement(Source.Placements, id), visited, out row) && AdmitsTemplateOf(id, visited, out row);
    }
    /// <summary>Checks an incoming placement's complete state dependency declaration and its parent chain's, and the
    /// template its id spells as a dealt child. A batch composes members without validating between them, so the chain
    /// may close on itself; each placement is checked once and the walk ends there.</summary>
    public bool AdmitsPlacement(WorldPlacement placement, out string row) {
        row = string.Empty;
        if (Unrestricted) { return true; }
        var visited = Writes.GetValue(this, static _ => new()).Placements;
        visited.Clear();
        return AdmitsChain(placement, visited, out row) && AdmitsTemplateOf(placement.Id, visited, out row);
    }
    private bool AdmitsTemplateOf(string id, HashSet<string> visited, out string row) {
        row = string.Empty;
        return !WorldPlacementDeal.TrySplitChildId(id, out var template, out _) ||
            AdmitsChain(WorldDefinitionRows.FindPlacement(Source.Placements, template), visited, out row);
    }
    private bool AdmitsChain(WorldPlacement? next, HashSet<string> visited, out string row) {
        row = string.Empty;
        var reads = Writes.GetValue(this, static _ => new()).Rows;
        while (next is not null && visited.Add(next.Id)) {
            reads.Clear();
            WorldDefinitionFingerprint.CollectPlacementStateInputs(next, reads);
            foreach (var name in reads) { if (!Admits(name)) { row = name; return false; } }
            next = next.Parent is { } parent ? WorldDefinitionRows.FindPlacement(Source.Placements, parent) : null;
        }
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
