using Puck.State.Rules;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: a compiled world arm resolves its facet from the host it is handed. A host serving
/// <see cref="IWorldFacts"/> reaches the arm door with the effect; a host serving no world facet reaches the same
/// door and its default refuses the arm by name, so an unbound world arm is a counted refusal rather than a
/// throw.</summary>
public sealed class WorldFactEffectFiringLawTests {
    private static EffectFiring Firing() => new(
        EngineTick: 0UL,
        RuleName: "cue",
        StepTicks: 1UL,
        Tick: 0UL
    );
    private static WorldCueEffect Cue() => new(
        body: null,
        cue: "ping",
        describe: "cue 'ping'",
        payload: null
    );
    private static StateSection Section() => new(Rows: [new StateRow(
            Name: CellName.Parse(candidate: "score"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: StateRow.SlotKey,
                    Value: CellValue.Int(value: 0L)
                )]
        )]);

    [Fact]
    public void AWorldArmReachingAHostServingNoWorldFacetIsACountedRefusalRatherThanAThrow() {
        var host = new ArenaEffectHost(arena: new StateArena(
            catalog: StateCatalog.Compile(section: Section()),
            options: null,
            section: Section(),
            time: ArenaTime.Origin
        ));

        var firing = Firing();

        Assert.False(condition: ((IRuleEffect)Cue()).TryFire(
            firing: in firing,
            host: host,
            refusal: out var refusal
        ));
        Assert.Equal(
            actual: refusal.Code,
            expected: StateEffectRefusal.ArmUnbound
        );
    }
    [Fact]
    public void AWorldArmReachingAHostServingTheWorldFacetReachesTheArmDoor() {
        var host = new FactsHost(arena: new StateArena(
            catalog: StateCatalog.Compile(section: Section()),
            options: null,
            section: Section(),
            time: ArenaTime.Origin
        ));
        var effect = Cue();
        var firing = Firing();

        Assert.True(condition: ((IRuleEffect)effect).TryFire(
            firing: in firing,
            host: host,
            refusal: out var refusal
        ));
        Assert.False(condition: refusal.IsRefused);
        Assert.Same(
            actual: host.Fired,
            expected: effect
        );
    }
    [Fact]
    public void APairReadDoesNotMintButItsWriteResolverAdmitsInsideTheFiringScope() {
        var section = Section();
        var catalog = StateCatalog.Compile(section: section);
        var host = new FactsHost(arena: new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        ));
        IRuleKey pair = new WorldPairKeyFact(key: new PairKeyFact(
            bodyA: new CompiledBodyRef(CompiledBodyRefKind.Literal, Index: 2, Row: null),
            bodyB: new CompiledBodyRef(CompiledBodyRefKind.Literal, Index: 7, Row: null)
        ));
        var before = host.Arena.Keys.Count;

        Assert.True(condition: pair.Resolve(reader: host).TryNamed(value: out var unseen));
        Assert.False(condition: unseen.IsValid);
        Assert.Equal(
            actual: host.Arena.Keys.Count,
            expected: before
        );
        var written = pair.ResolveForWrite(reader: host);

        Assert.True(condition: written.TryResolved(key: out var admitted), userMessage: written.Reason);
        Assert.True(condition: admitted.IsValid);
        Assert.Equal(
            actual: host.Arena.Keys.Count,
            expected: (before + 1)
        );
    }

    // A host that serves the world facet and answers no operand: the laws here fire one cue, which resolves nothing
    // through the facet and lands on the arm door.
    private sealed class FactsHost : ArenaEffectHost, IWorldFacts {
        public FactsHost(StateArena arena) : base(arena: arena) { }

        public ICompiledFact? Fired { get; private set; }

        public CellKey PairKey(PairKeyFact key) => default;
        public Fact Read(PopulationOperand operand) => throw Unserved();
        public Fact Read(PhysicsQuiescentOperand operand) => throw Unserved();
        public Fact Read(ClockOperand operand) => throw Unserved();
        public Fact Read(RegionOccupancyOperand operand) => throw Unserved();
        public Fact Read(PlacementInfluenceOperand operand) => throw Unserved();
        public Fact Read(MachineMemoryOperand operand) => throw Unserved();
        public Fact Read(ArgBodyOperand operand) => throw Unserved();
        public Fact Read(BodyDistanceOperand operand) => throw Unserved();
        public Fact Read(LineOfSightOperand operand) => throw Unserved();
        public Fact Read(ParkedOperand operand) => throw Unserved();
        public Fact Read(UprightOperand operand) => throw Unserved();
        public Fact Read(BodyFactOperand operand) => throw Unserved();
        public Fact Read(BodyPositionOperand operand) => throw Unserved();
        public Fact Read(EngagedOperand operand) => throw Unserved();
        public Fact Read(BodyStateOperand operand) => throw Unserved();
        public Fact Read(LinkStalenessOperand operand) => throw Unserved();
        public Fact Read(ChannelOperand operand) => throw Unserved();
        public Fact Read(WorldInputOperand operand) => throw Unserved();
        public Fact Read(PointerOperand operand) => throw Unserved();
        public Fact Read(NearestOperand operand) => throw Unserved();
        public Fact Read(NavigationOperand operand) => throw Unserved();
        public Fact Read(BoardCellOfOperand operand) => throw Unserved();
        public Fact Read(BoardSeatOperand operand) => throw Unserved();
        public int ResolveBody(in CompiledBodyRef bodyRef) => bodyRef.Index;
        public bool TryReadHostOwnedCell(int rowOrdinal, int cell, out long value) => throw Unserved();
        public bool TryReadHostOwnedSlot(int rowOrdinal, out CellValue value) => throw Unserved();
        public override bool Fire(ICompiledFact effect, in EffectFiring firing, out EffectRefusal refusal) {
            Fired = effect;
            refusal = EffectRefusal.None;

            return true;
        }

        private static NotSupportedException Unserved() => new(message: "This host answers no world operand.");
    }
}
