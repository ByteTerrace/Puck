using System.Text;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The Arc 7 INHABITATION + creation-facet READ-BACK surface — four Immediate censuses:
/// <c>world.inhabitants</c>, <c>world.faces</c>, <c>world.attachments</c>, <c>world.portals</c>. A placement's
/// <c>inhabit</c> and <c>faceSources</c> facets ride its WHOLE row through the general
/// <see cref="WorldRowCommandModule"/> (<c>world.row.set placements &lt;json&gt;</c>), and <c>world.placement.get</c>
/// (<see cref="WorldMutationCommandModule"/>) is the round-trip twin that harvests its exact current JSON to edit.
/// A SEPARATE module from <see cref="WorldMutationCommandModule"/> (at its analyzer ceiling; the plan splits the
/// verb families).
/// </summary>
internal sealed class WorldPlacementCommandModule(WorldServer server, WorldPopulation population, WorldScreenBinder binder) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.inhabitants",
            description: "Reports the inhabited-placement census (Immediate; reads the settled state after any pending mutation): one line per inhabited body — placementId, creationId, kit, source, bodyIndex, position.",
            handler: (_, _) => new CommandResult(Output: DescribeInhabitants())
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.faces",
            description: "Reports the derived-face census (Immediate): one line per derived creation face — placementId, faceName, screenIndex, resolvedSource, and the bound content handle (0 = the no-signal card).",
            handler: (_, _) => new CommandResult(Output: DescribeFaces())
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.attachments",
            description: "Reports every placement's ATTACH facet resolution (Immediate; reads the settled state after any pending mutation): one line per attached placement — placementId, bodyIndex, and either the resolved world pos/yaw (the body transform composed with the authored local offset, fixed-point) or the reason the row contributes nothing (an out-of-range or inactive/despawned body). This is the AUTHORITATIVE tick-aligned pose; the rendered stamp composes the same offset over the client's interpolated body pose, so between ticks the drawn position leads or trails this one exactly as an avatar's does.",
            handler: (_, _) => new CommandResult(Output: DescribeAttachments())
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.portals",
            description: "Reports every placement face's PORTAL facet (Immediate): one line per door — placementId/faceName, the named destination (a references row), lifetime (fresh|persistent, plus instance when persistent), and the RESOLVED travel (facet.travel, else portals.portalDefaults.travel, else 'body' when the world declares no portals section — the same order WorldDefinitionValidator resolves against). Authored data only: no boot-time destination-document check, and this verb echoes the DECISION — it never fires it; crossing the threshold is a later step's diegetic trigger.",
            handler: (_, _) => new CommandResult(Output: DescribePortals())
        );
    }

    private string DescribeInhabitants() {
        var builder = new StringBuilder(value: "[world.inhabitants:");
        var any = false;

        for (var index = 0; (index < population.Capacity); index++) {
            if ((population.InhabitantPlacementId(index: index) is not { } placementId) || (population.EntryBody(index: index) is not { } body)) {
                continue;
            }

            any = true;

            var placement = WorldDefinitionRows.FindPlacement(placements: server.Definition.Placements, id: placementId);
            var creationId = (placement?.CreationId ?? "?");
            var kit = ((placement?.Inhabit?.Kit) ?? "(locomotion)");
            var source = (placement?.Inhabit?.Source.ToString() ?? "?");
            var position = body.Position;

            _ = builder.Append(value: $" {placementId}[creation={creationId} kit={kit} source={source} body={index} pos={position.X:0.0},{position.Y:0.0},{position.Z:0.0}]");
        }

        return builder.Append(value: (any ? "" : " none")).Append(value: ']').ToString();
    }
    private string DescribeFaces() {
        var definition = server.Definition;
        var builder = new StringBuilder(value: "[world.faces:");
        var faceIndex = WorldCreationFacets.DerivedFaceBase;
        var limit = (WorldCreationFacets.DerivedFaceBase + definition.Authoring.DerivedFaceScreens);
        var any = false;

        foreach (var placement in definition.Placements) {
            if (WorldDefinitionRows.FindCreation(creations: definition.Creations, id: placement.CreationId) is not { } creation) {
                continue;
            }

            foreach (var face in (creation.Document.Behavior?.Faces ?? [])) {
                if (faceIndex >= limit) {
                    break;
                }

                any = true;

                var handle = binder.CurrentHandle(index: faceIndex);

                _ = builder.Append(value: $" {placement.Id}/{face.Name}[screen={faceIndex} handle={((handle != 0) ? "bound" : "no-signal")}]");
                faceIndex++;
            }
        }

        return builder.Append(value: (any ? "" : " none")).Append(value: ']').ToString();
    }
    private string DescribeAttachments() {
        var definition = server.Definition;
        var builder = new StringBuilder(value: "[world.attachments:");
        var any = false;

        foreach (var placement in definition.Placements) {
            if (placement.Attach is not { } attach) {
                continue;
            }

            any = true;

            if (WorldPlacementAttachment.TryResolve(attach: attach, population: population, position: out var position, yawRadians: out var yaw, reason: out var reason)) {
                var worldPosition = position.ToVector3();
                var yawDegrees = ((float)(double)yaw * (180f / MathF.PI));

                _ = builder.Append(value: $" {placement.Id}[body={attach.BodyIndex} pos=({worldPosition.X:0.00}, {worldPosition.Y:0.00}, {worldPosition.Z:0.00}) yaw={yawDegrees:0}°]");
            } else {
                _ = builder.Append(value: $" {placement.Id}[body={attach.BodyIndex} absent — {reason}]");
            }
        }

        return builder.Append(value: (any ? "" : " none")).Append(value: ']').ToString();
    }
    private string DescribePortals() {
        var definition = server.Definition;
        var defaultTravel = (definition.Portals?.PortalDefaults.Travel ?? WorldPortalTravel.Body);
        var builder = new StringBuilder(value: "[world.portals:");
        var any = false;

        foreach (var placement in definition.Placements) {
            foreach (var face in (placement.FaceSources ?? [])) {
                if (face.Portal is not { } portal) {
                    continue;
                }

                any = true;

                var travel = (portal.Travel ?? defaultTravel);
                var instance = ((portal.Lifetime == WorldPortalLifetime.Persistent) ? $" instance={portal.Instance}" : "");

                _ = builder.Append(value: $" {placement.Id}/{face.Face}[-> {portal.Destination} lifetime={WorldPortalTokens.LifetimeToken(lifetime: portal.Lifetime)}{instance} travel={WorldPortalTokens.TravelToken(travel: travel)}]");
            }
        }

        return builder.Append(value: (any ? "" : " none")).Append(value: ']').ToString();
    }
}
