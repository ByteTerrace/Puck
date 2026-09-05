using System.Globalization;
using System.Text.Json;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The world's participant/census verb surface — SERVER-SAFE (registered in <c>AddWorldAuthoritativeCore</c>, headless
/// or windowed alike): <c>world.players</c>, <c>world.devices</c>, <c>world.device-profiles</c>,
/// <c>world.population</c>, <c>world.navigation</c>, <c>world.flock</c>, <c>world.decisions</c>, <c>world.social</c>, and <c>world.budget</c>. Split out of
/// <see cref="WorldCommandModule"/> (which stays presentation-only — graphics levers, GPU timing, the diegetic-row
/// listings), because these verbs read pure roster/population/document state and never require a GPU, window, or audio
/// device. <c>world.budget</c> accepts an optional render probe: windowed composition fills its render figures,
/// while headless composition still reports every authoritative cost and names the absent renderer.
/// </summary>
internal sealed class WorldPopulationCommandModule(PlayerRoster roster, WorldPopulation population, WorldServer server, IServerLink link, WorldRenderProbe? renderProbe = null) : ICommandModule {
    private static string DescribeFixed(FixedQ4816 value) => ((double)value).ToString(
        format: "0.#####",
        provider: CultureInfo.InvariantCulture
    );
    private string DescribeGravity() {
        var authored = server.Definition.Gravity;
        var compiled = population.CompiledGravity;
        var statistics = population.GravityStatistics;
        var areaStatistics = population.GravityAreaStatistics;
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
                ? DescribeFixed(value: compiled.Attractors[compiledIndex].Mass)
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
        var areaRows = new List<string>();

        for (var compiledOrder = 0; (compiledOrder < compiled.Areas.Length); compiledOrder++) {
            var area = compiled.Areas[compiledOrder];
            var authoredArea = authored.Areas![area.AuthoredIndex];
            var bounds = authoredArea.Bounds switch {
                WorldGravityAreaBounds.SphereBounds sphere => $"sphere(r={sphere.Radius:0.#####})",
                WorldGravityAreaBounds.BoxBounds box => $"box(half=({box.HalfExtents.X:0.#####},{box.HalfExtents.Y:0.#####},{box.HalfExtents.Z:0.#####}))",
                _ => "unknown",
            };
            var acceleration = authoredArea.Acceleration switch {
                WorldGravityAreaAcceleration.Directional directional => $"directional({directional.Value.X:0.#####},{directional.Value.Y:0.#####},{directional.Value.Z:0.#####})",
                WorldGravityAreaAcceleration.Radial radial => $"radial({radial.Magnitude:0.#####})",
                _ => "unknown",
            };
            var ride = ((area.Attach is { } attach)
                ? $"body:{attach.BodyIndex}"
                : "static"
            );

            areaRows.Add(item: $"#{area.AuthoredIndex}:{area.PlacementId}/priority={area.Priority}/mode={area.Mode}/{bounds}/{acceleration}/ride={ride}/order={compiledOrder}");
        }
        var areaDescription = ((areaRows.Count == 0)
            ? "none"
            : string.Join(separator: ",", values: areaRows)
        );

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.gravity: solver {authored.Solver} uniform=({uniform.X:0.#####},{uniform.Y:0.#####},{uniform.Z:0.#####}) G={authored.GravitationalConstant:0.#####} softening={authored.SofteningLength:0.#####} | sources {sourceRows} | areas {areaDescription} | compiled={compiled.Attractors.Length} static source(s), {compiled.Areas.Length} area(s) last globalTargets={targetCount} nodes={statistics.TreeNodeCount} exact={statistics.ExactSourceEvaluations} approximate={statistics.ApproximatedNodeEvaluations} represented={statistics.ApproximatedSourceCount} m2m={statistics.MultipoleToMultipoleTranslations} m2l={statistics.MultipoleToLocalTranslations} l2l={statistics.LocalToLocalTranslations} local={statistics.LocalExpansionEvaluations} deferred={statistics.DeferredLocalExpansionEvaluations} areaTargets={areaStatistics.TargetCount} areaActive={areaStatistics.ActiveAreaCount} areaEvaluations={areaStatistics.EvaluationCount} areaMatches={areaStatistics.MatchCount}]"
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
        var looks = server.Definition.Looks;
        var workload = WorldRigCatalog.ActiveWorkload(
            isActive: population.IsActive,
            capacity: population.Capacity,
            rigFor: index => WorldRigCatalog.RigFor(WorldDefinitionRows.ResolveLook(looks, population.LookIndex(index)), population.CatalogRig(index))
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

        return $"[world.population: {simulated} network-human stand-ins active (0..{population.PeerCapacity}), behavior {behavior} | distribution {DescribeDistribution(distribution: defaults.Distribution)} | peerVariation {DescribeVariation(variation: defaults.PeerVariation)} seatVariation {DescribeVariation(variation: defaults.SeatVariation)} peerColors {DescribeSequence(sequence: defaults.PeerColors)} | assignments kit={kitAssignment} look={lookAssignment} | {local} local + {simulated} = {(local + simulated)}/{population.Capacity} inhabitants | archetypes {kits} | {WorldRigCatalog.RigCount} catalog looks, {WorldRigCatalog.MinInstructionCount}..{WorldRigCatalog.MaxInstructionCount} instructions/avatar; catalog workload {workload.Leaves} leaves in {workload.Instances} leaf cull instances, {workload.Instructions} authored VM instructions (creation stamps accounted separately)]";
    }
    private string DescribeBudget() {
        var render = ((renderProbe?.Node is { } node)
            ? $"program {node.LiveProgramWords}/{node.ProgramWordCapacity} word(s), {node.LiveProgramInstances} instance(s), stepScale {node.LiveProgramStepScale.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)}{((node.LiveProgramStepScale is > 0f and < 1f) ? $" (march ~{(1f / node.LiveProgramStepScale).ToString(format: "0.#", provider: CultureInfo.InvariantCulture)}x baseline)" : string.Empty)}{((node.LiveProgramStepScaleBinder is { } binder) ? $" bound by instance {binder.InstanceIndex} ({binder.Shape} x{binder.Factor.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)} at instruction {binder.InstructionIndex}, unscoped)" : string.Empty)}"
            : "renderer not built yet"
        );
        var farDistance = WorldRenderFarDistance.Resolve(defaults: server.Definition.Render);
        var fogDensity = (server.Definition.Render.Sky?.FogDensity ?? Puck.SdfVm.SdfFrame.DefaultSkyFogDensity);
        var far = string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"far {farDistance:0.##} unit(s) (reach x{(farDistance / Puck.SdfVm.SdfFrame.DefaultFarDistance):0.##} the {Puck.SdfVm.SdfFrame.DefaultFarDistance:0}-unit default; horizon ray ~{farDistance:0} step(s) per unit of camera height of {Puck.SdfVm.SdfWorldEngine.PrimaryMarchSteps}; fog remnant at the far plane {MathF.Exp(x: (-fogDensity * farDistance)):0.###})"
        );
        var lattice = ((population.Fields is { } fields)
            ? fields.DescribeCost(activeBodyCount: population.ActiveCount(), bodyCapacity: population.Capacity)
            : "lattice none"
        );
        var gravityCompiled = population.CompiledGravity;
        var gravityStatistics = population.GravityStatistics;
        var gravityAreaStatistics = population.GravityAreaStatistics;
        var gravity = $"gravity {gravityCompiled.Attractors.Length} static source(s), {gravityCompiled.Areas.Length} local area(s) / target declared, last global targets {Math.Max(val1: 0, val2: (gravityStatistics.BodyCount - gravityCompiled.Attractors.Length))}, exact {gravityStatistics.ExactSourceEvaluations}, approximate {gravityStatistics.ApproximatedNodeEvaluations}, m2l {gravityStatistics.MultipoleToLocalTranslations}, area targets {gravityAreaStatistics.TargetCount}, active {gravityAreaStatistics.ActiveAreaCount}, evaluations {gravityAreaStatistics.EvaluationCount}, matches {gravityAreaStatistics.MatchCount}";
        var placementInstances = WorldPlacementStamper.StaticStampInstances(
            creations: server.Definition.Creations,
            placements: server.Definition.Placements,
            worldSeed: (server.Definition.Generation?.WorldSeed ?? 0UL)
        );
        var placements = $"placements {placementInstances} static instance(s) ({server.Definition.Placements.Count} row(s))";
        var curves = $"curves {population.CountCurveFollowers()} follower(s)";
        var navigationWork = population.NavigationWork();
        var navigation = $"navigation {population.NavigationCellCount} compiled cell(s), {population.NavigationWorkspaceBytes} workspace byte(s), declared search {population.NavigationDeclaredSearchWork} expansion(s), live {navigationWork.Followers} follower(s) / last {navigationWork.LastExpanded} expansion(s) / simultaneous-replan ceiling {navigationWork.WorstExpanded} expansion(s)";
        var ruleBudget = WorldRuleWorkBudget.Measure(definition: server.Definition);
        var rules = $"rules {ruleBudget.RuleRows}/{WorldRuleCapacity.MaxRules}, interactions {ruleBudget.InteractionRows}/{WorldInteractionCapacity.MaxInteractions}, worst {ruleBudget.EvaluationSlots} evaluation(s), {ruleBudget.WorkUnitsPerTick}/{WorldRuleCapacity.MaxWorkUnitsPerTick} work unit(s) / tick (including {ruleBudget.FlockAffinityWorkUnitsPerTick} flock-affinity units); decision perception {ruleBudget.DecisionImagePointsPerTick} pose(s), {ruleBudget.DecisionGridBuildsPerTick} shared grid rebuild(s)/{ruleBudget.DecisionGridPointsPerTick} point(s) sorted per tick ceiling";

        return $"[world.budget: {render} | {far} | {lattice} | {gravity} | {placements} | state {(server.Definition.State?.Count ?? 0)} row(s) | {rules} | {curves} | {navigation} | {population.DescribeFlockWork()} | {population.DescribeRigidWork()} | {server.DescribeSocialBudget()} | {server.DescribePatternBudget()}]";
    }
    private static string DescribeSequence(WorldSequence sequence) =>
        $"{sequence.Name}(offset={sequence.Offset},step={sequence.Step:0.########})";
    private static string DescribeVariation(WorldPopulationVariation variation) =>
        $"phase={DescribeSequence(sequence: variation.Phase)},weave={DescribeSequence(sequence: variation.Weave)},activity={DescribeSequence(sequence: variation.Activity)}";

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.social",
            description: "Operator inspection of social policy, bounded work, and last evidence outcome, or one belief: world.social [<query-json>]. Requires Observe/all.",
            handler: (context, args) => {
                try {
                    var query = args.Count == 0 ? null : JsonSerializer.Deserialize(
                        WorldCommandArguments.RawAfter(context, in args, 1), WorldJsonContext.Default.WorldSocialQuery)
                        ?? throw new JsonException("query must be an object");
                    return new CommandResult(Output: server.DescribeSocial(context.ActingPrincipal(), query));
                } catch (JsonException exception) { return new CommandResult(Output: $"[world.social: invalid query: {exception.Message}]"); }
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.decisions",
            description: "Echoes authored choice policies and active bindings: selected option, last score, commitment, reconsideration cadence, and local random draw count.",
            handler: (_, args) => ((CommandResult.RequireNoArguments(args: args, verb: "world.decisions") is { } refusal)
                ? refusal : new CommandResult(Output: server.DescribeDecisions())),
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.flock",
            description: "Echoes each kit's authored local flock steering: space, perception range/cone/cadence, candidate and neighbor budgets, sight requirement, steering weights, and last-step work. Available headless; does not change behavior.",
            handler: (_, args) => ((CommandResult.RequireNoArguments(args: args, verb: "world.flock") is { } refusal)
                ? refusal
                : new CommandResult(Output: population.DescribeFlocks())),
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.navigation",
            description: "Echoes every authored deterministic navigation domain: surface, free-volume, or medium-constrained; compiled dimensions and clear cells; volume connectivity; medium binding; and hard route search/path limits.",
            handler: (_, args) => ((CommandResult.RequireNoArguments(args: args, verb: "world.navigation") is { } refusal)
                ? refusal
                : new CommandResult(Output: population.DescribeNavigation())),
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.budget",
            description: "Prints the immediate compose-time cost sheet: rendering, far-distance, fields, gravity, placements, state/rules, curves, bounded navigation, and local flock perception work. Rendering reads 'not built yet' under a headless host; authoritative costs remain available.",
            handler: (_, args) => ((CommandResult.RequireNoArguments(args: args, verb: "world.budget") is { } refusal)
                ? refusal
                : new CommandResult(Output: DescribeBudget())),
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.gravity",
            description: "Reads the authored and compiled gravity field back (Immediate): solver, uniform acceleration, shared G/softening, explicit mass sources, point/planet surface-gravity presets with their derived masses, and the last deterministic solve's work counters.",
            handler: (_, args) => ((CommandResult.RequireNoArguments(args: args, verb: "world.gravity") is { } refusal)
                ? refusal
                : new CommandResult(Output: DescribeGravity())),
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
            name: "world.responses",
            description: "Reads every placement carrying a response trait back (Immediate): its current prototype and which authored when-condition (if any) currently holds at its coupled lattice cell.",
            handler: (context, args) => ((CommandResult.RequireNoArguments(args: args, verb: "world.responses") is { } refusal)
                ? refusal
                : new CommandResult(Output: server.DescribeResponses())),
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
                // sweeps ALL peers (4..4095) to it — last-writer-wins, so a per-entity body.control does not survive
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
