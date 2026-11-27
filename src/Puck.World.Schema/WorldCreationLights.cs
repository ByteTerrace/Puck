using System.Numerics;

namespace Puck.World;

/// <summary>
/// The lights a world's creations carry (<see cref="WorldPrototype.Lights"/>), composed into the world's light list:
/// one light for each light of each prototype a placement can show (its own and each response's), anchored to the
/// placement while it shows that prototype (<see cref="WorldAnchor.Placement.Showing"/>), positioned at the
/// placement's scale. Every reader of the world's lights (the presentation's resolve, the shadow selection,
/// <c>world.lighting</c>, the validator) reads the list through <see cref="Expand"/>, so a creation's light is one
/// more light of the table wherever it is read, and no reader keeps a copy in step with the creation by hand.
/// </summary>
/// <remarks>A creation's light appends after the authored lights, so a sky disc's light index and a section key keep
/// addressing the lights the document authors. A placement of a lit creation needs one frame for its lights to ride
/// (<see cref="Carries"/>); the validator refuses one that expands into many.</remarks>
public static class WorldCreationLights {
    private static readonly Puck.Abstractions.CodeDerivedTable<WorldDefinition, WorldRenderLighting?[]> Expanded = new();

    /// <summary>Returns the world's lighting section with its keys applied (<see cref="WorldRenderKeys.Expand(WorldRenderLighting?)"/>)
    /// and its creations' lights appended, once per definition.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The section; the authored section itself when no placed creation carries a light, and
    /// <see langword="null"/> when the world authors none and carries none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldRenderLighting? Expand(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return Expanded.GetValue(key: definition, derive: static definition => [Compose(definition: definition, lighting: WorldRenderKeys.Expand(lighting: definition.Render.Lighting))])[0];
    }
    /// <summary>Returns a lighting section with the definition's creations' lights appended to its lights. A section
    /// that lists no lights renders the pinned sun, so the creations' lights append to the pinned sun.</summary>
    /// <param name="definition">The definition whose placements and prototypes carry the lights.</param>
    /// <param name="lighting">The section to append to, keys already applied.</param>
    /// <returns><paramref name="lighting"/> itself when no placed creation carries a light.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldRenderLighting? Compose(WorldDefinition definition, WorldRenderLighting? lighting) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var carried = Carried(definition: definition);

        if (carried.Count == 0) {
            return lighting;
        }

        return ((lighting ?? new WorldRenderLighting()) with {
            Lights = [.. (lighting?.Lights ?? WorldRenderLighting.Pinned.Lights ?? []), .. carried],
        });
    }
    /// <summary>Returns the lights the definition's placed creations carry, in placement order, then in the order of
    /// the prototypes each placement can show, then in each prototype's order.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The lights, each anchored to its placement while it shows its prototype.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<WorldRenderLight> Carried(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        List<WorldRenderLight>? carried = null;

        foreach (var placement in definition.Placements) {
            if ((placement?.Id is not { } id) || !Carries(placement: placement)) {
                continue;
            }

            var scale = (placement.HasStaticFrame ? WorldDefinitionRows.ResolvedFrame(definition: definition, placement: placement).Scale : placement.Scale);

            foreach (var prototypeId in Shows(placement: placement)) {
                if (WorldDefinitionRows.FindCreation(creations: definition.Creations, id: prototypeId)?.Lights is not { Count: > 0 } lights) {
                    continue;
                }

                for (var index = 0; (index < lights.Count); index++) {
                    if (lights[index] is not { } light) {
                        continue;
                    }

                    var name = string.Concat(id, GeneratedName.Joiner.ToString(), prototypeId, GeneratedName.Joiner.ToString(), (light.LightName ?? index.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)));

                    (carried ??= []).Add(item: Place(anchor: new WorldAnchor.Placement(PlacementId: id, ShapeId: null, Showing: prototypeId), light: light, name: name, scale: scale));
                }
            }
        }

        return ((IReadOnlyList<WorldRenderLight>?)carried ?? []);
    }
    /// <summary>Returns whether a placement has the one frame a creation's light rides: no deal template, mirror or
    /// expanding distribution, and at most one inhabitant.</summary>
    /// <param name="placement">The placement.</param>
    /// <returns><see langword="true"/> when the placement carries its creation's lights.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is <see langword="null"/>.</exception>
    public static bool Carries(WorldPlacement placement) {
        ArgumentNullException.ThrowIfNull(argument: placement);

        return (
            (placement.Deal is null) &&
            (placement.Mirror is null) &&
            (placement.Distribution is null) &&
            ((placement.Inhabit is null) || (placement.Inhabit.ResolvedCount.Literal == 1))
        );
    }
    /// <summary>Returns whether a light kind may belong to a creation: a point, spot or tube light, which stands at a
    /// place the creation's frame carries.</summary>
    /// <param name="light">The light.</param>
    /// <returns><see langword="true"/> for a point, spot or tube light.</returns>
    public static bool IsCarriable(WorldRenderLight? light) => (light is WorldRenderLight.Point or WorldRenderLight.Spot or WorldRenderLight.Tube);

    // The prototypes a row can show: its own and each response's, each once, in authored order.
    private static IEnumerable<string> Shows(WorldPlacement placement) {
        yield return placement.PrototypeId;

        if (placement.Respond is not { } responses) {
            yield break;
        }

        for (var index = 0; (index < responses.Count); index++) {
            if ((responses[index]?.PrototypeId is not { } prototypeId) || string.Equals(a: prototypeId, b: placement.PrototypeId, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            var seen = false;

            for (var earlier = 0; (earlier < index); earlier++) {
                seen |= string.Equals(a: responses[earlier]?.PrototypeId, b: prototypeId, comparisonType: StringComparison.Ordinal);
            }
            if (!seen) {
                yield return prototypeId;
            }
        }
    }
    // A creation's light as the world's: anchored to the row, its place in the creation's frame at the row's scale.
    private static WorldRenderLight Place(WorldRenderLight light, WorldAnchor anchor, string name, float scale) => (light switch {
        WorldRenderLight.Point point => (point with { Anchor = anchor, Name = name, Position = Scaled(position: point.Position, scale: scale) }),
        WorldRenderLight.Spot spot => (spot with { Anchor = anchor, Name = name, Position = Scaled(position: spot.Position, scale: scale) }),
        WorldRenderLight.Tube tube => (tube with {
            Anchor = anchor,
            From = Scaled(position: tube.From, scale: scale),
            Name = name,
            Radius = Math.Min(val1: (tube.Radius * scale), val2: WorldRenderLight.Tube.MaxRadius),
            To = Scaled(position: tube.To, scale: scale),
        }),
        _ => light,
    });
    private static BindableVector3? Scaled(BindableVector3? position, float scale) {
        if ((position is not { } value) || (scale == 1f)) {
            return position;
        }
        if (value.Literal is { } literal) {
            return new BindableVector3(literal: (literal * scale));
        }
        if (value.Keys is { } keys) {
            return new BindableVector3(keys: new WorldKeyTrack<Vector3>(clock: keys.Clock, keys: [.. keys.Keys.Select(selector: key => (key with { Value = (key.Value * scale) }))]));
        }

        return position;
    }
}
