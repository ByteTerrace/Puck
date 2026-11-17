using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    private BodyGateReader? m_gateReader;

    // Compiler admission restricts these programs to action facts. No arena, binding, or expression can be read.
    // The adapter carries only the current call's lane and intent; neither is persisted or hashed.
    private bool GateOpen(GateToken[] gate, int recencyLane, in PlayerIntent intent) {
        if (gate.Length == 0) { return true; }
        var reader = (m_gateReader ??= new BodyGateReader(body: this));

        reader.Lane = recencyLane;
        reader.Intent = intent;
        var frame = new EvaluationFrame(reader: reader);

        return RuleEvaluation.Decide(ref frame, gate) switch {
            Verdict.Open => true,
            Verdict.Closed => false,
            Verdict.Faulted => throw new InvalidOperationException(message: "A compiled action fact gate faulted."),
            _ => throw new InvalidOperationException(message: "Unknown gate verdict."),
        };
    }

    private sealed class BodyGateReader(WorldBody body) : IStateReader, IActionGateFacts {
        public int Lane { get; set; }
        public PlayerIntent Intent { get; set; }
        public ArenaScratch Scratch { get; } = new();
        public StateArena Arena => throw new InvalidOperationException(message: "An action gate cannot read arena rows.");
        public Span<long> Locals => [];
        public Span<long> PatternWord => [];
        public ulong EngineTick => 0;
        public ulong Tick => 0;
        public CellKey BoundEachKey { get; set; }
        public CellKey BoundPreviousKey { get; set; }
        public CellKey BoundTokenKey { get; set; }

        public int BoundIndex(BoundKey key) => -1;
        public bool Now(int fact) => body.FactHolds(fact: ((Puck.Physics.Motion.ActionFact)fact));
        public bool Recently(int slot) => (((Lane < 0) ? body.m_motionRecency[slot] : body.LaneRecency(lane: Lane)[slot]) > 0);
        public FixedQ4816 Counter(int slot) => body.StateCounter(slot: slot);
        public bool TimerElapsed(int slot) => (body.StateTimer(slot: slot) == 0);
        public bool Held(int channel) => (Intent[channel] >= body.m_channelThresholds[channel]);
    }
}
