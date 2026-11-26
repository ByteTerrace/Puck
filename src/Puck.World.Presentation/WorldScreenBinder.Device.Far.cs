using System.Diagnostics;
using System.Globalization;
using Puck.Abstractions.Memory;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

// What each world shows of itself to a far viewer (SdfFarProduct), one product per world the presentation holds a
// residency of, answered once per version of that world's definition from that residency's settled static scene and shared
// by every sky view that asks for it (InfinityViewSpec.FarProduct). The mesh library's store keeps a product under the
// name of what it is derived from, so a product is derived (clustered and levelled) only where the store holds none, and
// the log line of each says which: a shipped cache or an earlier run on the machine makes it a read. A world with no
// residency here has no product until a view of it draws the world whole once, which is that product's source (the derive
// on a miss); the boot world's own residency is its source, from the start when its document offers a product
// (render.farProduct) and otherwise once a view asks for it, so a view of it never draws it whole. A world nothing views
// from afar and whose document offers none builds no product.
//
// Invariant: a far entry draws only a product built from the document version its source residency's world holds now.
// Enforced at the one door that hands a product to the plan, FarProductOf, which answers only a product whose version is
// its source mirror's current one; the plan replaces an entry whose product is no longer that answer (ReplanInfinity,
// InfinityInputsMoved). The viewer's own mirror of the world is not asked: two mirrors of one world receive an install
// at different moments, and the product follows the residency it is built from.
//
// Invariant: a product's claim on its mesh is released once, after the last plan that draws it has let it go. A product
// leaves its state only through Retire, and ReleaseRetiredFarProducts, run each reconcile, disposes a retired product
// only when no infinity content names it; a draw that still names the mesh holds it through its own rows' claims.
public sealed partial class WorldScreenBinder {
    // The seconds a product waits to be asked again after the account refused its workspace, doubling to the ceiling.
    private const int FarRetrySeconds = 2;
    private const int FarRetryCeilingSeconds = 64;

    private readonly Dictionary<string, FarProductState> m_farProducts = new(comparer: StringComparer.Ordinal);
    private readonly List<SdfFarProduct> m_retiredFarProducts = [];
    // Moves whenever any product installs, so a root planned before it replans once (InfinityInputsMoved).
    private long m_farVersion;
    // The builds started, and of those that kept a product, how many this process derived and how many the store answered.
    private long m_farBuilds;
    private long m_farDerived;
    private long m_farStored;
    // The worlds some planned view has asked to draw from their far product (NoteFarWanted). The boot world builds its
    // product only when it declares render.farProduct or is in this set: a world nothing views from afar builds none.
    private readonly HashSet<string> m_farWanted = new(comparer: StringComparer.Ordinal);

    // Records that a planned view draws a world from its far product, so the boot world's residency builds it.
    private void NoteFarWanted(string instance) => _ = m_farWanted.Add(item: instance);
    // Whether the boot world's residency builds the boot world's product: its document offers one (render.farProduct),
    // or a planned view asks for it, taking the product's defaults.
    private bool BootOffersFarProduct(WorldSessionMirror mirror) =>
        ((mirror.Definition.Render?.FarProduct is not null) || m_farWanted.Contains(item: WorldDefinitionLoader.BootInstanceName));

    // The product of a world's current version, or null while none is built.
    private SdfFarProduct? FarProductOf(string instance) =>
        ((m_farProducts.TryGetValue(key: instance, value: out var state) && (state.Product is { } product) && (state.Source is { } source)
            && (state.Revision == source.Document.Version))
            ? product
            : null);
    // Whether a view of a world waits for its product rather than drawing the world whole: the presentation holds a
    // residency of that world anyway (the boot world's own) and no build has yet finished for the world's version.
    private bool FarSourceResident(string instance) =>
        ((instance == WorldDefinitionLoader.BootInstanceName) && (ViewHost is not null)
            && !(m_farProducts.TryGetValue(key: instance, value: out var state) && (state.Source is { } source) && (state.BuiltFor == source.Document.Version)));

