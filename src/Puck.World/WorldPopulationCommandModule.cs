using System.Globalization;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The world's participant/census verb surface — SERVER-SAFE (registered in <c>AddWorldAuthoritativeCore</c>, headless
/// or windowed alike): <c>world.players</c>, <c>world.devices</c>, <c>world.device-profiles</c>, and
/// <c>world.population</c>. Split out of
/// <see cref="WorldCommandModule"/> (which stays presentation-only — graphics levers, GPU timing, the diegetic-row
/// listings), because these three read pure roster/population/document state and never touch a GPU, window, or audio
/// device.
/// </summary>
internal sealed class WorldPopulationCommandModule(PlayerRoster roster, WorldPopulation population, WorldServer server, IServerLink link) : ICommandModule {
    private static string DescribeFixed(FixedQ4816 value) => ((double)value).ToString(
        format: "0.#####",
        provider: CultureInfo.InvariantCulture
    );
    // The WORLD-level attachment read-back: the world default grip policy and every tuning field, resolved-vs-authored
    // side by side. body.attachment answers the per-body question (mode/anchor/rope); this answers "what did the
    // document decide".
    private string DescribeAttachmentPolicy() {
        var authored = server.Definition.Attachment;
        var compiled = population.CompiledAttachment;

        if (!authored.Enabled) {
            return "[world.attach-policy: attachment disabled — no attach/detach/reel channel reaches any body]";
        }

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.attach-policy: enabled defaultGrip={authored.DefaultGrip} climbReach={authored.ClimbReach:0.#####} climbSpeed={authored.ClimbSpeed:0.#####} gripCost={authored.GripCost:0.#####} grappleMaxDistance={authored.GrappleMaxDistance:0.#####} grappleAssistHalfAngleDegrees={authored.GrappleAssistHalfAngleDegrees:0.#####} reelRate={authored.ReelRate:0.#####} reelInFloor={authored.ReelInFloor:0.#####} releaseMomentumScale={authored.ReleaseMomentumScale:0.#####} | channels attach={(authored.AttachChannel ?? "none")}(ordinal={compiled.AttachChannelOrdinal}) detach={(authored.DetachChannel ?? "none")}(ordinal={compiled.DetachChannelOrdinal}) reel={(authored.ReelChannel ?? "none")}(ordinal={compiled.ReelChannelOrdinal})]"
        );
    }
    private string DescribeGravity() {
        var authored = server.Definition.Gravity;
        var compiled = population.CompiledGravity;
        var statistics = population.GravityStatistics;
        var uniform = authored.UniformAcceleration;
        var sources = new List<string>();

        foreach (var attractor in authored.Attractors) {
            sources.Add(item: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"mass:{attractor.PlacementId}={attractor.Mass:0.#####}"
            ));
        }

        // Installed definitions passed the thick validator, so every explicit attractor resolves and occupies the
        // authored prefix of FixedWorldGravity.Attractors. Point presets follow that prefix in authored order.
        var compiledIndex = authored.Attractors.Count;

