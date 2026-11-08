using System.Globalization;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Server;
using static Puck.World.WorldCommandDefinition;

namespace Puck.World;

/// <summary>
/// The console surface for this process's running world instances (docs/architecture/worlds.md, "Multi-world ticking in one
/// process"). <c>world.instance.start</c> constructs a whole new <see cref="Server.WorldServer"/> and folds its
/// stepping into the same fixed-step call the boot world already runs on; <c>world.instance.stop</c> retires one and
/// disposes what it owned; <c>world.instance.status</c> reads any of them back — the boot instance included, under
/// the reserved name <see cref="WorldInstanceHost.BootInstanceName"/>, because the model has one kind of thing and
/// the read-back should not invent a second; <c>world.instance.seats</c> reads local-seat occupancy back. Entering,
/// driving, and leaving a local seat inside a named (non-boot) instance is reached through the ordinary player
/// surface: <c>player.join</c>/<c>leave</c>/<c>fly</c>/<c>stop</c>/<c>where</c>/<c>pose</c>
/// each accept a trailing <c>instance:&lt;name&gt;</c> token (see <see cref="PlayerCommandModule"/>'s class remarks).
/// </summary>
/// <remarks><para>Every instance-targeted player verb applies through the identical doors the boot instance's own
/// seats use — <see cref="Server.WorldServer.ApplySession"/> for join/leave (including the durable-state stage that
/// snapshots the seated identity's declared durable slots onto the body once, at entry, through
/// <see cref="Server.WorldOwnedWorlds.TryReadDurableState"/> — the instance then advances its own copy; the source
/// identity's later edits never reach back in) and <see cref="Server.WorldServer.ApplyCommand"/> for drive/where —
/// never a bypass, so the same Drive/body:slot authority check, capacity bound, and body-not-live no-op the boot
/// instance's <c>player.*</c> verbs rely on apply here too. There is still no per-instance mutate door and no
/// per-instance grant-table surface (a spawned instance's grants are whatever <see cref="Server.WorldGrants"/> seeds
/// at construction — the same permissive local-play defaults every instance gets, never narrowed or widened live);
/// every other <c>world.*</c>/<c>player.*</c> verb still addresses the boot instance implicitly.</para>
/// <para>A network peer entering a spawned instance — composing the existing peer-admission door with this seating
/// seam — is not built: the seam is <see cref="Server.WorldServer.ApplySession"/>
/// itself (already reachable per-instance, exactly as the instance-targeted player verbs reach it), but wiring a live
/// socket connection to address anything but the boot instance is unbuilt. See
/// <c>src/Puck.World.Server/WorldPeerHost.cs</c> for where that door lives today.</para></remarks>
internal sealed class WorldInstanceCommandModule(WorldInstanceHost instances, Client.WorldSeatAuthorityRouter seatRouter) : ICommandModule {
    private readonly WorldInstanceHost m_instances = instances;
    private readonly Client.WorldSeatAuthorityRouter m_seatRouter = seatRouter;
    private static readonly CommandForms WorldInstanceStartForms = CommandForms.Of(verb: "world.instance.start", "<name> <path>");
    private static readonly CommandForms WorldInstanceStopForms = CommandForms.Of(verb: "world.instance.stop", "<name>");
    private static readonly CommandForms WorldInstanceStatusForms = CommandForms.Of(verb: "world.instance.status", "[<name>]");
    private static readonly CommandForms WorldViewForms = CommandForms.Of(verb: "world.view", "[<seat>]");
    private static readonly CommandForms WorldInstanceSeatsForms = CommandForms.Of(verb: "world.instance.seats", "[<name>]");
    private static readonly CommandForms WorldTransferForms = CommandForms.Of(verb: "world.transfer", "<source-instance> <body:index|seat|party> <target-instance>", "ephemeral <site> <path>", "persisted <name> <path>");

