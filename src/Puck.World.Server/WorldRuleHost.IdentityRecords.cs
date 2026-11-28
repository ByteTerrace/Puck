using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The identity record store a rule writes: a claim, pair claim, field write or release lands in a stage over
/// the addressed body's identity store, opened once per identity per firing, which the firing's journal rewinds and its
/// commit installs through the account's one door (<see cref="WorldOwnedWorlds.TryCommit"/>).</summary>
/// <remarks>Invariant: an identity's record store changes only through <see cref="WorldIdentity.Apply"/> of a stage
/// whose every write passed <see cref="WorldIdentityWriter.Admits"/> for this world's package. The stage's mutators
/// take the writer, so no rule path writes the store without one; the account's door decides ownership and saves.</remarks>
public sealed partial class WorldRuleHost {
    private readonly Dictionary<WorldIdentity, WorldIdentityStage> m_identityStages = new(comparer: ReferenceEqualityComparer.Instance);
    private IdentityStageJournal? m_identityStageJournal;
    private long m_identityStageGeneration;
    private ulong m_identityStageTick;
    private ulong m_identityCommitTick;
    private int m_identityCommitOrdinal;

    // The stage this firing writes the identity's store through, opened and journalled on first use. A firing the
    // evaluator rewinds drops every stage it opened, so the identity is never touched.
    private bool TryIdentityStage(WorldIdentity identity, ulong tick, out WorldIdentityStage stage, out string reason) {
        if (m_identityStages.TryGetValue(key: identity, value: out stage!)) {
            reason = string.Empty;
            return true;
        }
        if (!identity.TryOpenStage(stage: out stage, reason: out reason)) {
            return false;
        }
        if (m_identityStages.Count == 0) {
            Host.Arena.Journal.RecordParticipant(participant: (m_identityStageJournal ??= new IdentityStageJournal(host: this)), words: [m_identityStageGeneration]);
            m_identityStageTick = tick;
        }
        m_identityStages.Add(key: identity, value: stage);
        return true;
    }

