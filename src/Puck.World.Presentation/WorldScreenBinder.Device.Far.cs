using System.Globalization;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

// What each world shows of itself to a far viewer (SdfFarProduct), one product per world the presentation holds a
// residency of, built once per version of that world's definition from that residency's settled static scene and shared
// by every sky view that asks for it (InfinityViewSpec.FarProduct). A world with no residency here has no product until a
// view of it draws the world whole once, which is that product's source (the derive on a miss); the boot world's own
// residency is its source from the start, so a view of it never draws it whole.
//
// Invariant: a far entry draws only a product built from the version of its world it shows. Enforced at the one door that
// hands a product to the plan, FarProductOf, which answers only a product whose revision is the mirror's current one; the
// plan replaces an entry whose product is no longer that answer (ReplanInfinity, InfinityInputsMoved).
public sealed partial class WorldScreenBinder {
    private readonly Dictionary<string, FarProductState> m_farProducts = new(comparer: StringComparer.Ordinal);
    // Moves whenever any product installs, so a root planned before it replans once (InfinityInputsMoved).
    private long m_farVersion;
    private long m_farBuilds;

    // The product of a world's current version, or null while none is built.
    private SdfFarProduct? FarProductOf(string instance, WorldSessionMirror mirror) =>
        ((m_farProducts.TryGetValue(key: instance, value: out var state) && (state.Product is { } product) && (state.Revision == mirror.DefinitionRevision))
            ? product
            : null);
    // Whether a view of a world waits for its product rather than drawing the world whole: the presentation holds a
    // residency of that world anyway (the boot world's own) and no build has yet finished for the world's version.
    private bool FarSourceResident(string instance, WorldSessionMirror mirror) =>
        ((instance == WorldDefinitionLoader.BootInstanceName) && (ViewHost is not null)
            && !(m_farProducts.TryGetValue(key: instance, value: out var state) && (state.BuiltFor == mirror.DefinitionRevision)));

    // Builds each product whose source has settled and whose world's version it does not hold, and installs each build
    // that finished. Runs once a reconcile, on the frame thread, before the roots follow their inputs.
    private void PublishFarProducts() {
        if ((m_renderProbe is { } probe) && (probe.FarProducts is null)) { probe.FarProducts = DescribeFarProducts; }
        if ((ViewHost is { } host) && (Observations.BootEndpoint is { } boot)) {
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
        if (state.Building is { } building) {
            if (!building.IsCompleted) { return; }
            state.Building = null;
            if (building.IsCompletedSuccessfully && (building.Result is { } built)) {
                state.Product = built;
                state.Revision = state.BuildingRevision;
                Console.Out.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.far: {instance} {built}]"));
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
        if (state.BuiltFor == mirror.DefinitionRevision) { return; }
        // The product is taken from a settled residency only: one still bringing its meshes would leave out what it owes.
        if (residency.MeshesArriving || (residency.Frame is not { } frame)) { return; }
        var options = OptionsOf(render: mirror.Definition.Render?.FarProduct);
        var inputs = SdfFarProduct.Take(frame: frame, options: options);

        if (inputs is null) { return; }
        state.BuildingRevision = mirror.DefinitionRevision;
        state.Building = Task.Run(function: () => SdfFarProduct.Build(inputs: inputs));
        m_farBuilds++;
    }
    private static SdfFarProductOptions OptionsOf(WorldFarProduct? render) => ((render is null)
        ? new SdfFarProductOptions()
        : new SdfFarProductOptions(Cell: render.Cell, MinimumSize: render.MinimumSize));
    private string DescribeFarProducts() {
        if (m_farProducts.Count == 0) { return $"far products: none, builds={m_farBuilds}"; }
        var lines = new List<string> { string.Create(provider: CultureInfo.InvariantCulture, handler: $"far products: {m_farProducts.Count}, builds={m_farBuilds}") };

        foreach (var (instance, state) in m_farProducts.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
            lines.Add(item: string.Create(provider: CultureInfo.InvariantCulture,
                handler: $"  {instance}: revision={state.Revision} {(((object?)state.Product) ?? "none")}{((state.Building is null) ? string.Empty : " building")}{((state.Fault is null) ? string.Empty : $" fault={state.Fault}")}"));
        }
        return string.Join(separator: Environment.NewLine, values: lines);
    }
    private sealed class FarProductState {
        public SdfFarProduct? Product { get; set; }
        // The definition revision the product was built from, the one its build is building from, and the last one a
        // build finished for (whether or not it kept anything).
        public int Revision { get; set; } = int.MinValue;
        public int BuildingRevision { get; set; } = int.MinValue;
        public int BuiltFor { get; set; } = int.MinValue;
        public Task<SdfFarProduct?>? Building { get; set; }
        public string? Fault { get; set; }
    }
}
