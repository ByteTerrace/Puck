using System.Globalization;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using static Puck.World.WorldCommandDefinition;

namespace Puck.World;

/// <summary>
/// The console surface for this process's running world instances (docs/world-model.md, "Multi-world ticking in one
/// process"). <c>world.instance.start</c> constructs a whole new <see cref="Server.WorldServer"/> and folds its
/// stepping into the SAME fixed-step call the boot world already runs on; <c>world.instance.stop</c> retires one and
/// disposes what it owned; <c>world.instance.status</c> reads any of them back — the boot instance included, under
/// the reserved name <see cref="WorldInstanceHost.BootInstanceName"/>, because the model has one kind of thing and
/// the read-back should not invent a second; <c>world.instance.seats</c> reads local-seat occupancy back. INSTANCE
/// EMBODIMENT — entering, driving, and leaving a local seat inside a NAMED (non-boot) instance — is reached through
/// the ORDINARY player surface now: <c>player.join</c>/<c>leave</c>/<c>fly</c>/<c>stop</c>/<c>where</c>/<c>pose</c>
/// each accept a trailing <c>instance:&lt;name&gt;</c> token (see <see cref="PlayerCommandModule"/>'s class remarks)
/// in place of the former standalone world.instance.seat.* verb family, which this module no longer declares.
/// </summary>
/// <remarks><para>Every instance-targeted player verb applies through the identical doors the boot instance's own
/// seats use — <see cref="Server.WorldServer.ApplySession"/> for join/leave (INCLUDING the durable-state stage that
/// snapshots the seated identity's declared durable slots onto the body ONCE, at entry, through
/// <see cref="Server.WorldOwnedWorlds.TryReadDurableState"/> — the instance then advances its own copy; the source
/// identity's later edits never reach back in) and <see cref="Server.WorldServer.ApplyCommand"/> for drive/where —
/// never a bypass, so the SAME Drive/body:slot authority check, capacity bound, and body-not-live no-op the boot
/// instance's <c>player.*</c> verbs rely on apply here too. There is still no per-instance mutate door and no
/// per-instance grant-table surface (a spawned instance's grants are whatever <see cref="Server.WorldGrants"/> seeds
/// at construction — the same permissive local-play defaults every instance gets, never narrowed or widened live);
/// every other <c>world.*</c>/<c>player.*</c> verb still addresses the boot instance implicitly.</para>
/// <para>A network peer entering a spawned instance — composing the existing peer-admission door with this seating
/// seam — is a deliberately UNBUILT stretch of this lane: the seam is <see cref="Server.WorldServer.ApplySession"/>
/// itself (already reachable per-instance, exactly as the instance-targeted player verbs reach it), but wiring a live
/// socket connection to address anything but the boot instance is unbuilt. See
/// <c>src/Puck.World.Server/WorldTcpHost.cs</c> for where that door lives today.</para></remarks>
internal sealed class WorldInstanceCommandModule(WorldInstanceHost instances) : ICommandModule {
    private readonly WorldInstanceHost m_instances = instances;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Simulation(
            name: "world.instance.start",
            description: "Starts another world running in THIS process from a world document and admits it under a name: world.instance.start <name> <path>. The instance ticks every fixed step alongside the boot world (its own zero-based tick count, own definition, own population, own owned-world store — nothing shared) but has no seats, no addons, no replay tape and no machines: a document declaring machine-sourced screens starts anyway with every one of them dark, and the echo counts them. Reachable only through world.instance.status/stop. Refuses, naming which: an empty name, the reserved name 'boot', a name that is not a single safe path segment (the name IS the directory its owned worlds live in), a name already running, a name whose owned-world directory would land outside the instances root, a path resolving to no file, a document the validator rejects, or an owned-world store that cannot be opened.",
            handler: (_, args) => {
                if (args.Count < 2) {
                    return CommandResult.Error(output: "[world.instance.start: expected <name> <path>]");
                }

                var name = args[0].ToString();

                if (!m_instances.TryStart(name: name, path: args.Tail(start: 1), instance: out var started, reason: out var reason) || (started is null)) {
                    return CommandResult.Error(output: $"[world.instance.start: refused ({reason})]");
                }

                // Echo the whole decision, not just the acceptance: the resolved path (which the fallback probe may
                // have moved), the document's own identity, where its owned worlds landed, and how many screens the
                // document asked to boot a machine on — an instance's machine host is empty by design, so a document
                // that declares them starts with them dark and that has to be READ BACK, not inferred from a silence.
                var dark = started.Server.Definition.Screens.Count(predicate: screen => (screen.Source is WorldScreenSource.Machine));

                return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.instance.start: '{name}' running from {started.SourcePath} — document {started.Server.Definition.DocumentId} schema {started.Server.Definition.Schema} capacity {started.Server.Population.Capacity} machine-screens {dark} (dark — an instance has no machine host) owned-worlds {m_instances.OwnedWorldsDirectory(name: name)}]"));
            }
        );
        yield return Simulation(
            name: "world.instance.stop",
            description: "Retires a running world instance and disposes what it owned: world.instance.stop <name>. Refuses the reserved name 'boot' (the world this process booted with — every other verb, the client, the seats and the tape address it) and an unknown name, naming which.",
            handler: (_, args) => {
                if (args.Count < 1) {
                    return CommandResult.Error(output: "[world.instance.stop: expected <name>]");
                }

                var name = args[0].ToString();

                return (m_instances.TryStop(name: name, reason: out var reason)
                    ? new CommandResult(Output: $"[world.instance.stop: '{name}' retired]")
                    : CommandResult.Error(output: $"[world.instance.stop: refused ({reason})]"));
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.instance.status",
            description: "Reports every running world instance (world.instance.status) or one by name (world.instance.status <name>): source path, document id, schema, completed ticks, population capacity, simulated count, journal dirty count and owned-worlds directory. The world this process booted with is listed like any other, under the name 'boot'. Immediate.",
            handler: (_, args) => {
                if (args.Count == 0) {
                    var names = m_instances.Names;

                    return new CommandResult(Output: $"[world.instance.status: {names.Count} running: {string.Join(separator: ", ", values: names)}]");
                }

                var name = args[0].ToString();

                if (!m_instances.TryGet(name: name, instance: out var instance) || (instance is null)) {
                    return CommandResult.Error(output: $"[world.instance.status: no instance named '{name}']");
                }

                var definition = instance.Server.Definition;

                return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.instance.status {name}: source {instance.SourcePath} document {definition.DocumentId} schema {definition.Schema} tick {instance.CompletedTicks} capacity {instance.Server.Population.Capacity} simulated {instance.Server.Population.SimulatedCount} dirty {instance.Server.JournalLength} owned-worlds {m_instances.OwnedWorldsDirectory(name: name)}]"));
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.instance.seats",
            description: "Reports every local seat's occupancy for one named instance (world.instance.seats <name>) or EVERY running instance (world.instance.seats, no argument) — the boot instance included, under 'boot'. Per seat: 1..4=<identity-id> when active on an identity, 1..4=pending when active with none chosen yet, 1..4=- when unoccupied. Immediate.",
            handler: (_, args) => {
                if (args.Count > 1) {
                    return CommandResult.Error(output: "[world.instance.seats: expected an optional instance name]");
                }

                var names = ((args.Count == 1) ? ([args[0].ToString()]) : m_instances.Names);
                var segments = new List<string>(capacity: names.Count);

                foreach (var name in names) {
                    if (!m_instances.TryGet(name: name, instance: out var instance) || (instance is null)) {
                        return CommandResult.Error(output: $"[world.instance.seats: no instance named '{name}']");
                    }

                    segments.Add(item: $"{name}: {DescribeSeats(server: instance.Server)}");
                }

                return new CommandResult(Output: $"[world.instance.seats: {string.Join(separator: " | ", values: segments)}]");
            },
            routing: CommandRouting.Immediate
        );
        yield return Simulation(
            name: "world.transfer",
            description: "Queues an atomic SAME-PROCESS transfer out of one running instance: world.transfer <source-instance> <slot|party> <destination>, where <slot> is the SOURCE instance's 1-based seat (the player.*/seat.* convention, one body) or the literal 'party' (every currently-active local seat 0..LocalSeatCount-1 of the source, moved together), and <destination> is one of: '<target-instance>' (an already-running instance, by name — the original form); 'fresh <site> <path>' (a BRAND-NEW instance, deterministically named '<site>-<n>' from a per-site draw counter this host holds — n advances by exactly one per fresh transfer resolved at that site, so two fresh transfers from one site draw two DISTINCT names, replay-stable because it is a pure function of drain order, never wall-clock/RNG/tick-of-entry); or 'persistent <name> <path>' (a STABLE instance: reused if <name> is already running, else started from <path> — two transfers naming the same persistent instance are two doors into one place). Applied at this host's ONE pending-transfer drain point (see WorldInstanceHost), BEFORE any instance steps that tick, with the destination resolved (and, for fresh/persistent, spawned or started) EXACTLY ONCE for the whole transfer — a party shares one minted name, never one instance per member. Each member's LEAVE(source) then JOIN(destination) runs synchronously with no Server.Step of any instance between the first and the last, so a body is never active in two instances at once nor in neither, and a party lands together. A traveler carries ONLY its seat's identity/profile — pose, action-track state, and tape are NOT carried across; the destination embodies each arrival through its OWN normal join (kit, appearance, and grants come from the destination's own tables), landing on its own free local seat at THAT seat's authored spawn point. A fresh destination reaps like any other instance once empty; a persistent one is RETAINED through an occupancy dip to zero (never torn down just because a party's own first join raced its spawn) until an explicit world.instance.stop. Queued, not applied inline: at the drain point each ACCEPTED member echoes its full decision on stdout (departed source seat, arrived destination seat, the arrival pose) so the outcome is read here, never inferred from a later world.instance.seats; every refusal — and, on a destination-join refusal, a same-tick reinstatement of that member's source seat — narrates on stderr, like a mutation verb's deferred reject echo. This verb is the DEVELOPER REFLECTION of the in-session portal act — the console mirror of an in-world capability (a portal facet becomes the diegetic trigger in a later step), never a separate product, per the unification doctrine. Refuses to enqueue only on a malformed <slot>/<destination> shape; every other refusal (unknown or unstartable instance, source and destination naming the same instance, an inactive/out-of-range/absent source seat, a denied Drive grant, no free seat in the destination) is named at drain time.",
            handler: (context, args) => {
                if (args.Count < 3) {
                    return CommandResult.Error(output: "[world.transfer: expected <source-instance> <slot|party> <target-instance> | fresh <site> <path> | persistent <name> <path>]");
                }

                var sourceName = args[0].ToString();
                var party = args.Is(index: 1, value: "party");
                var slot = 0;

                if (!party && !TrySlot(args: in args, index: 1, verb: "world.transfer", slot: out slot)) {
                    return CommandResult.Error(output: $"[world.transfer: slot must be an integer 1..{WorldPopulation.LocalSeatCount}, or 'party']");
                }

                if (!TryParseDestination(args: in args, start: 2, verb: "world.transfer", destination: out var destination, error: out var destinationError)) {
                    return CommandResult.Error(output: destinationError!);
                }

                m_instances.EnqueueTransfer(
                    sourceInstance: sourceName,
                    scope: (party ? WorldInstanceHost.TransferScope.Party : WorldInstanceHost.TransferScope.Body),
                    sourceSlot: (slot - 1),
                    destination: destination,
                    actingPrincipal: context.ActingPrincipal()
                );

                return CommandResult.None;
            }
        );
    }

    // world.transfer's destination clause, starting at token <start>: '<name>' (Existing), 'fresh <site> <path>', or
    // 'persistent <name> <path>' — the ONE parse both the body and party forms share, since the destination grammar
    // never depends on which seats are moving.
    private static bool TryParseDestination(in WireArgs args, int start, string verb, out WorldInstanceHost.TransferDestination destination, out string? error) {
        destination = default;

        if (args.Is(index: start, value: "fresh")) {
            if (args.Count < (start + 3)) {
                error = $"[{verb}: expected 'fresh <site> <path>']";

                return false;
            }

            destination = WorldInstanceHost.TransferDestination.Fresh(site: args[(start + 1)].ToString(), documentPath: args.Tail(start: (start + 2)));
            error = null;

            return true;
        }

        if (args.Is(index: start, value: "persistent")) {
            if (args.Count < (start + 3)) {
                error = $"[{verb}: expected 'persistent <name> <path>']";

                return false;
            }

            destination = WorldInstanceHost.TransferDestination.Persistent(name: args[(start + 1)].ToString(), documentPath: args.Tail(start: (start + 2)));
            error = null;

            return true;
        }

        if (args.Count != (start + 1)) {
            error = $"[{verb}: expected exactly one target-instance name, or 'fresh <site> <path>' / 'persistent <name> <path>']";

            return false;
        }

        destination = WorldInstanceHost.TransferDestination.Existing(name: args[start].ToString());
        error = null;

        return true;
    }

    // The 1-based seat display index world.transfer shares with the instance-targeted player.* verbs (see
    // PlayerCommandModule's own TryStripInstanceToken/ResolveInstanceSlot) — translated to the 0-based slot
    // SessionRequest/WorldCommand carry by the caller.
    private static bool TrySlot(in WireArgs args, int index, string verb, out int slot) {
        if (!args.TryInt(index: index, value: out slot) || (slot < 1) || (slot > WorldPopulation.LocalSeatCount)) {
            slot = 0;

            return false;
        }

        return true;
    }

    // world.instance.seats' per-instance row: one token per local seat, ordinal 1..LocalSeatCount.
    private static string DescribeSeats(WorldServer server) {
        var population = server.Population;
        var parts = new string[WorldPopulation.LocalSeatCount];

        for (var slot = 0; (slot < WorldPopulation.LocalSeatCount); slot++) {
            parts[slot] = (population.IsActive(index: slot)
                ? $"{(slot + 1)}={(population.EntryBody(index: slot)?.Profile?.Id ?? "pending")}"
                : $"{(slot + 1)}=-");
        }

        return string.Join(separator: " ", value: parts);
    }
}