        foreach (var point in (authored.Points ?? [])) {
            var derivedMass = ((compiledIndex < compiled.Attractors.Length)
                ? DescribeFixed(compiled.Attractors[compiledIndex].Mass)
                : "uncompiled"
            );

            sources.Add(item: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"point:{point.PlacementId}=g{point.SurfaceGravity:0.#####}@r{point.ReferenceRadius:0.#####}->mass{derivedMass}"
            ));
            compiledIndex++;
        }

        var sourceRows = ((sources.Count == 0)
            ? "none"
            : string.Join(
                separator: ",",
                values: sources
            )
        );
        var targetCount = Math.Max(
            val1: 0,
            val2: (statistics.BodyCount - compiled.Attractors.Length)
        );

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.gravity: solver {authored.Solver} uniform=({uniform.X:0.#####},{uniform.Y:0.#####},{uniform.Z:0.#####}) G={authored.GravitationalConstant:0.#####} softening={authored.SofteningLength:0.#####} | sources {sourceRows} | compiled={compiled.Attractors.Length} static source(s) last targets={targetCount} nodes={statistics.TreeNodeCount} exact={statistics.ExactSourceEvaluations} approximate={statistics.ApproximatedNodeEvaluations} represented={statistics.ApproximatedSourceCount} m2m={statistics.MultipoleToMultipoleTranslations} m2l={statistics.MultipoleToLocalTranslations} l2l={statistics.LocalToLocalTranslations} local={statistics.LocalExpansionEvaluations} deferred={statistics.DeferredLocalExpansionEvaluations}]"
        );
    }
    private static string DescribeAssignment(WorldRowAssignment assignment) =>
        $"{DescribeSequence(sequence: assignment.Sequence)}[{((assignment.Rows.Count == 0)
            ? "all"
            : string.Join(
                separator: ",",
                values: assignment.Rows
            ))}]";
    private static string DescribeDistribution(WorldDistribution distribution) {
        var region = distribution.Region switch {
            WorldDistributionRegion.Disc disc => $"disc(radius={disc.Radius:0.###},samples={(disc.SampleCount?.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) ?? "requested")})",
            WorldDistributionRegion.Points points => $"points(names={string.Join(
            separator: ",",
            values: points.Names
        )},halfExtent={points.HalfExtent:0.###})",
            WorldDistributionRegion.Lattice lattice => $"lattice({lattice.CountA}x{lattice.CountB})",
            _ => "unknown",
        };

        return $"{region}+{DescribeSequence(sequence: distribution.Fill)}";
    }
    // The world.parked readout: every entity index currently PARKED (see WorldPopulation.Entry.Parked), its
    // remaining grace and absolute deadline tick, and — when the retained body carries one — its profile name, so a
    // script can tell WHO a parked seat is waiting for without inferring it from body.where's silence. A body
    // parked with NO deadline (a positive reconnect grace compiled at simulation rate 0 — see
    // CompiledTickDuration.IsNever) reads null from WorldPopulation.ParkedRemainingTicks and renders "never" for
    // both fields — a concrete expiry that will never arrive would be worse than saying nothing. The same null is
    // POSITIVE INFINITY on the $parked: rule channel (Server.WorldServer.ReadWorldFact's Parked arm), so the
    // console and the rules substrate say the same thing in their own vocabularies.
    private string DescribeParked() {
        var tick = server.NextInputTick;
        var rows = new List<string>();

        for (var index = 0; (index < population.Capacity); index++) {
            if (!population.IsParked(index: index)) {
                continue;
            }

            var window = ((population.ParkedRemainingTicks(
                index: index,
                tick: tick
            ) is { } remaining)
                ? $"remaining={remaining} deadline={(tick + ((ulong)remaining))}"
                : "remaining=never deadline=never"
            );
            var body = population.EntryBody(index: index);
            var profile = body?.Profile?.Name;
            var pose = (body?.DescribePose() ?? "pos=(?, ?) yaw=?°");

            rows.Add(item: ((profile is null)
                ? $"body:{index} {window} {pose}"
                : $"body:{index} {window} profile={profile} {pose}"));
        }

        return $"[world.parked: {string.Join(
            separator: " | ",
            values: rows
        )}]";
    }
    // The world.players readout: the roster's four slots plus the population line spliced in as a trailing segment.
    // roster.Describe() ends with ']', so drop it (the [..^1] slice) and re-close after the population segment.
    private string DescribePlayers() {
        var players = roster.Describe();
        var local = roster.Count;
        var simulated = population.SimulatedCount;

        return $"{players[..^1]} | population: {local} local + {simulated} network = {(local + simulated)}/{population.Capacity}]";
    }
    // The world.population readout: the active simulated count, the between-tapes behavior, and the total avatar load on
    // the renderer. LOOPBACK-ONLY: the population reads here are in-process; a socket transport replaces them with a
    // link query the server composes.
    private string DescribePopulation() {
        var local = roster.Count;
        var simulated = population.SimulatedCount;
        var behavior = (population.DefaultPeerSource.IsIdle
            ? "idle"
            : ((population.DefaultPeerSource.ProducerName is { } producer)
                ? $"producer:{producer}"
                : "live"
        ));
        var workload = WorldRigCatalog.ActiveWorkload(
            isActive: population.IsActive,
            capacity: population.Capacity
        );
        // The per-kit census derives its names and counts from the definition rows, in row order.
        var counts = population.ActiveKitCounts();
        var kits = string.Join(
            separator: " ",
            values: server.Definition.Kits.Select(selector: (kit, row) => $"{kit.Name}={counts[row]}")
        );
        var defaults = server.Definition.Population;
        var kitAssignment = DescribeAssignment(assignment: server.Definition.Assignment);
        var lookAssignment = DescribeAssignment(assignment: server.Definition.LookAssignment);

        return $"[world.population: {simulated} network-human stand-ins active (0..{population.PeerCapacity}), behavior {behavior} | distribution {DescribeDistribution(distribution: defaults.Distribution)} | peerVariation {DescribeVariation(variation: defaults.PeerVariation)} seatVariation {DescribeVariation(variation: defaults.SeatVariation)} peerColors {DescribeSequence(sequence: defaults.PeerColors)} | assignments kit={kitAssignment} look={lookAssignment} | {local} local + {simulated} = {(local + simulated)}/{population.Capacity} inhabitants | archetypes {kits} | unique deterministic rigs {WorldRigCatalog.MinInstructionCount}..{WorldRigCatalog.MaxInstructionCount} instructions/avatar; active {workload.Leaves} leaf instances, {workload.Instructions} authored VM instructions]";
    }
    private static string DescribeSequence(WorldSequence sequence) =>
        $"{sequence.Name}(offset={sequence.Offset},step={sequence.Step:0.########})";
    private static string DescribeVariation(WorldPopulationVariation variation) =>
        $"phase={DescribeSequence(sequence: variation.Phase)},weave={DescribeSequence(sequence: variation.Weave)},activity={DescribeSequence(sequence: variation.Activity)}";

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.attach-policy",
            description: "Reads the authored/compiled climb-grapple attachment section back (Immediate): the world default grip policy, every reach/speed/cost/rope/reel/release tuning field, and each attach/detach/reel channel name with its resolved ordinal. 'attachment disabled' when the world authors no attachment section (today's behavior, unchanged). body.attachment answers the per-body question this does not — mode, anchor, rope length.",
            handler: (_, args) => ((args.Count == 0)
                ? new CommandResult(Output: DescribeAttachmentPolicy())
                : CommandResult.Error(output: $"[world.attach-policy: unrecognized '{args[0]}' — expected no arguments]")),
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.gravity",
            description: "Reads the authored and compiled gravity field back (Immediate): solver, uniform acceleration, shared G/softening, explicit mass sources, point/planet surface-gravity presets with their derived masses, and the last deterministic solve's work counters.",
            handler: (_, args) => ((args.Count == 0)
                ? new CommandResult(Output: DescribeGravity())
                : CommandResult.Error(output: $"[world.gravity: unrecognized '{args[0]}' — expected no arguments]")),
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.fields",
            description: "Reads the field lattice back (Immediate): its shape and cadence, and each field's nonzero cell count and mean.",
            handler: (context, args) => new CommandResult(Output: server.DescribeFields()),
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.population",
            description: "Sets the simulated peer count and its between-tape source: world.population [count] [idle|producer:<name>] (tokens are order-independent; no argument reads both).",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: DescribePopulation());
                }

                int? count = null;
                IntentSource? behavior = null;

                // Order-independent tokens: each is either a bare integer count or an intent-source token. A repeat
                // of either lane, or an unrecognized token, is rejected whole so a typo never half-applies. WireArgs has
                // no enumerator (a ref struct can't back foreach's pattern here without one) — walk it by index instead.
                for (var index = 0; (index < args.Count); index++) {
                    if (args.Is(
                        index: index,
                        value: "idle"
                    )) {
                        if (behavior is not null) {
                            return CommandResult.Error(output: $"[world.population: source given twice — idle|producer:<name>]");
                        }

                        behavior = IntentSource.Idle;

                        continue;
                    }

                    var token = args[index].ToString();

                    if (
                        token.StartsWith(
                        comparisonType: StringComparison.Ordinal,
                        value: "producer:"
                    ) &&
                        (token.Length > "producer:".Length)
                    ) {
                        if (behavior is not null) {
                            return CommandResult.Error(output: $"[world.population: source given twice — idle|producer:<name>]");
                        }

                        behavior = IntentSource.Producer(name: token["producer:".Length..]);

                        continue;
                    }

                    if (
                        !args.TryInt(
                        index: index,
                        value: out var parsed
                    ) ||
                        (parsed < 0) ||
                        (parsed > population.PeerCapacity)
                    ) {
                        return CommandResult.Error(output: $"[world.population: unknown token '{args[index]}' — a count 0..{population.PeerCapacity} and/or idle|producer:<name>]");
                    }

                    if (count is not null) {
                        return CommandResult.Error(output: $"[world.population: count given twice — one integer 0..{population.PeerCapacity}]");
                    }

                    count = parsed;
                }

                // The census and peer source are session requests to the authoritative server; each completion fires
                // INLINE over loopback, so the echo below (built AFTER both Submit calls return) still reads the
                // applied state — it is just assembled from the completion payloads rather than a live read taken
                // after a discarded synchronous return. An explicit idle/producer token sets the peer-source DEFAULT and
                // sweeps ALL peers (4..127) to it — last-writer-wins, so a per-entity body.control does not survive
                // the global flip; a count alone leaves existing peers' sources be. A census beyond the live ceiling is
                // CLAMPED, not refused — the ceiling is the tighter of the authored networkPlayers admission cap and
                // the inhabitant floor, and shrinking to fit is the right behavior. The echo leads with
                // requested-vs-granted whenever the two differ, and a DENIED request is a THIRD, distinct
                // outcome from "granted the full count" and "clamped to a lower one".
                var actingPrincipal = context.ActingPrincipal();
                string? notice = null;

                if (count is { } resolvedCount) {
                    link.SubmitSession(
                        request: new SessionRequest.SetPopulation(
                            Count: resolvedCount,
                            Principal: actingPrincipal
                        ),
                        completion: reply => {
                            if (!reply.Accepted) {
                                notice += $"[world.population: {actingPrincipal.Describe()} cannot set the census ({reply.Reason}) — see world.why]\n";
                            } else if (reply.AssignedIndex != resolvedCount) {
                                notice += $"[world.population: requested {resolvedCount}, GRANTED {reply.AssignedIndex} — clamped to the live ceiling ({population.SimulatedCeiling}: the networkPlayers admission cap under {population.MaxSimulated} free peer slots)]\n";
                            }
                        }
                    );
                }

                if (behavior is { } resolvedBehavior) {
                    link.SubmitSession(
                        request: new SessionRequest.SetPeerSource(
                            Principal: actingPrincipal,
                            Source: resolvedBehavior
                        ),
                        completion: peerReply => {
                            if (!peerReply.Accepted) {
                                notice += $"[world.population: {actingPrincipal.Describe()} cannot set the peer source ({peerReply.Reason}) — see world.why]\n";
                            }
                        }
                    );
                }

                return new CommandResult(Output: (notice + DescribePopulation()));
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "world.players",
            description: "Lists the roster's four slots — joined/empty, each joined slot's profile, state (active/PENDING), owned devices (or origin), and pose (p<N> name state(devices) pos=(x, z) yaw=d°) — plus the population line (local seats + simulated stand-ins). Every player is a networked player; a local pad or the keyboard is just one at zero latency.",
            valueKind: CommandValueKind.Digital,
            handler: _ => new CommandResult(Output: DescribePlayers())
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.input-holds",
            description: "Reports every active participant's authored, measured, and applied input hold plus the participant setting the equalized maximum.",
            handler: (_, args) => {
                if (args.Count != 0) {
                    return CommandResult.Error(output: "[world.input-holds: expected no values]");
                }

                var result = default(CommandResult);

                link.Query(
                    query: new WorldQuery.InputHolds(),
                    completion: answer => {
                        result = new CommandResult(Output: answer.Text) {
                            IsError = answer.Refused,
                        };
                    }
                );

                return result;
            }
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "world.devices",
            description: "Lists every input device seen this session by its stable token (keyboard1, keyboard2, …, mouse1, mouse2, …, gamepad1, gamepad2, …, camera1, camera2, …) in first-seen order, its name when known, and the player it currently drives (p<N> or unassigned; a slot-sharing device's marker * names the seat's resolved device of its own kind). The reassignment verbs — player.assign / player.cycle / player.claim — move a device between players.",
            valueKind: CommandValueKind.Digital,
            handler: _ => new CommandResult(Output: roster.DescribeDevices())
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "world.device-profiles",
            description: "Lists the preferred-profile decision recorded when each connected input device was first seen, including why a preference did not apply.",
            valueKind: CommandValueKind.Digital,
            handler: _ => new CommandResult(Output: roster.DescribeDeviceProfiles())
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Unbindable,
            name: "world.parked",
            description: "Reports every PARKED body — a disconnected seat or peer still retained in the sim/collider set (pose, durable state, occupancy) under population.reconnectGraceTicks' grace window: body:<n> remaining=<ticks> deadline=<tick> [profile=<name>] pos=(x, z) yaw=d°. Empty when nothing is parked. A parked body is the SAME thing the '$parked:<bodyRef>' reserved rule channel reads live; this is its read-back.",
            valueKind: CommandValueKind.Digital,
            handler: _ => new CommandResult(Output: DescribeParked())
        );
    }
}
