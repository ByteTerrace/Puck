using System.Globalization;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The contact/solidity READ-BACK surface — three <see cref="CommandRouting.Immediate"/> reads of the live
/// <see cref="Protocol.WorldMutation.SetCollision"/> tuning, the field, and the body table: <c>world.contacts</c> (the
/// analytic collider census, or one body's grounded/obstruction witness), <c>world.collision.probe</c> (a live
/// point sample against the solid field), and <c>world.collision.status</c> (the selected provider, the forcing
/// requirements, and the per-kit collider table). This module WRITES nothing: the tuning row is authored through
/// <c>world.row.set collision &lt;json&gt;</c>, and a kit's collider/program/response ride that kit's whole row via
/// <c>world.row.set kits &lt;json&gt;</c> — harvest the payload from a prior <c>world.save</c> rather than
/// hand-authoring it. A SEPARATE module from <see cref="WorldMutationCommandModule"/> to keep every class under its
/// analyzer ceilings.
/// </summary>
internal sealed class WorldCollisionCommandModule(WorldServer server, IServerLink link, Client.WorldSeatAuthorityRouter seatRouter) : ICommandModule {
    // The live analytic-vocabulary census, compiled by the same server path that materializes placement colliders.
    //
    // Names whether the world actually SOLVES against this vocabulary. A world that authors a field-selecting contact
    // requirement stands on the SDF field instead, blend and all, and the analytic figures below then describe a
    // vocabulary nothing is resolved against — a reading taken to answer "what am I standing on" that quietly
    // describes the wrong surface. The census is still worth printing there (it is what the analytic path WOULD see,
    // and the gap between the two is exactly what a blend contributes), but it is labelled for what it is.
    private CommandResult Census() {
        var census = server.Population.ContactCensus;
        var field = WorldContactSelection.RequiresField(collision: server.Definition.Collision);
        var provider = (field
            ? $"field contact (requirements: {string.Join(separator: ", ", values: server.Definition.Collision.Requirements)}); analytic census NOT SOLVED AGAINST"
            : "analytic contact; census"
        );

        return new CommandResult(Output: $"[world.contacts: {provider} {census.SolidCount} colliders ({census.SphereCount} spheres, {census.BoxCount} boxes, {census.PlaneCount} planes); placements={census.PlacementColliderCount} ({census.PlacementSphereCount} spheres, {census.PlacementBoxCount} boxes, {census.PlacementPlaneCount} planes), unsupported={census.UnsupportedPlacementCount}; dynamic potentialPairs={server.Population.DynamicContactPotentialPairs} narrowPairs={server.Population.DynamicContactNarrowPairs} resolvedPairs={server.Population.DynamicContactResolvedPairs}]");
    }
    private static string DescribeCollider(WorldCollider collider) {
        return collider switch {
            WorldCollider.Sphere sphere => string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"sphere r={sphere.Radius:0.##}"
        ),
            WorldCollider.Capsule capsule => string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"capsule endpoint=({capsule.Endpoint.X:0.##},{capsule.Endpoint.Y:0.##},{capsule.Endpoint.Z:0.##}) r={capsule.Radius:0.##}"
        ),
            WorldCollider.Box box => string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"box half=({box.HalfExtents.X:0.##},{box.HalfExtents.Y:0.##},{box.HalfExtents.Z:0.##}) rotation=({box.Rotation.X:0.##},{box.Rotation.Y:0.##},{box.Rotation.Z:0.##},{box.Rotation.W:0.##})"
        ),
            WorldCollider.FromCreation fromCreation => $"fromCreation creation={fromCreation.PrototypeId}",
            _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(collider),
            actualValue: collider,
            message: null
        ),
        };
    }
    // The live-field point read (world.collision.probe): sample distance/material/gradient exactly as the resolver does.
    private CommandResult Probe(WireArgs args) {
        if (
            (args.Count != 3) ||
            !args.TryFloats(
            count: 3,
            start: 0,
            values: out var xyz
        )
        ) {
            return CommandResult.Usage(
                form: "<x> <y> <z>",
                verb: "world.collision.probe"
            );
        }

        var (x, y, z) = (xyz[0], xyz[1], xyz[2]);

        if (server.SolidField is not { } field) {
            return CommandResult.Error(output: "[world.collision.probe: no field — author a field-selecting contact requirement]");
        }

        var position = new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        );

        if (!field.Probe(
            distance: out var distance,
            gradient: out var gradient,
            material: out var material,
            position: in position
        )) {
            return CommandResult.Error(output: "[world.collision.probe: the field has no geometry to answer against]");
        }

        var upMode = (field.GradientUp
            ? "gradient"
            : "+Y"
        );

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.collision.probe: ({x:0.###}, {y:0.###}, {z:0.###}) distance={((double)distance):0.000} material={material} gradient=({((double)gradient.X):0.000}, {((double)gradient.Y):0.000}, {((double)gradient.Z):0.000}) up={upMode}]"
        ));
    }
    private static string RequirementName(WorldContactRequirement requirement) {
        return requirement switch {
            WorldContactRequirement.SmoothUnionContact => "smooth-union-contact",
            WorldContactRequirement.GradientDerivedUp => "gradient-derived-up",
            _ => throw new ArgumentOutOfRangeException(
            nameof(requirement),
            requirement,
            message: null
        ),
        };
    }
    // The contact-solver status readout (world.collision.status): the tuning, the field size/revision, and the per-kit
    // collider table so the whole grounded-contact configuration is one Immediate read.
    private CommandResult Status() {
        var collision = server.Definition.Collision;
        var provider = ((server.SolidField is null)
            ? "analytic"
            : "field"
        );
        var forcedBy = ((collision.Requirements.Count == 0)
            ? "none"
            : string.Join(
                separator: ",",
                values: collision.Requirements.Select(selector: static requirement => RequirementName(requirement: requirement))
            )
        );
        var instructions = (server.SolidField?.InstructionCount ?? 0);
        var colliders = new List<string>();

        foreach (var kit in server.Definition.Kits) {
            if (kit.Collider is { } collider) {
                colliders.Add(item: $"{kit.Name}({DescribeCollider(collider: collider)})");
            }
        }

        var kitTable = ((colliders.Count == 0)
            ? "none"
            : string.Join(
                separator: ", ",
                values: colliders
            )
        );
        var census = server.Population.ContactCensus;
        var placementFieldShapes = (server.SolidField?.PlacementShapeCount ?? 0L);

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.collision.status: selectedProvider={provider} forcedBy={forcedBy} instructions={instructions} placementFieldShapes={placementFieldShapes} placementColliders={census.PlacementColliderCount} placementColliderLimit={WorldPlacementPolicy.MaxSolidPlacementColliders} revision={server.SolidRevision} skin={collision.ContactSkin:0.###} slope={collision.MaxSlopeDegrees:0.#}° colliders=[{kitTable}]]"
        ));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.contacts",
            description: "Reports the solidity state (Immediate read): world.contacts prints the analytic collider census and placement contribution; world.contacts <body-index> prints that 1-based body's grounded flag, planar speed, grounded witness (resolved=1 when grounded, else 0), the medium facts (submerged/atSurface — always false for a kit authoring no medium hold), and an obstruction witness (obstruction=none, or the LATCHED last non-walkable contact's unit surface normal at 3-decimal precision — a vertical wall reads a non-zero obstruction even though resolved=0, and even while the same body simultaneously reads resolved=1 from standing on the floor elsewhere; a walkable push, ground or ramp, never sets it). LATCHED, not a raw per-tick read: it survives a solver tick that happens not to re-register a push while the body stays actively driven and hasn't meaningfully moved, and clears the instant either input goes idle or the body actually gets clear — so a pinned body reads a stable obstruction rather than flickering.",
            handler: (_, args) => {
                if (args.Count == 0) {
                    return Census();
                }

                if (args.Count > 1) {
                    return CommandResult.Error(output: "[world.contacts: too many arguments — expected [<body-index>]]");
                }

                if (
                    !args.TryInt(
                    index: 0,
                    value: out var index
                ) ||
                    (index < 1) ||
                    (index > server.Population.Capacity)
                ) {
                    return CommandResult.Error(output: $"[world.contacts: bad body index '{args[0].ToString()}' — 1..{server.Population.Capacity}]");
                }

                if (
                    (index <= WorldBodiesLimits.LocalSeatCount) &&
                    seatRouter.TryRouteQuery(
                    factory: static authorityIndex => new WorldQuery.Contacts(Index: authorityIndex),
                    result: out var routed,
                    slot: (index - 1),
                    tagInstance: false
                )
                ) {
                    return routed;
                }

                // No local route for this slot (a world declaring fewer local seats than the ceiling) or a
                // simulated entry beyond the local range: query the injected link with the raw 1-based index.
                var result = default(CommandResult);

                link.Query(
                    query: new WorldQuery.Contacts(Index: index),
                    completion: answer => {
                        result = new CommandResult(Output: answer.Text) { IsError = answer.Refused };
                    }
                );
                return result;
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.collision.probe",
            description: "Reads the live field the simulation solves against (Immediate): world.collision.probe <x> <y> <z> prints signed distance, material, unit gradient, and the field's ambient up mode (gradient when GradientDerivedUp is authored, +Y otherwise). Body-frame policy separately decides whether that candidate, solved gravity, or a measured support normal may orient a body. Requires a field-selecting contact requirement.",
            handler: (_, args) => Probe(args: args)
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.collision.status",
            description: "Reports the selected contact provider, the requirements that forced it, solid instruction count, field revision, contact skin, and the per-kit collider table.",
            handler: (_, args) => ((CommandResult.RequireNoArguments(args: args, verb: "world.collision.status") is { } refusal)
            ? refusal
            : Status())
        );
    }

}
