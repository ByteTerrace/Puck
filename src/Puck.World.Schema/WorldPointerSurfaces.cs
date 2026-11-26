using Puck.Commands;

namespace Puck.World;

/// <summary>One surface a seat's pointer ray maps onto: a <c>Simulation</c> screen's face or a placement's pointer
/// surface, with the mapping a ray takes onto it and the regions a hit is tested against.</summary>
/// <param name="Name">The surface's read-back name: <c>screen:&lt;index&gt;</c> or <c>placement:&lt;id&gt;</c>.</param>
/// <param name="Mapping">The source-normalized mapping a ray takes onto the surface.</param>
/// <param name="Regions">The surface's named regions, or <see langword="null"/> for none.</param>
public readonly record struct WorldPointerTarget(string Name, SourceMapping Mapping, IReadOnlyList<WorldPointerRegion>? Regions);

/// <summary>The pointer surfaces a document declares, each with the mapping a <c>$pointer:</c> rule read takes onto it:
/// every <c>Simulation</c> screen's face (<see cref="WorldScreenMappings.Normalized"/>) and every placement's pointer
/// surface where the placement's resolved frame puts it (<see cref="WorldPointerSurface.MappingIn"/>). The one
/// enumeration the authority's read-back and the host's pointer capture walk, so both test a ray against the same
/// rectangles. Built from document data alone.</summary>
public static class WorldPointerSurfaces {
    /// <summary>Lists the pointer surfaces a document declares, screens first, in document order.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The surfaces; empty when the document declares none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<WorldPointerTarget> Of(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        List<WorldPointerTarget>? targets = null;

        foreach (var screen in definition.Screens) {
            if (screen.Route.Input == SourceDestination.Simulation) {
                (targets ??= []).Add(item: new WorldPointerTarget(
                    Mapping: WorldScreenMappings.Normalized(screen: screen),
                    Name: $"screen:{screen.Index}",
                    Regions: screen.Route.Regions
                ));
            }
        }
        foreach (var placement in definition.Placements) {
            if (placement?.Pointer is { } surface) {
                (targets ??= []).Add(item: new WorldPointerTarget(
                    Mapping: surface.MappingIn(frame: WorldDefinitionRows.ResolvedFrame(definition: definition, placement: placement), placementId: placement.Id),
                    Name: $"placement:{placement.Id}",
                    Regions: surface.Regions
                ));
            }
        }

        return ((IReadOnlyList<WorldPointerTarget>?)targets ?? []);
    }
    /// <summary>Returns whether a ray lands on any of the surfaces, ignoring one whose mapping does not validate.</summary>
    /// <param name="targets">The surfaces, from <see cref="Of"/>.</param>
    /// <param name="ray">The ray.</param>
    /// <returns><see langword="true"/> when the ray meets a surface on its face.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="targets"/> is <see langword="null"/>.</exception>
    public static bool Lands(IReadOnlyList<WorldPointerTarget> targets, SourceRay ray) {
        ArgumentNullException.ThrowIfNull(argument: targets);

        foreach (var target in targets) {
            if (target.Mapping.TryValidate(refusal: out _) && target.Mapping.MapRay(ray: ray).IsOnSource) {
                return true;
            }
        }

        return false;
    }
}
