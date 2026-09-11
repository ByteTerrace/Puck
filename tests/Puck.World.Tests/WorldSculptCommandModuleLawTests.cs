using System.Text.Json.Nodes;

using Puck.Commands;
using Puck.World.Authoring.Sculpting;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <c>creation.sculpt</c> submits every row a sculpt's patch touched as an ordinary <see cref="WorldMutation"/>
/// carrying the principal the issuing ingress stamped — a seat's own sculpt lands as that seat, is refused when the
/// seat lacks <see cref="WorldCapability.Mutate"/> over the section (the control: the console's identical sculpt
/// lands), collides by name with an edit already buffered against the same row in the tick window, and turns a
/// patch fault into a named refusal that submits nothing.
/// </summary>
public sealed class WorldSculptCommandModuleLawTests {
    private sealed class FakeConsoleAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;

            return true;
        }
    }
    // Sets render.farDistance — one keyless-section member, so the touched row is the render section itself.
    private sealed class FarDistanceSculpt : ICreationSculpt {
        public const string SculptName = "law-far-distance";
        public const float FarDistance = 64f;

        public string Name => SculptName;
        public string Description => "law fixture";

        public SculptPatch Sculpt(SculptContext context) => new SculptPatch().SetMember(
            path: "render.farDistance",
            value: JsonValue.Create(value: FarDistance)
        );
    }
    // A member path with an unterminated selector — a patch fault, not a document change.
    private sealed class FaultingSculpt : ICreationSculpt {
        public const string SculptName = "law-faulting";
        public const string MalformedPath = "render.lighting[name=key";

        public string Name => SculptName;
        public string Description => "law fixture";

        public SculptPatch Sculpt(SculptContext context) => new SculptPatch().SetMember(
            path: MalformedPath,
            value: new JsonObject()
        );
    }
    private sealed class Harness : IDisposable {
        private readonly HostRow m_row;
        private readonly TextCommandSource m_source;

        public Harness() {
            CreationSculptRegistry.Register(sculpt: new FarDistanceSculpt());
            CreationSculptRegistry.Register(sculpt: new FaultingSculpt());

            m_row = HostRow.Build(
                definition: Fixtures.BuildDocument(),
                name: "boot"
            );

            var authority = new FakeConsoleAuthority(instance: m_row.Instance);
            var echoes = new WorldDeferredVerbEchoes();
            var guard = new WorldRowStepWindowGuard();
            var registry = new CommandRegistry(modules: [
                new WorldRowCommandModule(authority: authority, echoes: echoes, link: m_row.Instance.Link, stepGuard: guard),
                new WorldSculptCommandModule(authority: authority, echoes: echoes, link: m_row.Instance.Link, stepGuard: guard),
            ]);

            m_source = new TextCommandSource(registry: registry);
            m_row.Server.MutationTap = (mutation, principal) => Submitted.Add(item: (mutation, principal));
            m_row.Server.MutationOutcomeTap = (mutation, applied) => Outcomes.Add(item: (mutation, applied));
        }

        public List<(WorldMutation Mutation, WorldPrincipal Principal)> Submitted { get; } = [];
        public List<(WorldMutation Mutation, bool Applied)> Outcomes { get; } = [];
        public WorldServer Server => m_row.Server;

        public CommandResult Submit(CommandPrincipal principal, string line) {
            var result = CommandResult.None;

            using var session = m_source.CreateSession(
                onResult: (_, value) => result = value,
                principal: principal
            );

            session.Enqueue(line: line);
            m_source.Collect();

            return result;
        }
        public void Step() => m_row.Server.Advance(stepTicks: Fixtures.StepTicks);
        public void Dispose() => m_row.Dispose();
    }

    /// <summary>A seat's sculpt is submitted as that seat — the mutation carries the stamped principal, not the
    /// console's — and applies under its seeded grant.</summary>
    [Fact]
    public void SeatSculptCarriesTheSeatPrincipalAndApplies() {
        using var harness = new Harness();
        var seat = CommandPrincipal.Seat(slot: 1);

        var result = harness.Submit(
            line: $"creation.sculpt {FarDistanceSculpt.SculptName}",
            principal: seat
        );

        Assert.False(condition: result.IsError, userMessage: result.Output);
        Assert.Contains(expectedSubstring: "planned=", actualString: result.Output, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: $"render set submitted as {WorldPrincipal.Seat(slot: 1).Describe()}", actualString: result.Output, comparisonType: StringComparison.Ordinal);

        var submitted = Assert.Single(collection: harness.Submitted);

        Assert.IsType<WorldMutation.SetRenderDefaults>(@object: submitted.Mutation);
        Assert.Equal(expected: WorldPrincipal.Seat(slot: 1), actual: submitted.Mutation.Principal);
        Assert.Equal(expected: WorldPrincipal.Seat(slot: 1), actual: submitted.Principal);

        harness.Step();

        Assert.Equal(expected: FarDistanceSculpt.FarDistance, actual: harness.Server.Definition.Render.FarDistance);
    }
    /// <summary>With the seat's Mutate over the render section revoked, the seat's sculpt is refused at apply and
    /// the document is unchanged; the console's identical sculpt (the control) lands.</summary>
    [Fact]
    public void SeatSculptIsRefusedWithoutMutateOverTheSection_ConsoleControlLands() {
        using var harness = new Harness();
        var seat = CommandPrincipal.Seat(slot: 1);
        var hold = new WorldGrant(
            Capability: WorldCapability.Mutate,
            Exclusive: false,
            Principal: WorldPrincipal.Seat(slot: 1),
            Subject: GrantSubject.Section(section: WorldSection.Render)
        );

        harness.Server.Revoke(
            actor: WorldPrincipal.Console,
            grant: hold
        );

        Laws.RefusalWithControl(
            lawId: "creation.sculpt.acting-principal",
            deniedOutcome: () => SculptLands(harness: harness, principal: seat),
            controlOutcome: () => SculptLands(harness: harness, principal: CommandPrincipal.Console)
        );
    }
    private static bool SculptLands(Harness harness, CommandPrincipal principal) {
        harness.Submitted.Clear();
        harness.Outcomes.Clear();

        var result = harness.Submit(
            line: $"creation.sculpt {FarDistanceSculpt.SculptName}",
            principal: principal
        );

        Assert.False(condition: result.IsError, userMessage: result.Output);

        harness.Step();

        var outcome = Assert.Single(collection: harness.Outcomes);
        var landed = (harness.Server.Definition.Render.FarDistance == FarDistanceSculpt.FarDistance);

        Assert.Equal(expected: landed, actual: outcome.Applied);

        return landed;
    }
    /// <summary>A row with an edit already buffered in the tick window refuses the sculpt by name before anything
    /// is submitted; the same sculpt lands once the window has drained (the control).</summary>
    [Fact]
    public void SculptCollidesWithAPendingRowEditInTheSameWindow_LandsAfterTheDrain() {
        using var harness = new Harness();

        var edit = harness.Submit(
            line: "world.row.set render farDistance 32",
            principal: CommandPrincipal.Console
        );

        Assert.False(condition: edit.IsError, userMessage: edit.Output);
        Assert.Single(collection: harness.Submitted);

        var collided = harness.Submit(
            line: $"creation.sculpt {FarDistanceSculpt.SculptName}",
            principal: CommandPrincipal.Console
        );

        Assert.True(condition: collided.IsError, userMessage: collided.Output);
        Assert.Contains(expectedSubstring: "row 'render' already has an edit buffered this tick", actualString: collided.Output, comparisonType: StringComparison.Ordinal);
        Assert.Single(collection: harness.Submitted);

        harness.Step();

        Assert.Equal(expected: 32f, actual: harness.Server.Definition.Render.FarDistance);

        var landed = harness.Submit(
            line: $"creation.sculpt {FarDistanceSculpt.SculptName}",
            principal: CommandPrincipal.Console
        );

        Assert.False(condition: landed.IsError, userMessage: landed.Output);

        harness.Step();

        Assert.Equal(expected: FarDistanceSculpt.FarDistance, actual: harness.Server.Definition.Render.FarDistance);
    }
    /// <summary>A patch that faults (a malformed member path) is a named refusal that submits nothing — never an
    /// escaped exception, never a partial apply.</summary>
    [Fact]
    public void PatchFaultIsANamedRefusalThatSubmitsNothing() {
        using var harness = new Harness();

        var result = harness.Submit(
            line: $"creation.sculpt {FaultingSculpt.SculptName}",
            principal: CommandPrincipal.Console
        );

        Assert.True(condition: result.IsError, userMessage: result.Output);
        Assert.Contains(expectedSubstring: $"[creation.sculpt: {FaultingSculpt.SculptName}: patch fault", actualString: result.Output, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "unterminated selector", actualString: result.Output, comparisonType: StringComparison.Ordinal);
        Assert.Empty(collection: harness.Submitted);
    }
    /// <summary>An unknown sculpt name is refused by name, listing the registry.</summary>
    [Fact]
    public void UnknownSculptIsRefusedByName() {
        using var harness = new Harness();

        var result = harness.Submit(
            line: "creation.sculpt not-a-real-sculpt",
            principal: CommandPrincipal.Console
        );

        Assert.True(condition: result.IsError, userMessage: result.Output);
        Assert.Contains(expectedSubstring: "unknown sculpt 'not-a-real-sculpt'", actualString: result.Output, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: FarDistanceSculpt.SculptName, actualString: result.Output, comparisonType: StringComparison.Ordinal);
        Assert.Empty(collection: harness.Submitted);
    }
}
