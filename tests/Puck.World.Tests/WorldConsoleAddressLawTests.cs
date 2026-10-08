using System.Numerics;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: every console verb that submits writes to the instance the console addresses, through that instance's own
/// link (<see cref="WorldInstance.SubmissionLink"/>), never through the boot instance's. With the console addressing a
/// non-boot instance, <c>world.row.set</c> applies there and the boot instance's document never moves, and a
/// representative verb of each submitting module (grant, group, look, machine, state) reaches that instance's link and
/// never boot's.
/// </summary>
public sealed class WorldConsoleAddressLawTests {
    private sealed class AddressedAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public WorldInstance Instance { get; set; } = instance;

        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = Instance;
            refusal = string.Empty;

            return true;
        }
    }
    // A link that records the payload of every envelope it forwards to an instance's own link.
    private sealed class CountingLink(IServerLink target) : IServerLink {
        public List<WorldSubmissionPayload> Submitted { get; } = [];

        public void Query(WorldQuery query, Action<QueryAnswer> completion) => target.Query(completion: completion, query: query);
        public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal) => SubmitEnvelope(
            completion: null,
            operationId: Guid.Empty,
            payload: payload,
            principal: principal
        );
        public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal, Guid operationId, Action<WorldSubmissionResult>? completion) {
            Submitted.Add(item: payload);

            return target.SubmitEnvelope(completion: completion, operationId: operationId, payload: payload, principal: principal);
        }
        public void SubmitIntent(in IntentSubmission submission) => target.SubmitIntent(submission: in submission);
        public void SubmitSession(SessionRequest request, Action<SessionReply> completion) => target.SubmitSession(completion: completion, request: request);
    }

    // The instance a row runs as, submitting through a link that records what reaches it. The row owns the server.
    private static WorldInstance Counted(HostRow row, CountingLink link) => new(
        documentOrigin: row.Instance.Origin,
        federation: row.Instance.Federation,
        link: link,
        name: row.Instance.Name,
        origin: () => row.Instance.Name,
        ownedMachines: null,
        server: row.Server
    );
    private static Vector3 CrateIn(HostRow row) => ((Vector3)WorldDefinitionRows.FindPlacement(id: "crate1", placements: row.Server.Definition.Placements)!.Position);

    [Fact]
    public void ARowSetAppliesInTheAddressedInstanceAndNeverInBoot() {
        using var boot = EditorPlacementFixtures.Build();
        using var north = HostRow.Build(definition: boot.Server.Definition, name: "north");
        var registry = new CommandRegistry(modules: [
            new WorldRowCommandModule(authority: new AddressedAuthority(instance: north.Instance), echoes: new WorldDeferredVerbEchoes()),
        ]);
        var moved = (WorldDefinitionRows.FindPlacement(id: "crate1", placements: north.Server.Definition.Placements)! with { Position = new Vector3(x: 5f, y: 3f, z: -1f) });
        var before = CrateIn(row: boot);
        var result = registry.Submit(line: $"world.row.set placements {JsonSerializer.Serialize(value: moved, jsonTypeInfo: WorldJsonContext.Default.WorldPlacement)}");

        Assert.False(condition: result.IsError, userMessage: result.Output);
        north.Server.Advance(stepTicks: Fixtures.StepTicks);
        boot.Server.Advance(stepTicks: Fixtures.StepTicks);
        Assert.Equal(actual: CrateIn(row: north), expected: new Vector3(x: 5f, y: 3f, z: -1f));

        // Red leg: the boot instance, whose link the row doors once held, never sees the edit.
        Assert.Equal(actual: CrateIn(row: boot), expected: before);
        Assert.NotSame(actual: north.Instance.SubmissionLink, expected: boot.Instance.SubmissionLink);
    }
    [InlineData("world.grant seat1 control composition", typeof(WorldSubmissionPayload.Grant))]
    [InlineData("world.group.form party1 party", typeof(WorldSubmissionPayload.Mutation))]
    [InlineData("world.population.spawn disc 3 5", typeof(WorldSubmissionPayload.Mutation))]
    [InlineData("machine.operation m 1 op1 {\"a\":1}", typeof(WorldSubmissionPayload.Operation))]
    [InlineData("world.state.cell.remove score a", typeof(WorldSubmissionPayload.Mutation))]
    [Theory]
    public void EachSubmittingVerbReachesTheAddressedInstanceAndNeverBoot(string line, Type payload) {
        using var bootRow = EditorPlacementFixtures.Build();
        using var northRow = HostRow.Build(definition: bootRow.Server.Definition, name: "north");
        var bootLink = new CountingLink(target: bootRow.Instance.Link);
        var northLink = new CountingLink(target: northRow.Instance.Link);
        var boot = Counted(link: bootLink, row: bootRow);
        var authority = new AddressedAuthority(instance: Counted(link: northLink, row: northRow));
        var echoes = new WorldDeferredVerbEchoes();
        var registry = new CommandRegistry(modules: [
            new WorldGrantCommandModule(authority: authority),
            new WorldGroupCommandModule(authority: authority),
            new WorldLookCommandModule(authority: authority),
            new WorldMachineCommandModule(authority: authority),
            new WorldStateCommandModule(authority: authority, echoes: echoes),
        ]);

        _ = registry.Submit(line: line);
        Assert.Equal(actual: northLink.Submitted.Select(selector: each => each.GetType()), expected: [payload]);
        Assert.Empty(collection: bootLink.Submitted);

        // Red leg: boot's own link carries the same verb once the console addresses boot, so its silence above is the
        // address, not a link that records nothing.
        authority.Instance = boot;
        _ = registry.Submit(line: line);
        Assert.Equal(actual: bootLink.Submitted.Select(selector: each => each.GetType()), expected: [payload]);
        _ = Assert.Single(collection: northLink.Submitted);
    }
}
