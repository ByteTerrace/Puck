using Puck.Commands;
using Puck.Maths;
using Puck.State.Rules;
using Puck.World.Protocol;

namespace Puck.World;

public sealed partial class WorldRecipientView {
    private sealed class WriteScratch {
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

    /// <summary>Admits a row's declared write footprint from policy alone: the row, its token domain, every row keyed
    /// over it (<see cref="StateCatalog.DependentRows"/>), and every state name spelled by the row's own declaration and
    /// by every declaration that spells the row (<see cref="WorldRowDeclarationNames"/>: a live audience, a derived
    /// board's token and code rows, a knowledge row's source, mask and positions, a phase protocol, whatever position
    /// the name registry lists). A write's checks read the current members of each (a key outside a domain, a domain
    /// key a dependent still holds, a board settled from its tokens, a knowledge row copied from its source), so every
    /// one must read whole or the verdict would be an oracle over hidden members. A declared set admits only if every
    /// row its expression reads admits. A name that resolves to no row and no set is refused exactly as a withheld row
    /// is, so the write door never tells a declared row the recipient cannot read from one that does not exist.</summary>
    /// <remarks>Every write question this view answers is the recipient's declared policy alone
    /// (<see cref="WritesWhole"/>): neither the delivery ceiling nor whether the recipient observes now enters it, so a
    /// session admitted at <see cref="WorldDisclosureTier.Frames"/> writes a public row its grants name, and no refusal
    /// reads observation state. The relation is the rows' declarations, never their cells, so it is the same in two
    /// worlds that differ only in withheld members.</remarks>
    /// <param name="row">The row or set name.</param>
    /// <returns><see langword="true"/> when the whole footprint reads whole.</returns>
    public bool Admits(string row) {
        if (Unrestricted) { return true; }
        if (!Arena.Catalog.TryResolve(StateLane.Document, row, out var handle)) { return AdmitsSet(row); }
        var ordinal = handle.Ordinal;
        var domain = Arena.Layout[ordinal].DomainOrdinal;
        if (!WritesWhole(ordinal) || ((domain >= 0) && !WritesWhole(domain))) { return false; }
        foreach (var dependent in Arena.Catalog.DependentRows(ordinal)) {
            if (!WritesWhole(dependent)) { return false; }
        }
        var declared = WorldRowDeclarationNames.Of(Source);
        foreach (var spelling in declared.SpelledBy(row)) {
            foreach (var name in declared.Spells(spelling)) {
                if (!WritesWholeName(name)) { return false; }
            }
        }
        return true;
    }
    // The one write-side policy question: whether the row's declarations can withhold any member from this recipient.
    // It is the read door's question without the ceiling and live observation (Includes), which restrict what the
    // recipient's stream carries, never what its policy lets it read.
    private bool WritesWhole(int row) => Observer.ReadsWhole(row);
    // A name a declaration spells, asked without its own footprint: a row reads whole, a set's every read row does,
    // and a name that resolves to neither is refused as a withheld one is.
    private bool WritesWholeName(string name) => Arena.Catalog.TryResolve(StateLane.Document, name, out var handle)
        ? WritesWhole(handle.Ordinal) : AdmitsSet(name);
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
    /// cells before the kernel runs. A transform that does not resolve is refused: its resolution may already have read
    /// the row it stopped at, so only a resolved transform's rows can be admitted.</summary>
    /// <param name="transform">The authored transform.</param>
    /// <returns><see langword="true"/> when the transform resolves and every resolved row reads whole.</returns>
    /// <remarks>The rows checked here are derived from the document, never named by the submitter, so this answers no
    /// row name for a refusal to carry. Every row the transform spells is admitted by name before this runs.</remarks>
    public bool AdmitsTransform(StateTransform transform) {
        if (Unrestricted) { return true; }
        var rows = Writes.GetValue(this, static _ => new()).Sets;
        rows.Clear();
        if (!RuleCompiler.TryCollectTransformRows(transform, WorldFactsCompiler.Context(Source), rows, out _)) { return false; }
        foreach (var ordinal in rows) {
            if (!WritesWhole(ordinal)) { return false; }
        }
        return true;
    }

    /// <summary>The one write refusal, independent of verb, key, membership, value and grants. <paramref name="row"/> is
    /// a name the submission itself spelled (the mutation adapter, <c>WorldRecipientMutations.Admits</c>, names nothing else), so the
    /// text carries nothing the submitter did not already hold.</summary>
    public string Refusal(string verb, string row, string? key = null) => $"'{row}' is not disclosed to {Recipient?.Describe() ?? "public"}";