    // world.instance.seats' per-instance row: one token per local seat, ordinal 1..LocalSeatCount.
    private static string DescribeSeats(WorldServer server) {
        var population = server.Population;
        var parts = new string[WorldBodiesLimits.LocalSeatCount];

        for (var slot = 0; (slot < WorldBodiesLimits.LocalSeatCount); slot++) {
            parts[slot] = (population.IsActive(index: slot)
                ? $"{PlayerRoster.DisplayNumber(slot: slot)}={(population.EntryBody(index: slot)?.Profile?.Id ?? "pending")}"
                : $"{PlayerRoster.DisplayNumber(slot: slot)}=-"
            );
        }

        return string.Join(
            separator: " ",
            value: parts
        );
    }
    // world.transfer's ephemeral/persisted destination clause, starting at token <start>: 'ephemeral <site> <path>'
    // or 'persisted <name> <path>' — the bare '<target-instance>' form is parsed directly by the handler above
    // instead (never through here), because it alone accepts trailing verification-only modifiers that a Tail-
    // consumed document path here must never be asked to share tokens with.
    private static bool TryParseDestination(in WireArgs args, int start, CommandForms forms, out WorldInstanceHost.TransferDestination destination, out CommandResult? error) {
        destination = default;

        if (args.Is(
            index: start,
            value: "ephemeral"
        )) {
            if (args.Count < (start + 3)) {
                error = forms.Refuse(args: in args);

                return false;
            }

            destination = WorldInstanceHost.TransferDestination.Fresh(
                site: args[(start + 1)].ToString(),
                documentPath: args.Tail(start: (start + 2))
            );
            error = null;

            return true;
        }

        if (args.Is(
            index: start,
            value: "persisted"
        )) {
            if (args.Count < (start + 3)) {
                error = forms.Refuse(args: in args);

                return false;
            }

            destination = WorldInstanceHost.TransferDestination.Persistent(
                name: args[(start + 1)].ToString(),
                documentPath: args.Tail(start: (start + 2))
            );
            error = null;

            return true;
        }

        error = forms.Refuse(args: in args);

        return false;
    }
    // Explicit body targets are zero-based; bare numbers retain the local-seat display convention.
    // The authority validates its actual capacity and occupancy when the transfer drains.
    private static bool TryTransferBodyIndex(in WireArgs args, int index, out int bodyIndex) {
        var token = args[index];

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "body:"
        ) &&
            CommandArgs.TryParseInt(
            text: token[5..],
            value: out bodyIndex
        ) &&
            (((uint)bodyIndex) < WorldBodiesLimits.CapacityCeiling)
        ) {
            return true;
        }
        if (
            args.TryInt(
            index: index,
            value: out var seat
        ) &&
            (seat >= 1) &&
            (seat <= WorldBodiesLimits.LocalSeatCount)
        ) {
            bodyIndex = (seat - 1);
            return true;
        }
        bodyIndex = -1;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Simulation(
            name: "world.instance.start",
            description: $"Starts another world running in THIS process from a world document and admits it under a name: {WorldInstanceStartForms.Spelling}. The instance ticks every fixed step alongside the boot world (its own zero-based tick count, own definition, own population, own owned-world store — nothing shared) and mounts its declared addons. It has no seats, replay tape or machines: a document declaring machine-sourced screens starts anyway with every one of them dark, and the echo counts them. Reachable only through world.instance.status/stop. Refuses, naming which: an empty name, the reserved name 'boot', a name carrying '~' (the character joining the instance names the engine generates), a name that is not a single safe path segment (the name IS the directory its owned worlds live in), a name already running, a name whose owned-world directory would land outside the instances root, a path resolving to no file, a document the validator rejects, or an owned-world store that cannot be opened.",
            forms: WorldInstanceStartForms,
            handler: (context, args) => {
                if (args.Count < 2) {
                    return WorldInstanceStartForms.Refuse();
                }

                var name = args[0].ToString();

                if (
                    !m_instances.TryStartAuthored(
                    name: name,
                    path: args.Tail(start: 1),
                    instance: out var started,
                    reason: out var reason
                ) ||
                    (started is null)
                ) {
                    return CommandResult.Error(output: $"[world.instance.start: refused ({reason})]");
                }

                // Echo the whole decision, not just the acceptance: the resolved path (which the fallback probe may
                // have moved), the document's own identity, where its owned worlds landed, and how many screens the
                // document asked to boot a machine on — an instance's machine host is empty by design, so a document
                // that declares them starts with them dark and that has to be READ BACK, not inferred from a silence.
                var dark = started.Server.ReadView(context).Definition.Screens.Count(predicate: screen => (screen.Source is WorldScreenSource.Machine));

                return new CommandResult(Output: string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"[world.instance.start: '{name}' running from {started.SourcePath} — document {started.Server.ReadView(context).Definition.DocumentId} schema {started.Server.ReadView(context).Definition.Schema} capacity {started.Server.Population.Capacity} addons {(started.Server.Addons?.MountedCount ?? 0)} machine-screens {dark} (dark — an instance has no machine host) owned-worlds {Puck.Abstractions.PuckPaths.Normalize(path: m_instances.OwnedWorldsDirectory(name: name))}]"
                ));
            }
        );
        yield return Simulation(
            name: "world.instance.stop",
            description: $"Retires a running world instance and disposes what it owned, never the companion custody it holds: {WorldInstanceStopForms.Spelling}. An instance embodying visiting companions sends them home and retires after the drain that settles them ('stopping', then a narrated 'retired'); one holding a companion's title abroad is refused, since no durable image here carries the title until its custodian returns it. Refuses the reserved name 'boot' (the world this process booted with — every other verb, the client, the seats and the tape address it), an unknown name, or an instance currently presenting a local traveler (transfer that seat out first), naming which. world.instance.status <name> reads the custody an instance holds.",
            forms: WorldInstanceStopForms,
            handler: (context, args) => {
                if (args.Count < 1) {
                    return WorldInstanceStopForms.Refuse();
                }

                var name = args[0].ToString();

                return m_instances.Stop(
                    name: name,
                    reason: out var reason
                ) switch {
                    WorldInstanceStop.Retired => new CommandResult(Output: $"[world.instance.stop: '{name}' retired]"),
                    WorldInstanceStop.Stopping => new CommandResult(Output: $"[world.instance.stop: stopping ({reason})]"),
                    _ => CommandResult.Error(output: $"[world.instance.stop: refused ({reason})]"),
                };
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.instance.status",
            forms: WorldInstanceStatusForms,
            description: $"Reports every running world instance, or one by name: {WorldInstanceStatusForms.Spelling}. Per instance: source path, document id, schema, completed ticks, population capacity, simulated count, journal dirty count and owned-worlds directory. The world this process booted with is listed like any other, under the name 'boot'. Immediate.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    var names = m_instances.Names;

                    return new CommandResult(Output: $"[world.instance.status: {names.Count} running: {string.Join(
                        separator: ", ",
                        values: names
                    )}]");
                }

                if (args.Count > 1) {
                    return WorldInstanceStatusForms.Refuse(got: args[1].ToString());
                }

                var name = args[0].ToString();

                if (
                    !m_instances.TryGet(
                    instance: out var instance,
                    name: name
                ) ||
                    (instance is null)
                ) {
                    return CommandResult.Error(output: $"[world.instance.status: no instance named '{name}']");
                }

                var definition = instance.Server.ReadView(context).Definition;

                var custody = (m_instances.TryDescribeCustody(
                    custody: out var held,
                    name: name,
                    stopping: out var stopping
                ) ? held : string.Empty);

                return new CommandResult(Output: string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"[world.instance.status {name}: source {instance.SourcePath} document {definition.DocumentId} schema {definition.Schema} tick {instance.CompletedTicks} capacity {instance.Server.Population.Capacity} simulated {instance.Server.Population.SimulatedCount} dirty {instance.Server.JournalLength} owned-worlds {Puck.Abstractions.PuckPaths.Normalize(path: m_instances.OwnedWorldsDirectory(name: name))} custody: {custody}{(stopping ? " (stopping)" : string.Empty)}]"
                ));
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.view",
            description: $"Reads each local seat's immutable authority claim: {WorldViewForms.Spelling} echoes <authority>:<entity>@<epoch>. Boot begins at boot:<seat>@1; every committed handoff publishes a whole new claim by CAS. Immediate.",
            forms: WorldViewForms,
            handler: (context, args) => {
                if (args.Count == 0) {
                    var parts = new string[WorldSeatBindings.SeatCount];

                    for (var slot = 0; (slot < WorldSeatBindings.SeatCount); slot++) {
                        // A world declaring fewer local seats than the host ceiling never publishes the rest; an
                        // unclaimed slot reads '-', the same mark world.instance.seats uses for an unoccupied seat.
                        parts[slot] = ((m_seatRouter.TryRoute(slot: slot) is { } location)
                            ? string.Create(
                                provider: CultureInfo.InvariantCulture,
                                handler: $"{PlayerRoster.DisplayNumber(slot: slot)}={location.Endpoint.Identity}:{(location.EntityIndex + 1)}@{location.Epoch}"
                            )
                            : string.Create(
                                provider: CultureInfo.InvariantCulture,
                                handler: $"{PlayerRoster.DisplayNumber(slot: slot)}=-"
                            )
                        );
                    }

                    return new CommandResult(Output: $"[world.view: {string.Join(
                        separator: " ",
                        values: parts
                    )}]");
                }

                if (args.Count > 1) {
                    return WorldViewForms.Refuse(got: args[1].ToString());
                }

                if (
                    !CommandArgs.TryParseInt(
                    text: args[0],
                    value: out var seat
                ) ||
                    (seat < 1) ||
                    (seat > WorldSeatBindings.SeatCount)
                ) {
                    return WorldViewForms.Refuse(got: args[0].ToString());
                }

                if (m_seatRouter.TryRoute(slot: PlayerRoster.SlotFromDisplay(number: seat)) is not { } seatLocation) {
                    return CommandResult.Error(output: $"[world.view {seat}: seat {seat} has no authority claim — this world declares fewer local seats]");
                }

                // The clock the seat's world presents by: its step and the spread of its snapshots' arrivals.
                Span<double> intervals = stackalloc double[63];
                var count = seatLocation.Endpoint.Mirror.SnapshotArrivalIntervals(milliseconds: intervals);
                var clock = "clock: no snapshots yet";

                if (count > 0) {
                    var held = intervals[..count];
                    var mean = 0.0;
                    var low = double.MaxValue;
                    var high = 0.0;

                    foreach (var interval in held) {
                        mean += interval;
                        low = Math.Min(val1: low, val2: interval);
                        high = Math.Max(val1: high, val2: interval);
                    }
                    mean /= count;

                    var spread = 0.0;

                    foreach (var interval in held) {
                        spread += ((interval - mean) * (interval - mean));
                    }

                    clock = string.Create(
                        provider: CultureInfo.InvariantCulture,
                        handler: $"clock: step {(seatLocation.Endpoint.Mirror.StepSeconds * 1000f):0.0} ms, {count} arrivals {mean:0.0} ms apart (min {low:0.0} max {high:0.0} sd {Math.Sqrt(d: (spread / count)):0.0}), alpha {seatLocation.Endpoint.InterpolationAlpha:0.00}"
                    );
                }

                return new CommandResult(Output: string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"[world.view {seat}: {seatLocation.Endpoint.Identity}:{(seatLocation.EntityIndex + 1)}@{seatLocation.Epoch} {clock}]"
                ));
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.instance.seats",
            forms: WorldInstanceSeatsForms,
            description: $"Reports every local seat's occupancy for one named instance or, with no name, EVERY running instance (the boot instance included, under 'boot'): {WorldInstanceSeatsForms.Spelling}. Per seat: 1..4=<identity-id> when active on an identity, 1..4=pending when active with none chosen yet, 1..4=- when unoccupied. Immediate.",
            handler: (context, args) => {
                if (args.Count > 1) {
                    return WorldInstanceSeatsForms.Refuse(got: args[1].ToString());
                }

                var names = ((args.Count == 1)
                    ? ([args[0].ToString()])
                    : m_instances.Names
                );
                var segments = new List<string>(capacity: names.Count);

                foreach (var name in names) {
                    if (
                        !m_instances.TryGet(
                        instance: out var instance,
                        name: name
                    ) ||
                        (instance is null)
                    ) {
                        return CommandResult.Error(output: $"[world.instance.seats: no instance named '{name}']");
                    }

                    segments.Add(item: $"{name}: {DescribeSeats(server: instance.Server)}");
                }

                return new CommandResult(Output: $"[world.instance.seats: {string.Join(
                    separator: " | ",
                    values: segments
                )}]");
            },
            routing: CommandRouting.Immediate
        );
        yield return Simulation(
            name: "world.transfer",
            description: "Queues a same-process transfer: " + WorldTransferForms.Spelling + ". body:<index> addresses any zero-based body index; a bare seat number is 1-based within the four local seats; party selects the source world's active local-seat cohort. Destination is an existing instance name, ephemeral <site> <path>, or persisted <name> <path>. The acting principal must hold Drive for each source body, and destination admission must authorize the arrival. Actual capacity, occupancy, grants, and destination resolution are checked at the transfer drain. Reservation precedes detach; a confirmed refusal restores detached members, while an ambiguous commit retains recovery state until resolved. Accepted moves echo their source and destination; refusals echo a reason. A forwarding authority remains alive after its last traveler departs. An ephemeral destination can otherwise reap when empty; persisted destinations remain until explicitly stopped. The existing-name destination also accepts forcejoinrefusal:<ordinal> as a verification-only modifier; every transfer id is minted by its source and never reused. Transfer handshakes are checkpointed host state, not boot-only replay input.",
            forms: WorldTransferForms,
            handler: (context, args) => {
                if (args.Count < 3) {
                    return WorldTransferForms.Refuse(args: in args);
                }

                var sourceName = args[0].ToString();
                var party = args.Is(
                    index: 1,
                    value: "party"
                );
                var bodyIndex = 0;

                if (
                    !party &&
                    !TryTransferBodyIndex(
                    args: in args,
                    bodyIndex: out bodyIndex,
                    index: 1
                )
                ) {
                    return WorldTransferForms.Refuse(args: in args, got: args[1].ToString());
                }

                WorldInstanceHost.TransferDestination destination;
                int? forceJoinRefusalOrdinal = null;

                if (
                    args.Is(
                    index: 2,
                    value: "ephemeral"
                ) ||
                    args.Is(
                    index: 2,
                    value: "persisted"
                )
                ) {
                    if (!TryParseDestination(
                        args: in args,
                        destination: out destination,
                        error: out var destinationError,
                        start: 2,
                        forms: WorldTransferForms
                    )) {
                        return destinationError!.Value;
                    }
                } else {
                    // The bare '<target-instance>' form — the only one that also accepts the trailing VERIFICATION-ONLY
                    // modifier (see this verb's own description), so the ephemeral/persisted forms' own Tail-consumed
                    // document path never has to account for it.
                    var end = args.Count;

                    if (
                        (end > 3) &&
                        args[(end - 1)].StartsWith(
                            comparisonType: StringComparison.Ordinal,
                            value: "forcejoinrefusal:"
                        ) &&
                        CommandArgs.TryParseInt(
                            text: args[(end - 1)][17..],
                            value: out var parsedOrdinal
                        )
                    ) {
                        forceJoinRefusalOrdinal = parsedOrdinal;
                        end--;
                    }

                    if (end != 3) {
                        return WorldTransferForms.Refuse(args: in args, got: args[3].ToString());
                    }

                    destination = WorldInstanceHost.TransferDestination.Existing(name: args[2].ToString());
                }

                m_instances.EnqueueTransfer(
                    sourceInstance: sourceName,
                    scope: (party
                    ? WorldInstanceHost.TransferScope.Party
                    : WorldInstanceHost.TransferScope.Body),
                    sourceSlot: bodyIndex,
                    destination: destination,
                    actingPrincipal: context.Principal,
                    testForceJoinRefusalOrdinal: forceJoinRefusalOrdinal
                );

                return CommandResult.None;
            }
        );
    }
}
