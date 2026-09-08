using System.Globalization;

namespace Puck.World.Server;

/// <summary>The identity facts the world carries on its reserved <see cref="WorldIdentityFactLane"/> row: a body's
/// lane loads from its identity's persisted facts row when a seat binds one, zeroes when the seat unbinds, and a
/// <c>setIdentityFact</c> effect writes the lane and the identity's row together, persisting through the owned-world
/// catalog's ordinary save. The lane writes ride the rule frame, so they fold, journal, undo, and replay like every
/// other rule-written cell.</summary>
public sealed partial class WorldServer {
    private readonly List<PendingIdentityFact> m_pendingIdentityFacts = [];
    private readonly Stack<int> m_pendingIdentityFactMarks = new();

    private WorldIdentity?[] m_identityLaneBound = [];
    private int[] m_identityLaneRevision = [];

    // A fact written inside a transaction's preflight persists only when the transaction commits.
    private readonly record struct PendingIdentityFact(WorldIdentity Identity, CellName Key, long Value);

    private void BeginIdentityFactScope() => m_pendingIdentityFactMarks.Push(item: m_pendingIdentityFacts.Count);
    private void EndIdentityFactScope() {
        var mark = m_pendingIdentityFactMarks.Pop();

        m_pendingIdentityFacts.RemoveRange(index: mark, count: (m_pendingIdentityFacts.Count - mark));
    }
    private void CommitIdentityFactScope() {
        _ = m_pendingIdentityFactMarks.Pop();

        if (m_pendingIdentityFactMarks.Count > 0) {
            return;
        }

        foreach (var pending in m_pendingIdentityFacts) {
            PersistIdentityFact(identity: pending.Identity, key: pending.Key, value: pending.Value);
        }

        m_pendingIdentityFacts.Clear();
    }
    // Every body whose identity binding, or whose identity's own facts row, moved since the last sync reloads its
    // lane: a body's cells the identity does not carry read 0, every fact it carries lands as a lane write. The
    // frame is loaded and active when this runs, so the writes queue for this tick's own fold and every rule
    // evaluating this tick reads them.
    private void SyncIdentityFactLanes(ulong tick) {
        var frame = m_ruleFrame!;

        if (frame.Find(name: WorldIdentityFactLane.RowName) is not { } lane) {
            return;
        }

        var capacity = m_population.Capacity;

        if (m_identityLaneBound.Length != capacity) {
            m_identityLaneBound = new WorldIdentity?[capacity];
            m_identityLaneRevision = new int[capacity];
        }

        for (var index = 0; (index < capacity); index++) {
            var profile = Body(index: index)?.Profile;
            var revision = (profile?.FactsRevision ?? 0);

            if (ReferenceEquals(objA: m_identityLaneBound[index], objB: profile) && (m_identityLaneRevision[index] == revision)) {
                continue;
            }

            ReloadIdentityFactLane(frame: frame, lane: lane, bodyIndex: index, profile: profile, tick: tick);
            m_identityLaneBound[index] = profile;
            m_identityLaneRevision[index] = revision;
        }
    }
    private void ReloadIdentityFactLane(StateFrame frame, StateRow lane, int bodyIndex, WorldIdentity? profile, ulong tick) {
        var facts = profile?.Facts;
        var count = frame.CellCount(row: lane);

        for (var cell = 0; (cell < count); cell++) {
            if (
                !frame.TryKeyAt(row: lane, index: cell, key: out var key) ||
                !WorldIdentityFactLane.TryParse(key: key.Value, bodyIndex: out var owner, fact: out var fact) ||
                (owner != bodyIndex) ||
                ((facts is not null) && (StateRows.FindCell(cells: facts.Cells, key: CellName.Parse(candidate: fact.ToString())) is not null))
            ) {
                continue;
            }

            WriteIdentityLane(frame: frame, lane: lane, key: key, value: 0L, tick: tick);
        }

        if (facts?.Cells is not { } carried) {
            return;
        }

        foreach (var carriedCell in carried) {
            WriteIdentityLane(frame: frame, lane: lane, key: CellName.Parse(candidate: WorldIdentityFactLane.Key(bodyIndex: bodyIndex, fact: carriedCell.Key.Value)), value: carriedCell.Value, tick: tick);
        }
    }
    // A lane write that would leave the cell as it is queues nothing, so a reload after a persist that already
    // mirrored the lane moves no row version.
    private bool WriteIdentityLane(StateFrame frame, StateRow lane, CellName key, long value, ulong tick) {
        if (frame.TryStored(row: lane, key: key, value: out var stored, text: out _) && (stored == value)) {
            return true;
        }
        if (((IRuleHost)this).TryApply(mutation: new StateMutation.UpsertCell(Row: WorldIdentityFactLane.RowName, Key: key.Value, Value: value, Write: StateWriteKind.Set), tick: tick, preflight: false, reason: out var reason)) {
            return true;
        }
        if (m_output.HasNarrationSink) {
            m_output.Narrate(channel: "world.identity", text: $"[world.identity: lane cell '{key}' refused — {reason}]");
        }

        return false;
    }
    // Applied when a lane write queued, Skipped when the lane already held the value (so a quiet tick delivers
    // nothing), Refused by name otherwise.
    private EffectOutcome FireIdentityFactEffect(IdentityFactEffect effect, string ruleName, ulong tick, bool preflight) {
        var spelled = ResolveOperandKey(key: effect.Key, keyFrom: effect.KeyFrom, tick: tick);

        if (
            !long.TryParse(s: spelled, style: NumberStyles.AllowLeadingSign, provider: CultureInfo.InvariantCulture, result: out var resolved) ||
            (resolved < 0L) ||
            (resolved > int.MaxValue) ||
            (Body(index: ((int)resolved)) is not { } body)
        ) {
            m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.BodyInactive, ruleName: ruleName, effect: effect, tick: tick, detail: $"key '{spelled}' names no active body");

            return EffectOutcome.Refused;
        }