    /// <summary>Checks a definition dependency before fingerprinting it. A guard over the document is a read of it, so
    /// it is answered only for a recipient whose read door hands it a replica document now (<see cref="ReadsDocument"/>);
    /// any other recipient is refused by the guard's shape, whatever the document declares and whatever the selection,
    /// an empty one included. A guard over the whole document would be admitted or refused by whether any declaration
    /// withholds from the recipient, which tells a world with a withheld row from one without it, so it is refused by
    /// its shape too.</summary>
    /// <param name="rows">The selected rows, or <see langword="null"/> for the whole document.</param>
    /// <param name="row">The first refused name the submission spelled, or <c>state</c> for a guard refused by shape.</param>
    /// <returns><see langword="true"/> when every selected row admits.</returns>
    public bool AdmitsDefinition(IReadOnlyList<string>? rows, out string row) {
        row = string.Empty;
        if (Unrestricted) { return true; }
        if ((rows is null) || !ReadsDocument) { row = WholeDocument; return false; }
        foreach (var name in rows) { if (!Admits(name)) { row = name; return false; } }
        return true;
    }
    // The name a guard refused by shape answers under: the section it reads, never a declaration in it.
    private const string WholeDocument = "state";

    /// <summary>Checks the declared state dependencies, deal sources and parents included, of every placement any
    /// bounded spatial read could observe (<see cref="WorldDefinitionFingerprint.EntersSpatialCensus"/>). Which
    /// placements one region holds can itself depend on state (a bound centre or extent, a parent's bound frame), so
    /// the answer takes no region: a refusal never tells the recipient where withheld geometry lies. A spatial read
    /// reads the document's placements, so a recipient whose read door hands it no replica document is refused.</summary>
    /// <returns><see langword="true"/> when every census-eligible placement's dependencies are admitted.</returns>
    public bool AdmitsSpatial() {
        if (Unrestricted) { return true; }
        if (!ReadsDocument) { return false; }
        foreach (var placement in Source.Placements) {
            if (WorldDefinitionFingerprint.EntersSpatialCensus(placement) && !AdmitsPlacement(placement.Id)) { return false; }
        }
        return true;
    }

    /// <summary>Checks a placement id's installed footprint without reading current values: the installed placement
    /// under the id, its parent chain, and the template every id on that chain spells as a dealt child, present or
    /// absent. Composition keeps an installed placement's dealt children and validation judges them against what
    /// replaces it, so a write that replaces or removes the id, or a guard on it, is judged by that whole chain.</summary>
    /// <param name="id">The placement id, which may name no placement.</param>
    /// <returns><see langword="true"/> when every state name the chain spells admits.</returns>
    public bool AdmitsPlacement(string id) {
        if (Unrestricted) { return true; }
        var visited = Writes.GetValue(this, static _ => new()).Placements;
        visited.Clear();
        return AdmitsChain(id, visited);
    }
    private bool AdmitsTemplateOf(string id, HashSet<string> visited) =>
        !WorldPlacementDeal.TrySplitChildId(id, out var template, out _) || AdmitsChain(template, visited);
    // Walks ids, not placements: an id that names no placement still depends on the template it spells. A batch
    // composes members without validating between them, so a chain may close on itself; each id is checked once.
    private bool AdmitsChain(string? id, HashSet<string> visited) {
        while (id is not null && visited.Add(id)) {
            if (!AdmitsTemplateOf(id, visited)) { return false; }
            if (WorldDefinitionRows.FindPlacement(Source.Placements, id) is not { } placement) { return true; }
            if (!AdmitsInstalled(placement, typeof(WorldPlacement))) { return false; }
            id = placement.Parent;
        }
        return true;
    }
    /// <summary>Checks every state name an installed document value spells (<see cref="WorldSpelledNames.StateNames"/>):
    /// the placement, machine, kit or other declaration a write replaces or removes. Composition and validation judge
    /// what the installed value decides against its replacement, so its rows are the write's footprint whether or not
    /// the replacement names them.</summary>
    /// <param name="installed">The installed value, or <see langword="null"/> when nothing is installed under the id.</param>
    /// <param name="type">The model type of <paramref name="installed"/>.</param>
    /// <returns><see langword="true"/> when every name admits.</returns>
    /// <remarks>The names checked here come from the document, never from the submission, so this answers no row name
    /// for a refusal to carry.</remarks>
    public bool AdmitsInstalled(object? installed, Type type) {
        if (Unrestricted || (installed is null)) { return true; }
        foreach (var name in WorldSpelledNames.StateNames(installed, type)) {
            if (!Admits(name)) { return false; }
        }
        return true;
    }
    /// <summary>Checks every state name the value a write replaces or removes spells, found in the authority's
    /// installed document (see <see cref="AdmitsInstalled(object?, Type)"/>).</summary>
    /// <typeparam name="T">The model type of the installed value.</typeparam>
    /// <param name="find">Finds the installed value in the document, or returns <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when nothing is installed or every name admits.</returns>
    public bool AdmitsInstalled<T>(Func<WorldDefinition, T?> find) where T : class {
        ArgumentNullException.ThrowIfNull(find);
        return Unrestricted || AdmitsInstalled(find(Source), typeof(T));
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
