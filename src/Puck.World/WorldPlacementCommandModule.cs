using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The inhabitation and creation-facet read-back surface — six Immediate censuses:
/// <c>world.inhabitants</c>, <c>world.faces</c>, <c>world.attachments</c>, <c>world.portals</c>,
/// <c>world.adjacencies</c>, <c>world.destinations</c>. A placement's <c>inhabit</c> and <c>faceSources</c> facets ride its whole row through
/// the general <see cref="WorldRowCommandModule"/> (<c>world.row.set placements &lt;json&gt;</c>), and
/// <c>world.placement.get</c> (<see cref="WorldMutationCommandModule"/>) is the round-trip twin that harvests its
/// exact current JSON to edit. A separate module from <see cref="WorldMutationCommandModule"/>, split at its
/// analyzer ceiling.
/// </summary>
/// <remarks>Every census but <c>world.faces</c> accepts a trailing <c>instance:&lt;name&gt;</c> token that redirects
/// the read from the boot world onto a named running instance's own population/document — the same grammar
/// <c>PlayerCommandModule.TryStripInstanceToken</c> and <see cref="WorldRateCommandModule"/> already establish
/// (see <see cref="TryResolveInstance"/>). <c>world.faces</c> has no instance-addressed form: screens, the
/// derived-face index space, and session binding are the boot instance's own presentation state, and a spawned
/// instance carries neither a client nor a real machine host to bind them from.</remarks>
internal sealed class WorldPlacementCommandModule(WorldServer server, WorldPopulation population, WorldScreenBinder binder, WorldInstanceHost instances, WorldSessionResolver resolver) : ICommandModule {
    // The resolver's own live state for one destination row — see WorldSessionResolver.DescribeActive. Occupancy
    // reads back "?" for a generation whose instance is no longer running (a stale echo, not a live one — the
    // resolver's own cache entry is cleared the moment WorldInstanceHost.TryStop/ReapIfEmpty actually retires it, so
    // this case is transient at best).
    private string DescribeActiveGenerations(string destinationName, WorldDestinationDurability durability, string referencedDocument) {
        var rows = resolver.DescribeActive(
            destinationName: destinationName,
            durability: durability,
            referencedDocument: referencedDocument
        );

        if (rows.Count == 0) {
            return "none";
        }

        var parts = new List<string>(capacity: rows.Count);

        foreach (var row in rows) {
            var occupancy = (instances.TryGet(
                instance: out var instance,
                name: row.InstanceName
            )
                ? instance!.Server.Population.ActiveCount().ToString()
                : "?"
            );

            parts.Add(item: $"{row.ScopeKey}:gen{row.GenerationId}@{row.InstanceName}(occupancy={occupancy})");
        }

        return string.Join(
            separator: ",",
            values: parts
        );
    }
    private string DescribeAdjacencies(WorldInstance? instance) {
        var definition = (instance?.Server.Definition ?? server.Definition);
        var source = (instance?.Server.Adjacencies ?? server.Adjacencies);
        var builder = new StringBuilder(value: "[world.adjacencies:");
        var any = false;

        foreach (var adjacency in (definition.Adjacencies ?? [])) {
            if (adjacency is null) {
                continue;
            }

            any = true;
            var boundary = adjacency.Boundary;

            _ = builder.Append(value: $" {adjacency.Name}[destination={adjacency.Destination} counterpart={adjacency.Counterpart} center={boundary.Center.X.ToString(
                format: "0.###",
                provider: CultureInfo.InvariantCulture
            )},{boundary.Center.Y.ToString(
                format: "0.###",
                provider: CultureInfo.InvariantCulture
            )},{boundary.Center.Z.ToString(
                format: "0.###",
                provider: CultureInfo.InvariantCulture
            )} yaw={boundary.OutwardYawDegrees.ToString(
                format: "0.###",
                provider: CultureInfo.InvariantCulture
            )} pitch={boundary.OutwardPitchDegrees.ToString(
                format: "0.###",
                provider: CultureInfo.InvariantCulture
            )} size={boundary.Width.ToString(
                format: "0.###",
                provider: CultureInfo.InvariantCulture
            )}x{boundary.Height.ToString(
                format: "0.###",
                provider: CultureInfo.InvariantCulture
            )} capacity={((adjacency.Capacity is { } borderCapacity)
                ? borderCapacity.ToString(provider: CultureInfo.InvariantCulture)
                : "destination")} unavailable={adjacency.Unavailable.ToString().ToLowerInvariant()} onUnavailable={(adjacency.OnUnavailable ?? "none")}");

            if (
                (source is null) ||
                !source.TryResolve(
                adjacencyName: adjacency.Name.Value,
                neighbour: out var neighbour
            ) ||
                (neighbour is null)
            ) {
                _ = builder.Append(value: " state=CLOSED]");
                continue;
            }

            var overlap = (WorldAdjacencyPolicy.TryDeriveOverlap(
                local: definition,
                neighbour: neighbour.Definition,
                depth: out var depth,
                reason: out var reason
            )
                ? ((double)depth).ToString(
                    format: "0.####",
                    provider: CultureInfo.InvariantCulture
                )
                : $"REFUSED({reason})"
            );
            var addresses = new List<string>();

            for (var index = 0; (index < neighbour.EntityCapacity); index++) {
                if (neighbour.IsEntityActive(index: index)) {
                    addresses.Add(item: neighbour.EntityAddress(index: index).ToString());
                }
            }
            _ = builder.Append(value: $" state=open overlap={overlap} tick={neighbour.SnapshotTick} entities={((addresses.Count == 0)
                ? "none"
                : string.Join(
                    separator: ",",
                    values: addresses
                ))}]");
        }

        if (source is not null) {
            foreach (var projection in source.Visuals().Where(predicate: static projection => !projection.Direct)) {
                any = true;
                var addresses = new List<string>();

                for (var index = 0; (index < projection.Neighbour.EntityCapacity); index++) {
                    if (projection.Neighbour.IsEntityActive(index: index)) {
                        addresses.Add(item: projection.Neighbour.EntityAddress(index: index).ToString());
                    }
                }
                _ = builder.Append(value: $" {projection.Name}[derived=corner hops={projection.Path.Count} state=open overlap={((double)projection.OverlapDepth).ToString(
                    format: "0.####",
                    provider: CultureInfo.InvariantCulture
                )} tick={projection.Neighbour.SnapshotTick} entities={((addresses.Count == 0)
                    ? "none"
                    : string.Join(
                        separator: ",",
                        values: addresses
                    ))}]");
            }
        }

        return builder.Append(value: (any
            ? ""
            : " none")).Append(value: ']').ToString();
    }
    private string DescribeAttachments(WorldInstance? instance) {
        var pop = (instance?.Server.Population ?? population);
        var definition = (instance?.Server.Definition ?? server.Definition);
        var builder = new StringBuilder(value: "[world.attachments:");
        var any = false;

        foreach (var placement in definition.Placements) {
            if (placement.Attach is not { } attach) {
                continue;
            }

            any = true;

            if (WorldPlacementAttachment.TryResolve(
                attach: attach,
                population: pop,
                position: out var position,
                reason: out var reason,
                yawRadians: out var yaw
            )) {
                var worldPosition = position.ToVector3();
                var yawDegrees = (((float)((double)yaw)) * (180f / MathF.PI));

                _ = builder.Append(value: $" {placement.Id}[body={attach.BodyIndex} pos=({worldPosition.X:0.00}, {worldPosition.Y:0.00}, {worldPosition.Z:0.00}) yaw={yawDegrees:0}°]");
            } else {
                _ = builder.Append(value: $" {placement.Id}[body={attach.BodyIndex} absent — {reason}]");
            }
        }

        return builder.Append(value: (any
            ? ""
            : " none")).Append(value: ']').ToString();
    }
    // Every placement/face PORTAL facet naming a given destination, joined for one world.destinations line.
    private static string DescribeDestinationConsumers(WorldDefinition definition, string name) {
        var consumers = new List<string>();

        foreach (var placement in definition.Placements) {
            foreach (var face in (placement.FaceSources ?? [])) {
                if (
                    (face.Portal is { } portal) &&
                    string.Equals(
                    a: portal.Destination,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    consumers.Add(item: $"{placement.Id}/{face.Face}");
                }
            }
        }

        return ((consumers.Count > 0)
            ? string.Join(
                separator: ",",
                values: consumers
            )
            : "none"
        );
    }
    private string DescribeDestinations(WorldInstance? instance) {
        var definition = (instance?.Server.Definition ?? server.Definition);
        var builder = new StringBuilder(value: "[world.destinations:");
        var any = false;

        foreach (var destination in (definition.Destinations ?? [])) {
            any = true;

            var reference = WorldDefinitionRows.FindReference(
                references: definition.References,
                name: destination.Reference
            );
            var documentPath = (reference?.Document ?? "?");
            var consumers = DescribeDestinationConsumers(
                definition: definition,
                name: destination.Name.Value
            );
            var scopeToken = WorldDestinationTokens.ScopeToken(scope: destination.Scope);
            var selectorText = DescribeSelector(selector: destination.Selector);
            // Filtered by this row's own durability and resolved document too — the resolver's cache key carries
            // both, so a bare destinationName filter would echo generations belonging to a different row (a
            // different durability, or an unrelated document reached through a different spelling) sharing the same name.
            var sourceInstance = (instance ?? instances.Boot)!;
            var active = DescribeActiveGenerations(
                destinationName: destination.Name.Value,
                durability: destination.Durability,
                referencedDocument: WorldInstanceHost.ResolveReferenceDocument(
                    documentPath: documentPath,
                    source: sourceInstance
                )
            );

            _ = builder.Append(value: $" {destination.Name}[durability={WorldDestinationTokens.DurabilityToken(durability: destination.Durability)} scope={scopeToken}{selectorText} reference={destination.Reference}->{documentPath} consumers={consumers} active={active}]");
        }

        return builder.Append(value: (any
            ? ""
            : " none")).Append(value: ']').ToString();
    }
    // Reads WorldFaceCatalog's OWN rows — the same derivation the renderer and the portal trigger consume — so this
    // census can never disagree with what was actually seated.
    private string DescribeFaces() {
        var definition = server.Definition;
        var catalog = WorldFaceCatalog.For(definition: definition);
        var builder = new StringBuilder(value: "[world.faces:");
        var any = false;

        foreach (var row in catalog.Rows) {
            any = true;

            var placement = WorldDefinitionRows.FindPlacement(
                placements: definition.Placements,
                id: row.PlacementId
            );
            var slot = (row.SlotStarved
                ? "darkened (band full)"
                : ((row.ScreenIndex < 0)
                    ? "unlit (source shows nothing)"
                    : row.ScreenIndex.ToString(provider: CultureInfo.InvariantCulture)
            ));
            var handle = ((row.ScreenIndex >= 0)
                ? binder.CurrentHandle(index: row.ScreenIndex)
                : nint.Zero
            );
            var session = ((placement is null)
                ? string.Empty
                : DescribeSession(
                    index: row.ScreenIndex,
                    placement: placement,
                    faceName: row.FaceName
                )
            );

            _ = builder.Append(value: $" {row.PlacementId}/{row.FaceName}[screen={slot} handle={((handle != 0)
                ? "bound"
                : "no-signal")}{session}]");
        }

        return builder.Append(value: (any
            ? ""
            : " none")).Append(value: ']').ToString();
    }
    private string DescribeInhabitants(WorldInstance? instance) {
        var pop = (instance?.Server.Population ?? population);
        var placements = (instance?.Server.Definition.Placements ?? server.Definition.Placements);
        var builder = new StringBuilder(value: "[world.inhabitants:");
        var any = false;

        for (var index = 0; (index < pop.Capacity); index++) {
            if (
                (pop.InhabitantPlacementId(index: index) is not { } placementId) ||
                (pop.EntryBody(index: index) is not { } body)
            ) {
                continue;
            }

            any = true;

            var placement = WorldDefinitionRows.FindPlacement(
                id: placementId,
                placements: placements
            );
            var prototypeId = (placement?.PrototypeId ?? "?");
            var kit = ((placement?.Inhabit?.Kit) ?? "(locomotion)");
            var source = (placement?.Inhabit?.Source.ToString() ?? "?");
            var position = body.Position;

            _ = builder.Append(value: $" {placementId}[creation={prototypeId} kit={kit} source={source} body={index} pos={position.X:0.0},{position.Y:0.0},{position.Z:0.0}]");
        }

        return builder.Append(value: (any
            ? ""
            : " none")).Append(value: ']').ToString();
    }
    // The live edge latch (WorldPortalOccupancy) for one door, 1-based to match every other player-facing seat
    // number. A door fires on the edge INTO its band, so this is what decides whether the next scan can fire at all —
    // including the latches an ARRIVING traveler seeds for itself, which nothing else could echo.
    private static string DescribeOccupancy(WorldInstance? instance, string placementId, string faceName) {
        if (instance is null) {
            return "?";
        }

        var seats = new List<string>();

        for (var seat = 0; (seat < WorldBodiesLimits.LocalSeatCount); seat++) {
            if (instance.PortalOccupancy.IsInside(
                faceName: faceName,
                placementId: placementId,
                seat: seat
            )) {
                seats.Add(item: (seat + 1).ToString(provider: CultureInfo.InvariantCulture));
            }
        }

        return ((seats.Count == 0)
            ? "none"
            : string.Join(
                separator: ",",
                values: seats
            )
        );
    }
    private string DescribePortals(WorldInstance? instance) {
        var resolved = (instance ?? instances.Boot);
        var definition = (resolved?.Server.Definition ?? server.Definition);
        var defaultTravel = (definition.Portals?.PortalDefaults.Travel ?? WorldPortalTravel.Body);
        var defaults = (definition.Portals?.PortalDefaults ?? new WorldPortalDefaults(Travel: WorldPortalTravel.Body));
        var builder = new StringBuilder(value: "[world.portals:");
        var any = false;

        foreach (var placement in definition.Placements) {
            foreach (var face in (placement.FaceSources ?? [])) {
                if (face.Portal is not { } portal) {
                    continue;
                }

                any = true;

                var travel = (portal.Travel ?? defaultTravel);
                var destination = WorldDefinitionRows.FindDestination(
                    destinations: definition.Destinations,
                    name: portal.Destination
                );
                var durability = ((destination is not null)
                    ? WorldDestinationTokens.DurabilityToken(durability: destination.Durability)
                    : "?"
                );
                var arrivalToken = WorldDestinationTokens.ArrivalToken(arrival: portal.Arrival);
                var counterpartText = ((portal.Arrival == WorldPortalArrival.Mapped)
                    ? $" counterpart={portal.Counterpart}"
                    : ""
                );
                var capacityText = ((portal.Capacity is { } capacity)
                    ? capacity.ToString(provider: CultureInfo.InvariantCulture)
                    : "population"
                );

                _ = builder.Append(value: $" {placement.Id}/{face.Face}[-> {portal.Destination} durability={durability} travel={WorldDestinationTokens.TravelToken(travel: travel)} arrival={arrivalToken}{counterpartText} holdSeconds={defaults.HoldSeconds.ToString(provider: CultureInfo.InvariantCulture)} full={defaults.Full.ToString().ToLowerInvariant()} partyAllOrNothing={defaults.PartyAllOrNothing.ToString().ToLowerInvariant()} capacity={capacityText} inside={DescribeOccupancy(
                    instance: resolved,
                    placementId: placement.Id,
                    faceName: face.Face
                )}]");
            }
        }

        return builder.Append(value: (any
            ? ""
            : " none")).Append(value: ']').ToString();
    }
    // A scope=group row's selector, echoed as its authored $type shape — empty string for every other scope (Scope
    // itself already names the absence, so a bare "selector=none" would only repeat it).
    private static string DescribeSelector(WorldGroupSelector? selector) => selector switch {
        WorldGroupSelector.Named named => $" selector={{$type:named,group:{named.Group}}}",
        WorldGroupSelector.Tagged tagged => $" selector={{$type:tagged,tag:{tagged.Tag}}}",
        _ => string.Empty,
    };
    // A session-sourced face's projection state, appended to its world.faces line — destination, resolved generation
    // (or 'unresolved'), camera, and lease state. Empty string for every non-session face; "unresolved" when the face's DECLARED
    // source names a session but the binder never resolved it (a refused bind — see the destination's own stderr
    // refusal line and screen.state for the exact reason).
    private string DescribeSession(int index, WorldPlacement placement, string faceName) {
        if (binder.TryDescribeSession(
            description: out var session,
            index: index
        )) {
            var cameraText = (session.EffectiveCamera ?? (session.RequestedCamera ?? "(default)"));
            var leaseText = (session.InstanceGone
                ? "instance-retired (holding last image)"
                : "held"
            );
            var projectionText = ProjectionText(
                projection: session.Projection,
                width: session.RenderWidth,
                height: session.RenderHeight,
                rendersEveryFrame: session.RendersEveryFrame
            );

            return $" session={session.Destination} camera={cameraText} generation=gen{session.GenerationId}@{session.InstanceName} lease={leaseText}{projectionText}";
        }

        foreach (var faceOverride in (placement.FaceSources ?? [])) {
            if (
                string.Equals(
                a: faceOverride.Face,
                b: faceName,
                comparisonType: StringComparison.Ordinal
            ) &&
                (faceOverride.Source is WorldScreenSource.Session declared)
            ) {
                var projectionText = ProjectionText(
                    projection: declared.Projection,
                    width: (declared.Resolution?.Width ?? 0),
                    height: (declared.Resolution?.Height ?? 0),
                    rendersEveryFrame: (declared.Projection == WorldScreenProjection.Window)
                );

                return $" session={declared.Destination} camera={(declared.CameraName ?? "(default)")} generation=unresolved lease=none{projectionText}";
            }
        }

        return string.Empty;
    }
    // The projection/true-cost tail every session line carries: a window projection renders every produced frame,
    // never sharing ViewStack's round-robin the way an ordinary camera projection does, so its resolved pixel
    // dimensions are a real, additive per-frame GPU cost. An ordinary camera projection reports its width/height
    // too (the same resolved render target every session pays for), so the line stays one shape for both.
    private static string ProjectionText(WorldScreenProjection projection, int width, int height, bool rendersEveryFrame) =>
        $" projection={projection.ToString().ToLowerInvariant()} cost={width}x{height}{(rendersEveryFrame
            ? "/frame"
            : "")}";
    // Resolves the optional trailing `instance:<name>` token: absent addresses the boot instance (null), a single
    // matching token addresses a named running instance, and anything else — a second token, 'instance:boot', an
    // empty name, or an unknown name — is refused by name rather than silently answering for the boot world.
    private bool TryResolveInstance(in WireArgs args, string verb, out WorldInstance? instance, out CommandResult? error) {
        if (args.Count == 0) {
            instance = null;
            error = null;

            return true;
        }

        if (args.Count > 1) {
            instance = null;
            error = CommandResult.Error(output: $"[{verb}: too many arguments — expected [instance:<name>]]");

            return false;
        }

        if (!WorldArgs.IsInstanceToken(token: args[0])) {
            instance = null;
            error = CommandResult.Error(output: $"[{verb}: unrecognized '{args[0]}' — expected [instance:<name>]]");

            return false;
        }

        return WorldArgs.TryResolveInstance(
            token: args[0],
            verb: verb,
            instances: instances,
            instance: out instance,
            error: out error
        );
    }
    // Splices ` instance:<name>` just inside a bracketed echo's closing ']' — the same surgery
    // PlayerCommandModule.WithInstanceTag uses, so a script can tell which instance answered. A no-op for the boot
    // instance (null): its own echoes carry no tag.
    private static string WithInstanceTag(string text, WorldInstance? instance) =>
        ((instance is not null)
            ? WorldArgs.SpliceTag(
                text: text,
                tag: $"instance:{instance.Name}"
            )
            : text
        );

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.inhabitants",
            description: "Reports the inhabited-placement census (Immediate; reads the settled state after any pending mutation): one line per inhabited body — placementId, prototypeId, kit, source, bodyIndex, position. A trailing instance:<name> token reads a named running instance's own population instead of the boot world's (see world.instance.status) — the same grammar every instance-addressed read-back shares.",
            handler: (_, args) => {
                if (!TryResolveInstance(
                    args: in args,
                    error: out var tokenError,
                    instance: out var instance,
                    verb: "world.inhabitants"
                )) {
                    return tokenError!.Value;
                }

                return new CommandResult(Output: WithInstanceTag(
                    text: DescribeInhabitants(instance: instance),
                    instance: instance
                ));
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.faces",
            description: "Reports the derived-face census (Immediate): one line per derived creation face — placementId, faceName, screenIndex, resolvedSource, and the bound content handle (0 = the no-signal card). No instance-addressed form: screens, the derived-face index space, and session binding are the boot instance's own presentation state — a spawned instance carries an empty machine host and no client perceiving from it (see WorldInstance's remarks).",
            handler: (_, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: DescribeFaces());
                }

                if (WorldArgs.IsInstanceToken(token: args[0])) {
                    return CommandResult.Error(output: "[world.faces: no instance-addressed form — screens are the boot instance's own; see world.inhabitants/world.attachments/world.portals/world.destinations for instance:<name>]");
                }

                return CommandResult.Error(output: $"[world.faces: unrecognized '{args[0]}' — expected no arguments]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.attachments",
            description: "Reports every placement's ATTACH facet resolution (Immediate; reads the settled state after any pending mutation): one line per attached placement — placementId, bodyIndex, and either the resolved world pos/yaw (the body transform composed with the authored local offset, fixed-point) or the reason the row contributes nothing (an out-of-range or inactive/despawned body). This is the AUTHORITATIVE tick-aligned pose; the rendered stamp composes the same offset over the client's interpolated body pose, so between ticks the drawn position leads or trails this one exactly as an avatar's does. A trailing instance:<name> token reads a named running instance's own population instead of the boot world's.",
            handler: (_, args) => {
                if (!TryResolveInstance(
                    args: in args,
                    error: out var tokenError,
                    instance: out var instance,
                    verb: "world.attachments"
                )) {
                    return tokenError!.Value;
                }

                return new CommandResult(Output: WithInstanceTag(
                    text: DescribeAttachments(instance: instance),
                    instance: instance
                ));
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.portals",
            description: "Reports every placement face's PORTAL facet (Immediate): one line per door — placementId/faceName, the named destination (a destinations row), that row's resolved durability (ephemeral|persisted, from the destinations section — '?' when the name resolves to nothing), the RESOLVED travel (facet.travel, else portals.portalDefaults.travel, else 'body' when the world declares no portals section — the same order WorldDefinitionValidator resolves against), the arrival mode (spawn|mapped), its authored counterpart placementId/face for a mapped facet, and inside=<the 1-based local seats currently latched inside that door's band, 'none' when nobody is>. The inside= column is the live edge latch: a door fires on the edge INTO its band, so a latched seat cannot fire again until it leaves — and an ARRIVING traveler seeds its own latches at transfer commit, which nothing else echoes. Authored data only: no boot-time destination-document or counterpart-existence check (a counterpart's placement/face is resolved against the DESTINATION document at transfer time, not here), and this verb echoes the DECISION — WorldInstanceHost.TriggerPortal, not this verb, is what actually fires it. A trailing instance:<name> token reads a named running instance's own document instead of the boot world's.",
            handler: (_, args) => {
                if (!TryResolveInstance(
                    args: in args,
                    error: out var tokenError,
                    instance: out var instance,
                    verb: "world.portals"
                )) {
                    return tokenError!.Value;
                }

                return new CommandResult(Output: WithInstanceTag(
                    text: DescribePortals(instance: instance),
                    instance: instance
                ));
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.destinations",
            description: "Reports the destinations section (Immediate): one line per row — name, durability (ephemeral|persisted), scope (user|group|global) and its selector when scope=group ({\"$type\":\"named\"|\"tagged\", ...}), the selected references row (name -> document path, '?' when the name resolves to nothing), every placement/face PORTAL facet that names it ('none' when no facet does), and the RESOLVER's own live state for that row — one entry per active (scope key, generation id, instance name), plus that instance's current occupancy where it is still running ('none' when nothing has resolved this row yet). Authored data is boot-time only; resolver state is live and changes as travelers cross. Refuses nothing — an absent destinations section prints an honest empty line. A trailing instance:<name> token reads a named running instance's own destinations section instead of the boot world's; the resolver's own live generation/occupancy state is host-wide either way.",
            handler: (_, args) => {
                if (!TryResolveInstance(
                    args: in args,
                    error: out var tokenError,
                    instance: out var instance,
                    verb: "world.destinations"
                )) {
                    return tokenError!.Value;
                }

                return new CommandResult(Output: WithInstanceTag(
                    text: DescribeDestinations(instance: instance),
                    instance: instance
                ));
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.adjacencies",
            description: "Reports every invisible authority boundary and its live delivered neighbour: reciprocal row, boundary frame, compiler-derived overlap, neighbour tick, and durable addresses of active remote entities. Unreachable neighbours read CLOSED by name. A trailing instance:<name> token reads a named running authority.",
            handler: (_, args) => {
                if (!TryResolveInstance(
                    args: in args,
                    error: out var tokenError,
                    instance: out var instance,
                    verb: "world.adjacencies"
                )) {
                    return tokenError!.Value;
                }

                return new CommandResult(Output: WithInstanceTag(
                    text: DescribeAdjacencies(instance: instance),
                    instance: instance
                ));
            }
        );
    }
}
