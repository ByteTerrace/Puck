using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: the closed step vocabulary a <c>schedule</c> row may open with
/// (<see cref="WorldScheduleCommands"/>) is tied to the LIVE command registry, read out of a running host's own
/// affordance manifest. Every admitted step exists there and routes to the simulation, every admitted read exists there
/// and runs immediately, and every simulation-routed verb there is either admitted or listed below with the reason it
/// is not a test step. A new simulation verb therefore fails this class until someone decides which side it belongs
/// on.</summary>
/// <remarks>The manifest comes from booting the real executable, unarmed, and asking it — a reconstructed registry
/// would answer about the modules the test remembered to compose rather than the ones the host does.</remarks>
public sealed class ScheduledStepVocabularyLawTests {
    // Every simulation-routed verb the step vocabulary deliberately leaves out, with why. A verb reaching the
    // simulation is not automatically a test step: it may widen the acting principal's own reach, reach the process
    // or the filesystem, drive a seat's presentation rather than world state, or simply not have been asked for.
    private static readonly (string Reason, string[] Verbs)[] Excluded = [
        ("changes who may act, or writes durable identity, ownership or group state a test world does not own", [
            "identity.bindings.save",
            "identity.deliver",
            "identity.fact.set",
            "identity.hud",
            "identity.motion",
            "player.assign",
            "player.claim",
            "player.confirm",
            "world.assign",
            "world.grant",
            "world.grant.remove",
            "world.grant.set",
            "world.group.form",
            "world.group.join",
            "world.group.kick",
            "world.group.leave",
            "world.identify",
            "world.ownership.accept",
            "world.ownership.offer",
            "world.ownership.reclaim",
            "world.revoke",
            "world.transfer",
        ]),
        ("reaches the process, the clock, the filesystem or the running document rather than the simulation", [
            "creation.sculpt",
            "pipeline.commit",
            "pipeline.load",
            "world.generate",
            "world.instance.start",
            "world.instance.stop",
            "world.load",
            "world.rate",
            "world.reflow.commit",
            "world.reload",
            "world.reset",
            "world.undo",
        ]),
        ("drives a seat's own presentation — camera, look, bindings, synthesized input — which is not world state a verdict reads", [
            "channel.ordinal.0",
            "channel.ordinal.1",
            "channel.ordinal.10",
            "channel.ordinal.11",
            "channel.ordinal.12",
            "channel.ordinal.13",
            "channel.ordinal.14",
            "channel.ordinal.15",
            "channel.ordinal.2",
            "channel.ordinal.3",
            "channel.ordinal.4",
            "channel.ordinal.5",
            "channel.ordinal.6",
            "channel.ordinal.7",
            "channel.ordinal.8",
            "channel.ordinal.9",
            "player.bind",
            "player.camera",
            "player.cycle",
            "player.look",
            "player.look.free",
            "player.look.recenter",
            "player.look.steer",
            "player.mode",
            "player.motion.angular",
            "player.motion.controls",
            "player.move",
            "player.move.strafe",
            "player.orbit",
            "player.signal",
            "player.steer",
            "source.pointer.direction",
            "source.pointer.origin",
            "view.override",
        ]),
        ("a chat channel, whose effect is a message rather than state", [
            "chat.allow",
            "chat.block",
            "chat.inbox",
            "chat.log",
            "chat.whisper",
        ]),
        ("a screen machine's cartridge or camera, a machine-host operation rather than a simulation step", [
            "screen.camera",
            "screen.eject",
            "screen.insert",
            "screen.source",
        ]),
        ("undecided: it reaches the simulation and could be admitted, but no test world has asked for it", [
            "body.attach",
            "body.designate",
            "body.detach",
            "body.hold",
            "body.reconcile",
            "body.reel",
            "body.targets",
            "world.kit.default",
            "world.population",
            "world.population.defaults",
            "world.population.spawn",
        ]),
    ];
    private static readonly Lazy<IReadOnlyDictionary<string, (string Routing, string Audience)>> LiveRegistry = new(valueFactory: Read);

    // The affordance manifest's own rendering: "[world.affordances: [ … ],"channels":[ … ], … ]". Only the command
    // array is wanted, and it ends where the channel table begins.
    private static IReadOnlyDictionary<string, (string Routing, string Audience)> Read() {
        using var legRoot = ScheduledWorldBoot.Leg();
        var leg = legRoot.PathOf(name: "affordances");
        var run = ScheduledWorldBoot.Boot(
            legDirectory: leg,
            script: string.Join(
                separator: Environment.NewLine,
                "world.affordances",
                "quit",
                string.Empty
            ),
            world: "refused-command.world.json"
        );

        var opened = run.Stdout.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: "[world.affordances:"
        );

        Assert.True(
            condition: (opened >= 0),
            userMessage: $"the boot printed no affordance manifest: {run.Stdout}"
        );

        var start = run.Stdout.IndexOf(
            startIndex: opened,
            value: '['
        );

        start = run.Stdout.IndexOf(
            startIndex: (start + 1),
            value: '['
        );