    // Builds each product whose source has settled and whose world's version it does not hold, and installs each build
    // that finished. Runs once a reconcile, on the frame thread, before the roots follow their inputs.
    private void PublishFarProducts() {
        if ((m_renderProbe is { } probe) && (probe.FarProducts is null)) { probe.FarProducts = DescribeFarProducts; }
        ReleaseRetiredFarProducts();
        if ((ViewHost is { } host) && (Observations.BootEndpoint is { } boot) && BootOffersFarProduct(mirror: boot.Mirror)) {
            PublishFarProduct(instance: WorldDefinitionLoader.BootInstanceName, mirror: boot.Mirror, residency: host);
        }
        foreach (var entry in InfinityEntries()) {
            if (entry.Planned.Spec.FarProduct && (entry.Far is null) && (entry.Residency is { } residency)) {
                PublishFarProduct(instance: entry.Instance, mirror: entry.Mirror, residency: residency);
            }
        }
    }
    private void PublishFarProduct(string instance, WorldSessionMirror mirror, SdfWorldResidency residency) {
        if (!m_farProducts.TryGetValue(key: instance, value: out var state)) {
            state = new FarProductState();
            m_farProducts.Add(key: instance, value: state);
        }
        state.Source = mirror;
        if (state.Building is { } building) {
            if (!building.IsCompleted) { return; }
            state.Building = null;
            if (building.IsCompletedSuccessfully) {
                state.RetrySeconds = 0;
                // The product of the version before is let go whether or not this version kept one.
                Retire(state: state);
                if (building.Result is { } built) {
                    state.Product = built;
                    state.Revision = state.BuildingRevision;
                    if (built.Derived) { m_farDerived++; } else { m_farStored++; }
                    Console.Out.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.far: {instance} {built}]"));
                }
            } else if ((building.Exception is { } refusal) && (HostMemoryRefusedException.Of(exception: refusal) is { } refused)) {
                // The account had no room for the product's workspace: nothing was kept, and the product is asked again
                // after a wait, as a bake's group is readmitted, rather than marked built with nothing to show.
                state.RetrySeconds = ExponentialBackoff.Next(ceiling: FarRetryCeilingSeconds, current: state.RetrySeconds, first: FarRetrySeconds);
                state.RetryAt = (Stopwatch.GetTimestamp() + (state.RetrySeconds * Stopwatch.Frequency));
                Console.Error.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.far: {instance} waits {state.RetrySeconds} s for memory: {refused.Message}]"));
                return;
            } else if (building.Exception is { } fault) {
                state.Fault = fault.GetBaseException().Message;
                Console.Error.WriteLine(value: $"[world.far: {instance} build failed: {state.Fault}]");
            }
            state.BuiltFor = state.BuildingRevision;
            // Every finished build moves the version, so a view waiting on it replans: to the product, or to the world
            // whole when the build kept nothing.
            m_farVersion++;
        }
        // One build a version: a version whose build failed or kept nothing is not built again until the world changes.
        if ((state.BuiltFor == mirror.Document.Version) || (Stopwatch.GetTimestamp() < state.RetryAt)) { return; }
        // The product is taken from a settled residency only: one still bringing its meshes would leave out what it owes.
        if (residency.MeshesArriving || (residency.Frame is not { } frame)) { return; }
        var options = OptionsOf(render: mirror.Definition.Render?.FarProduct);
        var bake = residency.MeshPathBake;
        var inputs = SdfFarProduct.Take(bake: bake, frame: frame, options: options);

        if (inputs is null) { return; }

        var library = MeshLibrary;
        var identity = bake.Identity.Value;

        state.BuildingRevision = mirror.Document.Version;
        state.Building = Task.Run(function: () => SdfFarProduct.Build(identity: identity, inputs: inputs, library: library));
        m_farBuilds++;
    }
    // Takes a state's product out of it, to be released once no plan draws it.
    private void Retire(FarProductState state) {
        if (state.Product is { } product) { m_retiredFarProducts.Add(item: product); }
        state.Product = null;
    }
    private void ReleaseRetiredFarProducts() {
        for (var at = (m_retiredFarProducts.Count - 1); (at >= 0); at--) {
            var product = m_retiredFarProducts[at];
            var drawn = false;

            foreach (var content in m_infinityContents.Keys) {
                if (ReferenceEquals(objA: content.Far, objB: product)) {
                    drawn = true;
                    break;
                }
            }
            if (drawn) { continue; }
            m_retiredFarProducts.RemoveAt(index: at);
            product.Dispose();
        }
    }
    // Releases every product's claim, at the binder's end, after the residencies that drew them are gone.
    private void DisposeFarProducts() {
        foreach (var state in m_farProducts.Values) { Retire(state: state); }
        foreach (var product in m_retiredFarProducts) { product.Dispose(); }
        m_retiredFarProducts.Clear();
        m_farProducts.Clear();
    }
    private static SdfFarProductOptions OptionsOf(WorldFarProduct? render) => ((render is null)
        ? new SdfFarProductOptions()
        : new SdfFarProductOptions(Cell: render.Cell, MinimumSize: render.MinimumSize));
    private string DescribeFarProducts() {
        if (m_farProducts.Count == 0) { return $"far products: none, builds={m_farBuilds}"; }
        var lines = new List<string> { string.Create(provider: CultureInfo.InvariantCulture, handler: $"far products: {m_farProducts.Count}, builds={m_farBuilds} derived={m_farDerived} stored={m_farStored}") };

        foreach (var (instance, state) in m_farProducts.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
            lines.Add(item: string.Create(provider: CultureInfo.InvariantCulture,
                handler: $"  {instance}: version={state.Revision.Sequence} {(((object?)state.Product) ?? "none")}{((state.Building is null) ? string.Empty : " building")}{((state.Fault is null) ? string.Empty : $" fault={state.Fault}")}"));
        }
        return string.Join(separator: Environment.NewLine, values: lines);
    }
    private sealed class FarProductState {
        public SdfFarProduct? Product { get; set; }
        // The mirror of the world the source residency draws, whose document version the product is judged by.
        public WorldSessionMirror? Source { get; set; }
        // The definition revision the product was built from, the one its build is building from, and the last one a
        // build finished for (whether or not it kept anything).
        public WorldDocumentVersion Revision { get; set; }
        public WorldDocumentVersion BuildingRevision { get; set; }
        public WorldDocumentVersion BuiltFor { get; set; }
        public Task<SdfFarProduct?>? Building { get; set; }
        public string? Fault { get; set; }
        // The wait after the account last refused the build's workspace, and the timestamp it ends at.
        public long RetrySeconds { get; set; }
        public long RetryAt { get; set; }
    }
}