        var bodyIndex = ((int)resolved);

        if (body.Profile is not { Document: not null } identity) {
            m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.IdentityUnbound, ruleName: ruleName, effect: effect, tick: tick, detail: $"body:{bodyIndex} drives under no owned identity — a fact is refused, never minted for an anonymous seat");

            return EffectOutcome.Refused;
        }

        var frame = EnsureRuleFrame();

        if (frame.Find(name: WorldIdentityFactLane.RowName) is not { } lane) {
            m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.IdentityFactUnwritable, ruleName: ruleName, effect: effect, tick: tick, detail: $"the installed document declares no '{WorldIdentityFactLane.RowName}' lane row");

            return EffectOutcome.Refused;
        }

        long value;

        if (effect.Expression is { } program) {
            if (!TryEvaluateExpression(program: program, kind: CellKind.Int, tick: tick, value: out value)) {
                m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.IdentityFactUnwritable, ruleName: ruleName, effect: effect, tick: tick, detail: "the value expression faulted");

                return EffectOutcome.Refused;
            }
        } else {
            value = effect.RawValue;
        }

        var key = effect.LaneKey(bodyIndex: bodyIndex, capacity: m_population.Capacity);
        var unchanged = (frame.TryStored(row: lane, key: key, value: out var stored, text: out _) && (stored == value));

        if (!unchanged && !((IRuleHost)this).TryApply(mutation: new StateMutation.UpsertCell(Row: WorldIdentityFactLane.RowName, Key: key.Value, Value: value, Write: StateWriteKind.Set), tick: tick, preflight: preflight, reason: out var reason)) {
            m_evaluator.ReportRefusal(refusal: WorldRuleEffectRefusal.IdentityFactUnwritable, ruleName: ruleName, effect: effect, tick: tick, detail: reason);

            return EffectOutcome.Refused;
        }

        if (preflight) {
            m_pendingIdentityFacts.Add(item: new PendingIdentityFact(Identity: identity, Key: effect.Fact, Value: value));
        } else {
            PersistIdentityFact(identity: identity, key: effect.Fact, value: value);
        }

        return (unchanged ? EffectOutcome.Skipped : EffectOutcome.Applied);
    }
    private void PersistIdentityFact(WorldIdentity identity, CellName key, long value) {
        if (!m_profiles.TrySetFact(identity: identity, key: key, value: value, changed: out _, reason: out var reason) && m_output.HasNarrationSink) {
            m_output.Narrate(channel: "world.identity", text: $"[world.identity: fact '{key}' on world:{identity.Id} not persisted — {reason}]");
        }
    }
}
