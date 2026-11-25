using System.Numerics;
using Puck.Abstractions;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The navigation domains a document's simulation runs, each with its extent resolved: a domain that names a
/// placement's region (<see cref="WorldNavigationDomain.Region"/>) takes its origin, width, depth and probe band or
/// layers from that region's box, so moving a place moves the ground its walkers route over in the same edit. Every
/// reader of a domain's extent (the population's compiled domains, the validator, a flock's or a motion's domain
/// check) reads it here, never from the authored rows, so no reader sees a region-bound domain without its extent.
/// </summary>
public static class WorldNavigationDomains {
    private sealed class Held {
        public IReadOnlyList<WorldNavigationDomain> Domains { get; init; } = [];
    }

    private static readonly CodeDerivedTable<WorldDefinition, Held> Cache = new();

    /// <summary>Returns a document's navigation domains in authored order, each region-bound one resolved. A domain whose
    /// region cannot be resolved (an unknown or moving placement, a sphere, a turned box) keeps its authored form with
    /// no extent; the validator refuses it by name (<see cref="TryResolve"/>).</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The resolved domains.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<WorldNavigationDomain> Of(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return Cache.GetValue(
            key: definition,
            derive: static candidate => new Held { Domains = Resolve(definition: candidate) }
        ).Domains;
    }
    private static WorldNavigationDomain[] Resolve(WorldDefinition definition) {
        var authored = (definition.Navigation.Domains ?? []);
        var resolved = new WorldNavigationDomain[authored.Count];

        for (var index = 0; (index < resolved.Length); index++) {
            resolved[index] = (TryResolve(definition: definition, domain: authored[index], refusal: out _, resolved: out var domain)
                ? domain
                : authored[index]);
        }

        return resolved;
    }
    /// <summary>Resolves one domain's extent from its region, or returns an authored domain as it stands.</summary>
    /// <param name="definition">The document.</param>
    /// <param name="domain">The authored domain.</param>
    /// <param name="resolved">The domain with its extent, on success.</param>
    /// <param name="refusal">Why the region does not resolve, on failure: the clause after the domain's path.</param>
    /// <returns><see langword="true"/> when the domain carries no region or its region resolves.</returns>
    public static bool TryResolve(WorldDefinition definition, WorldNavigationDomain domain, out WorldNavigationDomain resolved, out string? refusal) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: domain);

        resolved = domain;
        refusal = null;

        if (domain.Region is not { } id) {
            resolved = EveryCell(domain: domain);

            return true;
        }

        if ((domain.Origin is not null) || (domain.Width is not null) || (domain.Depth is not null) || (domain.ProbeUp != 0f) || (domain.ProbeDown != 0f) || (domain.Layers != 1) || (domain.Parent is not null)) {
            refusal = $"names region '{id}', which gives its origin, width, depth, probe band and layers; it authors none of them, nor a parent.";

            return false;
        }

        if (WorldPlacementIndex.ById(placements: definition.Placements).GetValueOrDefault(key: id) is not { Region: { HalfExtents: { } half } region, Attach: null, Deal: null } placement) {
            refusal = $"region '{id}' must name a static placement whose region is a box.";

            return false;
        }

        var frame = WorldDefinitionRows.ResolvedFrame(definition: definition, placement: placement);

        if ((frame.FixedYawRadians != FixedQ4816.Zero) || (region.YawDegrees != 0f)) {
            refusal = $"region '{id}' is turned about +Y; a domain's grid follows the world's axes, so its region may not turn.";

            return false;
        }

        var cell = domain.CellSize;
        var margin = domain.Margin;
        var center = (frame.Position + region.Center);
        var low = new Vector3(x: (center.X - half.X - margin), y: (center.Y - half.Y), z: (center.Z - half.Z - margin));
        var width = (((2f * half.X) + (2f * margin)) / cell);
        var depth = (((2f * half.Z) + (2f * margin)) / cell);

        if (!(cell > 0f) || !float.IsFinite(f: margin) || (margin < 0f) || !OnGrid(value: width) || !OnGrid(value: depth) || !OnGrid(value: (low.X / cell)) || !OnGrid(value: (low.Z / cell))) {
            refusal = $"region '{id}' grown by margin {margin} must fall on the domain's {cell} cell grid: its corners and its size each a whole number of cells.";

            return false;
        }

        resolved = (domain.Kind == WorldNavigationKind.Surface)
            ? domain with {
                Depth = ((int)MathF.Round(x: depth)),
                Origin = low with { Y = center.Y },
                ProbeDown = half.Y,
                ProbeUp = half.Y,
                Width = ((int)MathF.Round(x: width)),
            }
            : domain with {
                Depth = ((int)MathF.Round(x: depth)),
                Layers = Math.Max(val1: 1, val2: ((int)MathF.Round(x: ((2f * half.Y) / cell)))),
                Origin = low,
                Width = ((int)MathF.Round(x: width)),
            };
        resolved = EveryCell(domain: resolved);

        return true;
    }
    // A zero expansion budget searches every cell in one route: the domain's cell count.
    private static WorldNavigationDomain EveryCell(WorldNavigationDomain domain) => (((domain.MaxExpandedNodes == 0) && (domain.Width is { } width) && (domain.Depth is { } depth))
        ? domain with { MaxExpandedNodes = ((int)Math.Min(val1: int.MaxValue, val2: ((((long)width) * depth) * Math.Max(val1: 1, val2: domain.Layers)))) }
        : domain
    );
    // A whole number of cells, within the float rounding of an authored decimal.
    private static bool OnGrid(float value) => (float.IsFinite(f: value) && (MathF.Abs(x: (value - MathF.Round(x: value))) < 1e-3f));
}