        var end = run.Stdout.IndexOf(
            comparisonType: StringComparison.Ordinal,
            startIndex: start,
            value: "],\"channels\":"
        );

        Assert.True(
            condition: (end > start),
            userMessage: $"the affordance manifest did not carry a command array: {run.Stdout}"
        );

        var commands = ((JsonNode.Parse(json: run.Stdout[start..(end + 1)]) as JsonArray) ?? throw new JsonException(message: "the affordance manifest's command member is not an array"));
        var registry = new Dictionary<string, (string Routing, string Audience)>(comparer: StringComparer.Ordinal);

        foreach (var command in commands) {
            if (
                (command is JsonObject entry) &&
                (entry[propertyName: "name"]?.GetValue<string>() is { } name)
            ) {
                registry[key: name] = (
                    (entry[propertyName: "routing"]?.GetValue<string>() ?? string.Empty),
                    (entry[propertyName: "audience"]?.GetValue<string>() ?? string.Empty)
                );
            }
        }

        Assert.NotEmpty(collection: registry);

        return registry;
    }

    // The registry refuses an operator verb for every principal but the console before its handler runs
    // (Puck.Commands' CommandAudienceLawTests), so what a host must get right is which verbs carry the audience. The
    // evaluation diagnostics print values the rules computed, some of them from state a seat is not shown; every one
    // of them, in whichever module the host composes it, answers the operator alone, and in the headless host this
    // class boots nothing else does. A GPU shape adds the verb that arms its device's creation faults and nothing
    // more (WorldBootCompositionLawTests). A read step a schedule admits is never an operator verb.
    [Fact]
    public void TheHeadlessHostsOperatorVerbsAreExactlyTheEvaluationDiagnostics() {
        var registry = LiveRegistry.Value;

        Assert.All(
            collection: registry.Values,
            action: static entry => Assert.Contains(
                collection: ((string[])["anyone", "operator"]),
                expected: entry.Audience
            )
        );
        Assert.Equal(
            actual: registry.Where(predicate: static entry => string.Equals(
                a: entry.Value.Audience,
                b: "operator",
                comparisonType: StringComparison.Ordinal
            )).Select(selector: static entry => entry.Key).Order(comparer: StringComparer.Ordinal),
            expected: ["world.decisions", "world.responses", "world.rule.failures", "world.rule.trace", "world.rules", "world.search", "world.verdicts"]
        );
        Assert.DoesNotContain(
            collection: WorldScheduleCommands.Admitted,
            filter: verb => string.Equals(
                a: registry[key: verb].Audience,
                b: "operator",
                comparisonType: StringComparison.Ordinal
            )
        );
    }
    [Fact]
    public void EveryAdmittedReadIsRegisteredAndRunsImmediately() {
        var registry = LiveRegistry.Value;

        Assert.NotEmpty(collection: WorldScheduleCommands.Reads);
        foreach (var verb in WorldScheduleCommands.Reads) {
            Assert.True(
                condition: registry.TryGetValue(
                    key: verb,
                    value: out var routing
                ),
                userMessage: $"the schedule admits the read '{verb}' and the host registers no such verb"
            );
            Assert.Equal(
                actual: routing.Routing,
                comparer: StringComparer.Ordinal,
                expected: "immediate"
            );
        }
    }
    [Fact]
    public void EveryAdmittedStepVerbIsRegisteredAndRoutesToTheSimulation() {
        var registry = LiveRegistry.Value;

        foreach (var verb in WorldScheduleCommands.Steps) {
            Assert.True(
                condition: registry.TryGetValue(
                    key: verb,
                    value: out var routing
                ),
                userMessage: $"the schedule admits '{verb}' and the host registers no such verb — a row naming it becomes a recorded refusal at its tick"
            );
            Assert.Equal(
                actual: routing.Routing,
                comparer: StringComparer.Ordinal,
                expected: "simulation"
            );
        }
    }
    [Fact]
    public void EverySimulationRoutedVerbIsEitherAdmittedOrExcludedWithAReason() {
        var registry = LiveRegistry.Value;
        var decided = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var (reason, verbs) in Excluded) {
            foreach (var verb in verbs) {
                Assert.True(
                    condition: decided.TryAdd(
                        key: verb,
                        value: reason
                    ),
                    userMessage: $"'{verb}' is excluded twice"
                );
                Assert.False(
                    condition: WorldScheduleCommands.IsAdmitted(verb: verb),
                    userMessage: $"'{verb}' is both admitted and excluded"
                );
                Assert.True(
                    condition: registry.ContainsKey(key: verb),
                    userMessage: $"'{verb}' is excluded and the host registers no such verb — delete the entry"
                );
                Assert.NotEmpty(collection: reason);
            }
        }

        var undecided = registry
            .Where(predicate: entry => (string.Equals(
            a: entry.Value.Routing,
            b: "simulation",
            comparisonType: StringComparison.Ordinal
        ) && !WorldScheduleCommands.IsAdmitted(verb: entry.Key) && !decided.ContainsKey(key: entry.Key)))
            .Select(selector: static entry => entry.Key)
            .Order(comparer: StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(collection: undecided);
    }
}