    private bool FireIdentityRecord(WorldIdentityRecordEffect effect, in EffectFiring firing, out EffectRefusal refusal) {
        var bodyIndex = BodyIndexOf(source: firing.Values[WorldIdentityRecordEffect.BodySource]);

        if (Host.Body(index: bodyIndex) is not { } body) {
            return Refuse(code: WorldRuleEffectRefusal.BodyInactive, reason: $"body:{bodyIndex} is inactive", refusal: out refusal);
        }
        if (body.Profile is not { } identity) {
            return Refuse(code: WorldRuleEffectRefusal.IdentityUnbound, reason: $"body:{bodyIndex} drives under no identity — a record is refused, never minted for an anonymous seat", refusal: out refusal);
        }
        if (!TryIdentityStage(identity: identity, reason: out var reason, stage: out var stage, tick: firing.Tick)) {
            return Unwritable(reason: reason, refusal: out refusal);
        }
        if (!TryArmValue(position: effect.SlotPosition, source: effect.Slot, value: out var slot, values: firing.Values) ||
            !TryArmValue(position: effect.SecondPosition, source: effect.Second, value: out var second, values: firing.Values) ||
            (slot is < int.MinValue or > int.MaxValue) || (second is < int.MinValue or > int.MaxValue)) {
            return Unwritable(reason: "a slot names no number a slot can be", refusal: out refusal);
        }

        var writer = IdentityWriter;
        var generation = (effect.HasSecond ? second : (long?)null);
        var ok = effect.Op switch {
            WorldIdentityRecordOp.Release => stage.TryRelease(generation: generation, pool: effect.Pool, reason: out reason, slot: (int)slot, writer: writer),
            _ => TryWriteFields(effect: effect, firing: in firing, generation: generation, reason: out reason, second: (int)second, slot: (int)slot, stage: stage, writer: writer),
        };

        return (ok
            ? Accept(refusal: out refusal)
            : Unwritable(reason: reason, refusal: out refusal));

        static bool Accept(out EffectRefusal refusal) {
            refusal = EffectRefusal.None;
            return true;
        }
        static bool Unwritable(string reason, out EffectRefusal refusal) => Refuse(code: WorldRuleEffectRefusal.IdentityRecordUnwritable, reason: reason, refusal: out refusal);
    }
    // A claim, pair claim or write: every field's value in the kind the identity's record declares for it, then the one
    // stage operation, which admits the writer and lands all or nothing.
    private bool TryWriteFields(WorldIdentityRecordEffect effect, in EffectFiring firing, WorldIdentityStage stage, WorldIdentityWriter writer, int slot, int second, long? generation, out string reason) {
        var values = new WorldIdentityField[effect.Fields.Count];

        for (var index = 0; (index < values.Length); index++) {
            var input = effect.Fields[index];
            var identityId = (input.IdentityOf ? Host.Body(index: BodyIndexOf(source: firing.Values[input.Position]))?.Profile?.Id : null);

            if (!stage.TryFieldKind(field: input.Field, kind: out var kind, pool: effect.Pool, reason: out reason) ||
                !input.TryValue(identityId: identityId, kind: kind, reason: out reason, value: out var value, values: firing.Values)) {
                return false;
            }
            values[index] = new WorldIdentityField(Field: input.Field, Value: value);
        }

        return effect.Op switch {
            WorldIdentityRecordOp.Claim => stage.TryClaim(generation: out _, pool: effect.Pool, reason: out reason, slot: out _, values: values, writer: writer),
            WorldIdentityRecordOp.ClaimPair => stage.TryClaimPair(generation: out _, leftSlot: slot, pairPool: effect.Pool, reason: out reason, rightSlot: second, slot: out _, values: values, writer: writer),
            _ => TryWriteEach(generation: generation, pool: effect.Pool, reason: out reason, slot: slot, stage: stage, values: values, writer: writer),
        };

        static bool TryWriteEach(WorldIdentityStage stage, WorldIdentityWriter writer, CellName pool, int slot, long? generation, WorldIdentityField[] values, out string reason) {
            foreach (var value in values) {
                if (!stage.TryWrite(field: value.Field, generation: generation, pool: pool, reason: out reason, slot: slot, value: value.Value, writer: writer)) {
                    return false;
                }
            }
            reason = string.Empty;
            return true;
        }
    }
    // Installs every stage the committed firing wrote, each crediting an award id that a re-run of the same tick (a world
    // restored from an image behind the account) spells again, so the account credits it once.
    private void FlushIdentityStages() {
        if (m_identityStages.Count == 0) {
            return;
        }

        var tick = m_identityStageTick;

        if (tick != m_identityCommitTick) {
            m_identityCommitTick = tick;
            m_identityCommitOrdinal = 0;
        }

        var account = Host.Profiles;

        foreach (var (identity, stage) in m_identityStages) {
            var award = AwardId(identity: identity, ordinal: m_identityCommitOrdinal++, tick: tick);

            if ((!account.TryCommit(award: award, durability: out var durability, reason: out var reason, stage: stage) || (durability == WorldWriteDurability.Uncertain)) &&
                Host.Output.HasNarrationSink) {
                Host.Output.Narrate(channel: "world.identity", text: $"[world.identity: record store of world:{identity.Id} {durability.ToString().ToLowerInvariant()} — {reason}]");
            }
        }
        m_identityStages.Clear();
        m_identityStageGeneration++;
        m_identityCommits++;
    }
    // The award id of one committed stage: this world's authority, the tick and the stage's place among the tick's
    // commits, and the identity — a function of the run, never of the wall clock.
    private CellName AwardId(WorldIdentity identity, ulong tick, int ordinal) {
        var hash = Puck.Maths.Fnv1aHash.Create();

        hash.Add(value: Host.AuthorityIdentity);
        hash.Add(value: identity.Id);
        hash.Add(value: tick);
        hash.Add(value: ((uint)ordinal));
        return WorldIdentityRows.Award(digest: hash.Value);
    }

    // Drops the stages a rewound firing opened. A stage generation the journal recorded that is no longer current
    // belongs to a firing already committed, so its rewind leaves the next firing's stages alone.
    private sealed class IdentityStageJournal(WorldRuleHost host) : IJournalParticipant {
        void IJournalParticipant.Undo(ReadOnlySpan<long> words) {
            if (words[0] == host.m_identityStageGeneration) {
                host.m_identityStages.Clear();
                host.m_identityStageGeneration++;
            }
        }
    }
}
