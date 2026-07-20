using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The Arc 7 INHABITATION + creation-facet verb surface: the RMW sugar that molds a placement's <c>inhabit</c> and
/// <c>faceSources</c> facets and a kit's <c>attend</c> flavor through the SAME <see cref="WorldMutation"/> messages the
/// editor drives, plus the two Immediate censuses (<c>world.inhabitants</c>, <c>world.faces</c>). A SEPARATE module from
/// <see cref="WorldMutationCommandModule"/> (at its analyzer ceiling; the plan splits the verb families).
/// </summary>
/// <remarks>Every write verb routes <see cref="CommandRouting.Simulation"/> (the stdin barrier serializes a following
/// read-after-write); the server prints the loud accept/reject line when the buffered edit applies. No new mutation
/// kinds: <c>inhabit</c>/<c>faceSources</c> ride <see cref="WorldMutation.UpsertPlacement"/>, <c>attend</c> rides
/// <see cref="WorldMutation.UpsertKit"/>.</remarks>
internal sealed class WorldPlacementCommandModule(WorldServer server, WorldPopulation population, WorldScreenBinder binder, IServerLink link) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.placement.inhabit",
            description: "Molds a placement's INHABIT facet (RMW): world.placement.inhabit <id> <kit|auto|-> [idle|wander|attend|live] [count] [radius]. '-' as <kit> clears the facet (the placement reverts to furniture); 'auto' resolves the creation's own locomotion token as the kit name. Applies LIVE; a full-document revalidation rejects loudly (e.g. an unresolved kit names every kit the world declares).",
            handler: (_, args) => Inhabit(args: args),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.placement.face",
            description: "Overrides one declared creation FACE's feed (RMW upserting a WorldPlacementFace): world.placement.face <id> <faceName> <sourceToken>. The token is none|test|camera:<name>|feed:<name>; '-' clears the override back to the creation's declared default. Applies LIVE.",
            handler: (_, args) => Face(args: args),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.kit.attend",
            description: "Molds a kit's ATTEND flavor (RMW): world.kit.attend <kit> <notice> <release> <standoff> <approach> <orbit> [face] [seat|body]. '-' in place of <notice> clears the flavor (the kit can no longer attend). releaseRadius > noticeRadius >= standoffRadius; approach/orbit in 0..1. Applies LIVE.",
            handler: (_, args) => Attend(args: args),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.inhabitants",
            description: "Reports the inhabited-placement census (Immediate; reads the settled state after any pending mutation): one line per inhabited body — placementId, creationId, kit, source, bodyIndex, position.",
            handler: (_, _) => new CommandResult(Output: DescribeInhabitants())
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.faces",
            description: "Reports the derived-face census (Immediate): one line per derived creation face — placementId, faceName, screenIndex, resolvedSource, and the bound content handle (0 = the no-signal card).",
            handler: (_, _) => new CommandResult(Output: DescribeFaces())
        );
    }

    private CommandResult Inhabit(string[] args) {
        if ((args.Length < 2) || (args.Length > 5)) {
            return Usage(verb: "world.placement.inhabit", form: "<id> <kit|auto|-> [idle|wander|attend|live] [count] [radius]");
        }

        if (FindPlacement(id: args[0]) is not { } placement) {
            return new CommandResult(Output: $"[world.placement.inhabit: no placement row named '{args[0]}']") { IsError = true };
        }

        if (args[1] == "-") {
            return Submit(mutation: new WorldMutation.UpsertPlacement(Principal: WorldPrincipal.Console, Placement: (placement with { Inhabit = null })));
        }

        var kit = (string.Equals(a: args[1], b: "auto", comparisonType: StringComparison.Ordinal) ? null : args[1]);
        var source = IntentSource.Wander;

        if ((args.Length >= 3) && (ParseSource(token: args[2]) is not { } parsed)) {
            return new CommandResult(Output: $"[world.placement.inhabit: bad source '{args[2]}' — idle|wander|attend|live]") { IsError = true };
        } else if (args.Length >= 3) {
            source = ParseSource(token: args[2])!.Value;
        }

        var count = 1;

        if ((args.Length >= 4) && (!int.TryParse(s: args[3], style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out count))) {
            return new CommandResult(Output: $"[world.placement.inhabit: bad count '{args[3]}' — an integer]") { IsError = true };
        }

        var radius = 0f;

        if ((args.Length >= 5) && (!float.TryParse(s: args[4], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out radius))) {
            return new CommandResult(Output: $"[world.placement.inhabit: bad radius '{args[4]}' — a number]") { IsError = true };
        }

        var inhabit = new WorldPlacementInhabit(Kit: kit, Look: placement.Inhabit?.Look, Source: source, Count: count, Radius: radius);

        return Submit(mutation: new WorldMutation.UpsertPlacement(Principal: WorldPrincipal.Console, Placement: (placement with { Inhabit = inhabit })));
    }

    private CommandResult Face(string[] args) {
        if (args.Length != 3) {
            return Usage(verb: "world.placement.face", form: "<id> <faceName> <sourceToken>");
        }

        if (FindPlacement(id: args[0]) is not { } placement) {
            return new CommandResult(Output: $"[world.placement.face: no placement row named '{args[0]}']") { IsError = true };
        }

        var overrides = new List<WorldPlacementFace>(collection: (placement.FaceSources ?? []).Where(predicate: face => !string.Equals(a: face.Face, b: args[1], comparisonType: StringComparison.Ordinal)));

        if (args[2] != "-") {
            if (ParseSourceToken(token: args[2]) is not { } source) {
                return new CommandResult(Output: $"[world.placement.face: bad source '{args[2]}' — none|test|camera:<name>|feed:<name>]") { IsError = true };
            }

            overrides.Add(item: new WorldPlacementFace(Face: args[1], Source: source));
        }

        return Submit(mutation: new WorldMutation.UpsertPlacement(Principal: WorldPrincipal.Console, Placement: (placement with { FaceSources = ((overrides.Count > 0) ? overrides : null) })));
    }

    private CommandResult Attend(string[] args) {
        if ((args.Length < 1) || (args.Length > 8)) {
            return Usage(verb: "world.kit.attend", form: "<kit> <notice> <release> <standoff> <approach> <orbit> [face] [seat|body]");
        }

        if (FindKit(name: args[0]) is not { } kit) {
            return new CommandResult(Output: $"[world.kit.attend: no kit row named '{args[0]}']") { IsError = true };
        }

        if ((args.Length >= 2) && (args[1] == "-")) {
            return Submit(mutation: new WorldMutation.UpsertKit(Principal: WorldPrincipal.Console, Kit: (kit with { Attend = null })));
        }

        if (args.Length < 6) {
            return Usage(verb: "world.kit.attend", form: "<kit> <notice> <release> <standoff> <approach> <orbit> [face] [seat|body]");
        }

        if (!TryFloat(value: args[1], out var notice) || !TryFloat(value: args[2], out var release) || !TryFloat(value: args[3], out var standoff) ||
            !TryFloat(value: args[4], out var approach) || !TryFloat(value: args[5], out var orbit)) {
            return new CommandResult(Output: "[world.kit.attend: notice/release/standoff/approach/orbit must be numbers]") { IsError = true };
        }

        var faceTarget = true;
        var target = AttendTarget.NearestSeat;

        for (var index = 6; (index < args.Length); index++) {
            switch (args[index].ToLowerInvariant()) {
                case "face": faceTarget = true; break;
                case "noface": faceTarget = false; break;
                case "seat": target = AttendTarget.NearestSeat; break;
                case "body": target = AttendTarget.NearestBody; break;
                default:
                    return new CommandResult(Output: $"[world.kit.attend: unknown flag '{args[index]}' — face|noface|seat|body]") { IsError = true };
            }
        }

        var attend = new AttendFlavor(NoticeRadius: notice, ReleaseRadius: release, StandoffRadius: standoff, Approach: approach, Orbit: orbit, FaceTarget: faceTarget, Target: target);

        return Submit(mutation: new WorldMutation.UpsertKit(Principal: WorldPrincipal.Console, Kit: (kit with { Attend = attend })));
    }

    private string DescribeInhabitants() {
        var builder = new StringBuilder(value: "[world.inhabitants:");
        var any = false;

        for (var index = 0; (index < WorldPopulation.MaxPopulation); index++) {
            if ((population.InhabitantPlacementId(index: index) is not { } placementId) || (population.EntryBody(index: index) is not { } body)) {
                continue;
            }

            any = true;

            var placement = FindPlacement(id: placementId);
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
            if (WorldPlacementStamper.FindCreation(creations: definition.Creations, id: placement.CreationId) is not { } creation) {
                continue;
            }

            foreach (var face in (creation.Document.Behavior?.Faces ?? [])) {
                if (faceIndex >= limit) {
                    break;
                }

                any = true;

                var handle = binder.CurrentHandle(index: faceIndex);

                _ = builder.Append(value: $" {placement.Id}/{face.Name}[screen={faceIndex} handle={(handle != 0 ? "bound" : "no-signal")}]");
                faceIndex++;
            }
        }

        return builder.Append(value: (any ? "" : " none")).Append(value: ']').ToString();
    }

    private static IntentSource? ParseSource(string token) => token.ToLowerInvariant() switch {
        "idle" => IntentSource.Idle,
        "wander" => IntentSource.Wander,
        "attend" => IntentSource.Attend,
        "live" => IntentSource.Live,
        _ => null,
    };

    // The closed four-token face source grammar, mirroring WorldCreationFacets.ParseDefaultSource.
    private static WorldScreenSource? ParseSourceToken(string token) {
        if (string.Equals(a: token, b: "none", comparisonType: StringComparison.Ordinal)) {
            return new WorldScreenSource.None();
        }

        if (string.Equals(a: token, b: "test", comparisonType: StringComparison.Ordinal)) {
            return new WorldScreenSource.TestPattern(Width: 256, Height: 192);
        }

        if (token.StartsWith(value: "camera:", comparisonType: StringComparison.Ordinal)) {
            return new WorldScreenSource.View(CameraName: token["camera:".Length..]);
        }

        if (token.StartsWith(value: "feed:", comparisonType: StringComparison.Ordinal)) {
            return new WorldScreenSource.View(CameraName: token["feed:".Length..]);
        }

        return null;
    }

    private static bool TryFloat(string value, out float result) =>
        float.TryParse(s: value, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out result);

    private WorldPlacement? FindPlacement(string id) {
        foreach (var placement in server.Definition.Placements) {
            if (string.Equals(a: placement.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return placement;
            }
        }

        return null;
    }

    private WorldKit? FindKit(string name) {
        foreach (var kit in server.Definition.Kits) {
            if (string.Equals(a: kit.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return kit;
            }
        }

        return null;
    }

    private CommandResult Submit(WorldMutation mutation) {
        link.SubmitWorldMutation(mutation: mutation);

        return CommandResult.None;
    }

    private static CommandResult Usage(string verb, string form) => new(Output: $"[{verb}: expected {form}]") {
        IsError = true,
    };
}
