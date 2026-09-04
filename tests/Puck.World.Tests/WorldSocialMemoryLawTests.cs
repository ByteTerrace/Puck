using System.Diagnostics;
using System.Text.Json;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

[Collection(ConsoleRedirectionCollection.Name)]
public sealed class WorldSocialMemoryLawTests(ITestOutputHelper output) {
    private static readonly long One = FixedQ4816.One.Value;
    private static WorldEntityAddress Person(int index, int generation = 1, string authority = "world") => new(authority, index, generation);
    private static WorldSocialImpressionKey Key(int observer = 0, int subject = 1, int dimension = 0) => new(Person(observer), Person(subject), dimension);
    private static WorldSocialPolicy Policy(int impressions = 64, int receipts = 256) => new(
        [new(WorldCellName.Parse("kindness")), new(WorldCellName.Parse("reliability"), Minimum: 0, Baseline: 0.5m)],
        ImpressionCapacity: impressions, ImpressionsPerObserver: impressions, ReceiptCapacity: receipts,
        EvidenceAttemptsPerTick: 1024, ExpiredReceiptsPerTick: 16, EvidenceLifetimeSeconds: 1,
        ReliabilityDimension: "reliability");
    private static WorldSocialMemory Bank(WorldSocialPolicy? policy = null) => new(CompiledWorldSocialPolicy.Compile(policy ?? Policy()));
    private static WorldSocialEvidence Evidence(ulong sequence = 0, long? value = null, int? source = null, WorldSocialImpressionKey? key = null, ulong tick = 0) =>
        new(key ?? Key(), new(Person(1), "help.outcome", sequence), tick, value ?? One, One, source.HasValue ? Person(source.Value) : null);
    private static WorldSocialImpression Read(WorldSocialMemory bank, WorldSocialImpressionKey? key = null) {
        Assert.True(bank.TryRead(key ?? Key(), out var value)); return value;
    }

    [Fact]
    public void UnknownBaselineIsNotKnowledgeAndReadsDoNotMutateState() {
        var bank = Bank(); var hash = bank.StateHash;
        Assert.Equal(new(false, 0, 0, 0, 0, 0, 0), Read(bank));
        Assert.Equal(One / 2, Read(bank, Key(dimension: 1)).Value);
        Assert.False(bank.TryRead(Key(dimension: 99), out _));
        Assert.Equal(hash, bank.StateHash);
    }

    [Fact]
    public void OneMistakeIsBoundedAndDoesNotChangeOtherDimensionsOrTheReverseRelationship() {
        var bank = Bank();
        for (ulong i = 0; i < 40; i++) { Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence(i))); }
        var before = Read(bank);
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence(40, -One)));
        var after = Read(bank);
        Assert.InRange(before.Value - after.Value, 1, One / 4);
        Assert.True(after.Value > 0);
        Assert.True(after.Uncertainty > before.Uncertainty);
        Assert.Equal(41UL, after.IndependentEvents);
        Assert.False(Read(bank, Key(observer: 1, subject: 0)).Known);
        Assert.False(Read(bank, Key(dimension: 1)).Known);
    }

    [Fact]
    public void ReportsUseLearnedReliabilityNotAffection() {
        var policy = Policy() with { UnfamiliarReliability = 0, ReportWeight = 1 };
        var bank = Bank(policy);
        for (ulong i = 0; i < 30; i++) {
            bank.Observe(Evidence(i, -One, key: Key(subject: 2)));
            bank.Observe(Evidence(i, One, key: Key(subject: 2, dimension: 1)));
        }
        Assert.True(Read(bank, Key(subject: 2)).Value < 0);
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence(source: 2)));
        Assert.True(Read(bank).Value > 0);
        Assert.Equal(WorldSocialEvidenceResult.ZeroWeight, bank.Observe(Evidence(source: 3, key: Key(subject: 4))));
        Assert.False(Read(bank, Key(subject: 4)).Known);
    }

    [Fact]
    public void RepeatedRelaysCannotAccumulateCorroborationOrConsumeMoreStorage() {
        var bank = Bank(); var evidence = Evidence(source: 2);
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(evidence));
        var before = Read(bank);
        for (var source = 3; source < 100; source++) {
            Assert.Equal(WorldSocialEvidenceResult.Duplicate, bank.Observe(evidence with { Source = Person(source) }));
        }
        Assert.Equal(before, Read(bank));
        Assert.Equal(1, bank.ReceiptCount); Assert.Equal(1, bank.ImpressionCount);
    }

    [Fact]
    public void ContradictoryCopiesRaiseUncertaintyOnlyOnceWithoutAddingSupport() {
        var bank = Bank(); bank.Observe(Evidence(source: 2));
        var original = Read(bank);
        Assert.Equal(WorldSocialEvidenceResult.Conflict, bank.Observe(Evidence(value: -One, source: 3)));
        var conflict = Read(bank);
        Assert.True(conflict.Uncertainty > 0);
        Assert.Equal(original.Value, conflict.Value); Assert.Equal(original.Weight, conflict.Weight);
        Assert.Equal(original.IndependentEvents, conflict.IndependentEvents);
        Assert.Equal(WorldSocialEvidenceResult.Duplicate, bank.Observe(Evidence(value: -One, source: 4)));
        Assert.Equal(conflict, Read(bank));
    }

    [Fact]
    public void DirectUpgradeCorrectsHearsayOnceButIsNotAnotherIndependentEvent() {
        var bank = Bank(); bank.Observe(Evidence(value: -One, source: 2));
        var report = Read(bank);
        Assert.Equal(WorldSocialEvidenceResult.Upgraded, bank.Observe(Evidence()));
        var direct = Read(bank);
        Assert.True(direct.Value > report.Value);
        Assert.Equal(1UL, direct.IndependentEvents);
        Assert.Equal(One, direct.Weight);
        Assert.Equal(WorldSocialEvidenceResult.Duplicate, bank.Observe(Evidence()));
        Assert.Equal(WorldSocialEvidenceResult.Duplicate, bank.Observe(Evidence(value: -One, source: 3)));
        Assert.Equal(direct, Read(bank));
    }

    [Fact]
    public void FreshDirectEvidenceReceivesBoundedFollowUpWeightButRelaysDoNot() {
        var withBoost = Policy();
        var withoutBoost = withBoost with { Dimensions = [withBoost.Dimensions[0] with { FollowUpBoost = 0 }, withBoost.Dimensions[1]] };
        var a = Bank(withBoost); var b = Bank(withoutBoost);
        foreach (var bank in new[] { a, b }) {
            bank.Observe(Evidence(source: 2));
            bank.Observe(Evidence(value: -One, source: 3));
        }
        Assert.Equal(Read(a), Read(b));
        a.Observe(Evidence(1)); b.Observe(Evidence(1));
        Assert.True(Read(a).Weight > Read(b).Weight);
        Assert.True(Read(a).Value > Read(b).Value);
        Assert.InRange(Read(a).Value - Read(b).Value, 1, One / 4);
    }

    [Fact]
    public void LockedValuesStillRememberEvidenceWithoutChangingPersonality() {
        var policy = Policy();
        policy = policy with { Dimensions = [policy.Dimensions[0] with { Baseline = -0.5m, LearningRate = 0 }, policy.Dimensions[1]] };
        var bank = Bank(policy);
        for (ulong i = 0; i < 32; i++) { bank.Observe(Evidence(i)); }
        Assert.Equal(-One / 2, Read(bank).Value);
        Assert.Equal(32UL, Read(bank).IndependentEvents);
        Assert.True(Read(bank).Weight > 0);
    }

    [Fact]
    public void DecayAndRecoveryAreLazyReadStableAndReachTheirAuthoredEndpoints() {
        var policy = Policy();
        policy = policy with { Dimensions = [policy.Dimensions[0] with { RecoverySeconds = 1, ConfidenceDecaySeconds = 1 }, policy.Dimensions[1]] };
        var bank = Bank(policy); bank.Observe(Evidence()); var initial = Read(bank);
        bank.Advance(25200); var hash = bank.StateHash; var half = Read(bank);
        Assert.Equal(initial.Value - initial.Value / 2, half.Value);
        Assert.Equal(initial.Weight / 2, half.Weight);
        Assert.Equal(half, Read(bank)); Assert.Equal(hash, bank.StateHash);
        bank.Advance(50400); var end = Read(bank);
        Assert.Equal(0, end.Value); Assert.Equal(0, end.Confidence); Assert.True(end.Known);
        Assert.Equal(1, bank.ReceiptCount); // maximum age is inclusive
        bank.Advance(50401); Assert.Equal(0, bank.ReceiptCount); Assert.True(Read(bank).Known);
    }

    [Fact]
    public void ForgettingDoesNotMakeAnOldRumorFreshEvenAfterRelearningTheSamePerson() {
        var bank = Bank(); var report = Evidence(source: 2);
        bank.Observe(report); Assert.True(bank.Forget(Key())); Assert.False(bank.Forget(Key()));
        Assert.Equal(1, bank.ReceiptCount);
        Assert.Equal(WorldSocialEvidenceResult.Duplicate, bank.Observe(report));
        Assert.Equal(WorldSocialEvidenceResult.Duplicate, bank.Observe(report with { Source = null }));
        Assert.False(Read(bank).Known);
        bank.Observe(Evidence(1, -One)); var relearned = Read(bank);
        Assert.Equal(WorldSocialEvidenceResult.Duplicate, bank.Observe(report with { Source = null }));
        Assert.Equal(relearned, Read(bank));
    }

    [Fact]
    public void SlotReuseDoesNotInheritOldOccupantsMemoriesAndAuthorityIsPartOfIdentity() {
        var bank = Bank(); bank.Observe(Evidence());
        Assert.False(Read(bank, Key() with { Observer = Person(0, 2) }).Known);
        Assert.False(Read(bank, Key() with { Subject = Person(1, 2) }).Known);
        Assert.False(Read(bank, Key() with { Observer = Person(0, authority: "other") }).Known);
        Assert.True(Read(bank).Known);
    }

    [Fact]
    public void FullReceiptLedgerRefusesWithoutEvictionAndCannotBeFreedByForgetting() {
        var bank = Bank(Policy(receipts: 1)); bank.Observe(Evidence());
        var before = Read(bank);
        Assert.Equal(WorldSocialEvidenceResult.ReceiptCapacityLimited, bank.Observe(Evidence(1, -One)));
        Assert.Equal(before, Read(bank)); Assert.Equal(1, bank.ReceiptCount);
        bank.Forget(Key());
        Assert.Equal(WorldSocialEvidenceResult.ReceiptCapacityLimited, bank.Observe(Evidence(1)));
        bank.Advance(50401);
        Assert.Equal(WorldSocialEvidenceResult.Stale, bank.Observe(Evidence()));
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence(1, tick: 50401)));
    }

    [Fact]
    public void ImpressionLimitsCountDimensionsAndDoNotChargeReceiptStorageOnRefusal() {
        var bank = Bank(Policy() with { ImpressionsPerObserver = 1 }); bank.Observe(Evidence());
        Assert.Equal(WorldSocialEvidenceResult.ImpressionCapacityLimited, bank.Observe(Evidence(key: Key(dimension: 1))));
        Assert.Equal(1, bank.ReceiptCount);
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence(key: Key(observer: 3))));
        bank.Forget(Key());
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence(key: Key(dimension: 1))));
        var full = Bank(Policy(impressions: 1)); full.Observe(Evidence());
        Assert.Equal(WorldSocialEvidenceResult.ImpressionCapacityLimited, full.Observe(Evidence(key: Key(observer: 2))));
    }

    [Fact]
    public void WorkBudgetCountsInvalidAndDuplicateAttemptsAndSameTickCannotRefreshIt() {
        var bank = Bank(Policy() with { EvidenceAttemptsPerTick = 2 });
        Assert.Equal(WorldSocialEvidenceResult.Invalid, bank.Observe(Evidence() with { Quality = -1 }));
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence()));
        Assert.Equal(WorldSocialEvidenceResult.WorkLimited, bank.Observe(Evidence()));
        var hash = bank.StateHash; bank.Advance(0); Assert.Equal(hash, bank.StateHash);
        Assert.Equal(WorldSocialEvidenceResult.WorkLimited, bank.Observe(Evidence(1)));
        bank.Advance(1); Assert.Equal(WorldSocialEvidenceResult.Duplicate, bank.Observe(Evidence()));
        Assert.Equal(1, bank.EvidenceAttempts);
        Assert.Throws<ArgumentOutOfRangeException>(() => bank.Advance(0));
    }

    [Fact]
    public void ReclamationUsesOldestOccurrenceThenAdmissionOrderAndDoesNotCatchUpUnboundedly() {
        var bank = Bank(Policy() with { ExpiredReceiptsPerTick = 1 }); bank.Advance(100);
        bank.Observe(Evidence(0, tick: 90)); bank.Observe(Evidence(1, tick: 0)); bank.Observe(Evidence(2, tick: 0));
        bank.Advance(1000000); Assert.Equal(1, bank.ReclaimedReceipts); Assert.Equal(2, bank.ReceiptCount);
        Assert.DoesNotContain(bank.Capture().Receipts, row => row.Event.Sequence == 1);
        bank.Advance(1000000); Assert.Equal(2, bank.ReceiptCount);
        bank.Advance(1000001); Assert.Single(bank.Capture().Receipts, row => row.Event.Sequence == 0);
    }

    [Fact]
    public void OriginalTimestampCannotBeRewrittenToMakeTheSameEventFresh() {
        var bank = Bank(); bank.Observe(Evidence()); bank.Advance(10);
        var before = Read(bank);
        Assert.Equal(WorldSocialEvidenceResult.Invalid, bank.Observe(Evidence(tick: 1)));
        Assert.Equal(WorldSocialEvidenceResult.Future, bank.Observe(Evidence(1, tick: 11)));
        Assert.Equal(before, Read(bank));
    }

    [Fact]
    public void InvalidInputsAndZeroWeightDoNotCreateMemoryOrReceipts() {
        var bank = Bank();
        var invalid = new[] {
            Evidence() with { Quality = One + 1 }, Evidence(value: One + 1),
            Evidence(key: Key(dimension: -1)), Evidence() with { Event = new(Person(1), "", 0) },
            Evidence() with { Event = new(Person(1), new string('x', 65), 0) },
            Evidence() with { Source = new("", 1, 1) },
            Evidence(key: Key() with { Subject = Person(-1) }),
        };
        foreach (var evidence in invalid) { Assert.Equal(WorldSocialEvidenceResult.Invalid, bank.Observe(evidence)); }
        Assert.Equal(WorldSocialEvidenceResult.ZeroWeight, bank.Observe(Evidence() with { Quality = 0 }));
        Assert.Equal(0, bank.ReceiptCount); Assert.Equal(0, bank.ImpressionCount);
    }

    [Fact]
    public void CheckpointRestoresFutureHashesIncludingExpiryBudgetsAndForgottenReceipts() {
        var bank = Bank(Policy() with { ExpiredReceiptsPerTick = 1 });
        bank.Observe(Evidence(source: 2)); bank.Observe(Evidence(1, -One));
        bank.Observe(Evidence(key: Key(observer: 5))); bank.Forget(Key(observer: 5));
        bank.Advance(100);
        var checkpoint = bank.Capture();
        var restored = WorldSocialMemory.Restore(bank.Policy, checkpoint);
        Assert.Equal(bank.StateHash, restored.StateHash);
        for (ulong tick = 101; tick < 120; tick++) {
            bank.Advance(tick); restored.Advance(tick);
            var evidence = Evidence(tick, tick: tick, source: tick % 2 == 0 ? 2 : null);
            Assert.Equal(bank.Observe(evidence), restored.Observe(evidence));
            Assert.Equal(Read(bank), Read(restored)); Assert.Equal(bank.StateHash, restored.StateHash);
        }
        foreach (var target in new ulong[] { 50401, 50402, 50403, ulong.MaxValue }) {
            bank.Advance(target); restored.Advance(target); Assert.Equal(bank.StateHash, restored.StateHash);
        }
        Assert.Equal(checkpoint.Impressions, WorldSocialMemory.Restore(bank.Policy, checkpoint).Capture().Impressions);
    }

    [Fact]
    public void CheckpointOrderAndDictionaryPackingDoNotChangeFutureBehavior() {
        var bank = Bank();
        for (var i = 0; i < 32; i++) { bank.Observe(Evidence((ulong)i, key: Key(observer: i))); }
        var checkpoint = bank.Capture();
        var shuffled = checkpoint with { Impressions = checkpoint.Impressions.Reverse().ToArray(), Receipts = checkpoint.Receipts.Reverse().ToArray() };
        var restored = WorldSocialMemory.Restore(bank.Policy, shuffled);
        Assert.Equal(bank.StateHash, restored.StateHash);
        for (ulong tick = 50401; tick < 50405; tick++) {
            bank.Advance(tick); restored.Advance(tick); Assert.Equal(bank.StateHash, restored.StateHash);
        }
    }

    [Fact]
    public void InvalidCheckpointRefusalsAreAtomicAndCheckFutureAffectingFields() {
        var bank = Bank(); bank.Observe(Evidence(source: 2));
        var s = bank.Capture(); var impression = s.Impressions[0]; var receipt = s.Receipts[0]; var hash = bank.StateHash;
        var invalid = new[] {
            s with { PolicyIdentity = "wrong" }, s with { EvidenceAttempts = -1 }, s with { ReclaimedReceipts = 1 },
            s with { Impressions = null! }, s with { Receipts = null! },
            s with { Impressions = [impression, impression] }, s with { Receipts = [receipt, receipt] },
            s with { Impressions = [impression with { Value = long.MaxValue }] },
            s with { Impressions = [impression with { UpdatedAt = 1 }] },
            s with { Impressions = [impression with { Weight = -1 }] },
            s with { Impressions = [impression with { IndependentEvents = 0 }] },
            s with { Impressions = [impression with { FirstReceiptOrdinal = s.NextOrdinal }] },
            s with { Receipts = [receipt with { Ordinal = s.NextOrdinal }] },
            s with { Receipts = [receipt with { OriginalSource = null }] },
            s with { Receipts = [receipt with { Weight = long.MaxValue }] },
            s with { Receipts = [receipt with { Value = -receipt.Value }] },
            s with { Receipts = [receipt with { LocalOccurredAt = 1 }] },
        };
        foreach (var candidate in invalid) {
            Assert.Throws<ArgumentException>(() => WorldSocialMemory.Restore(bank.Policy, candidate)); Assert.Equal(hash, bank.StateHash);
        }
    }

    [Fact]
    public void ReceiptOrdinalExhaustionRefusesRatherThanWrappingAndReusingAnIdentity() {
        var bank = Bank(); var saved = bank.Capture() with { NextOrdinal = ulong.MaxValue };
        bank = WorldSocialMemory.Restore(bank.Policy, saved);
        Assert.Equal(WorldSocialEvidenceResult.SequenceExhausted, bank.Observe(Evidence()));
        Assert.Equal(0, bank.ImpressionCount); Assert.Equal(0, bank.ReceiptCount);
    }

    [Fact]
    public void NewDirectEvidenceCanBeStrongWithoutChangingTheAccumulatedWeightCeiling() {
        var policy = Policy() with { DirectWeight = 1024 };
        policy = policy with { Dimensions = [policy.Dimensions[0] with { WeightCapacity = 1, MaximumChange = 2 }, policy.Dimensions[1]] };
        var bank = Bank(policy); bank.Observe(Evidence());
        Assert.InRange(Read(bank).Value, One * 99 / 100, One);
        Assert.Equal(One, Read(bank).Weight);
        Assert.Equal(bank.StateHash, WorldSocialMemory.Restore(bank.Policy, bank.Capture()).StateHash);
    }

    [Fact]
    public void FixedPointLearningMatchesAnIndependentWidenedIntegerOracleAtTheNumericExtremes() {
        var policy = Policy() with { DirectWeight = 1024, ReportWeight = 1024, UnfamiliarReliability = 1 };
        var d = policy.Dimensions[0] with {
            Baseline = -1, PriorWeight = 0.0000152587890625m, WeightCapacity = 65536,
            MaximumChange = 2, ConflictGain = 0, ConsistencyGain = 0, FollowUpBoost = 16,
        };
        policy = policy with { Dimensions = [d, policy.Dimensions[1]] };
        var bank = Bank(policy); var expected = -One; var mass = 0L;
        for (ulong i = 0; i < 80; i++) {
            var target = (i & 1) == 0 ? One : -One;
            var weight = 1024L * One;
            var fraction = (long)((System.Numerics.BigInteger)weight * One / (1 + mass + weight));
            expected += (long)((System.Numerics.BigInteger)(target - expected) * fraction / One);
            mass = Math.Min(65536L * One, mass + weight);
            Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence(i, target)));
            Assert.Equal(expected, Read(bank).Value); Assert.Equal(mass, Read(bank).Weight);
        }
    }

    [Fact]
    public void ComponentReadbackExposesExplicitStorageAndWorkWithoutMutatingIt() {
        var bank = Bank(); bank.Observe(Evidence()); var saved = bank.Capture(); var hash = bank.StateHash;
        Assert.Equal(1, saved.EvidenceAttempts); Assert.Equal(1UL, saved.NextOrdinal);
        Assert.Single(saved.Impressions); Assert.Single(saved.Receipts);
        Assert.Equal(hash, bank.StateHash);
    }

    [Fact]
    public void ObserverCaptureIncludesForgottenReceiptsButNoOtherIncarnationsOrReverseRelationships() {
        var bank = Bank();
        var ownKeys = new[] { Key(subject: 4), Key(subject: 3, dimension: 1), Key(subject: 3) };
        foreach (var key in ownKeys) { Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence(key: key))); }
        var forgotten = Key(subject: 9);
        bank.Observe(Evidence(key: forgotten, source: 2)); bank.Forget(forgotten);
        var otherKeys = new[] { Key(observer: 4, subject: 0), Key() with { Observer = Person(0, 2) },
            Key() with { Observer = Person(0, authority: "elsewhere") } };
        foreach (var key in otherKeys) { bank.Observe(Evidence(key: key)); }
        bank.Advance(100);
        var hash = bank.StateHash;
        var selected = bank.CaptureObserver(Person(0));
        Assert.Equal(3, selected.Impressions.Count); Assert.Equal(4, selected.Receipts.Count);
        Assert.All(selected.Impressions, row => Assert.Equal(Person(0), row.Key.Observer));
        Assert.All(selected.Receipts, row => Assert.Equal(Person(0), row.Impression.Observer));
        Assert.Equal(new[] { ownKeys[2], ownKeys[1], ownKeys[0] }, selected.Impressions.Select(row => row.Key));
        Assert.Contains(selected.Receipts, row => row.Impression == forgotten);
        Assert.Equal(bank.EngineTick, selected.EngineTick); Assert.Equal(bank.Policy.Identity, selected.PolicyIdentity);
        Assert.Equal(0, selected.EvidenceAttempts); Assert.Equal(0, selected.ReclaimedReceipts);
        var restored = WorldSocialMemory.Restore(bank.Policy, selected);
        foreach (var key in ownKeys) { Assert.Equal(Read(bank, key), Read(restored, key)); }
        foreach (var key in otherKeys) { Assert.False(Read(restored, key).Known); }
        Assert.Equal(WorldSocialEvidenceResult.Duplicate, restored.Observe(Evidence(key: forgotten)));
        // Public checkpoint collections are detached even when a caller mutates their concrete arrays.
        ((WorldSocialImpressionCheckpoint[])selected.Impressions)[0] = default;
        ((WorldSocialReceiptCheckpoint[])selected.Receipts)[0] = default;
        Assert.Equal(hash, bank.StateHash);
        foreach (var key in ownKeys) { Assert.Equal(Read(bank, key), Read(restored, key)); }
    }

    [Fact]
    public void UnknownObserverCaptureIsEmptyAndInvalidAddressesCannotMutateTheSource() {
        var bank = Bank(); bank.Observe(Evidence()); var hash = bank.StateHash;
        var empty = bank.CaptureObserver(Person(99));
        Assert.Empty(empty.Impressions); Assert.Empty(empty.Receipts); Assert.Equal(0UL, empty.NextOrdinal);
        Assert.Equal(0, WorldSocialMemory.Restore(bank.Policy, empty).ImpressionCount);
        foreach (var invalid in new[] { default(WorldEntityAddress), Person(-1), Person(0, -1),
            Person(0, authority: " "), Person(0, authority: new string('x', 513)) }) {
            Assert.Throws<ArgumentException>(() => bank.CaptureObserver(invalid));
        }
        Assert.Equal(hash, bank.StateHash);
    }

    [Fact]
    public void ObserverCaptureCompactsUnrelatedOrdinalsWithoutRevivingForgottenEvidence() {
        var solo = Bank(); var crowded = Bank();
        void Feed(WorldSocialEvidence evidence) {
            Assert.Equal(WorldSocialEvidenceResult.Accepted, solo.Observe(evidence));
            for (var other = 2; other < 10; other++) {
                Assert.Equal(WorldSocialEvidenceResult.Accepted, crowded.Observe(Evidence(evidence.Event.Sequence, key: Key(observer: other))));
            }
            Assert.Equal(WorldSocialEvidenceResult.Accepted, crowded.Observe(evidence));
        }
        Feed(Evidence(0, source: 2));
        Assert.True(solo.Forget(Key())); Assert.True(crowded.Forget(Key()));
        Feed(Evidence(1, -One, source: 3));
        Feed(Evidence(2, key: Key(subject: 4)));
        solo.Advance(100); crowded.Advance(100);
        var selected = crowded.CaptureObserver(Person(0));
        AssertCheckpointEqual(solo.CaptureObserver(Person(0)), selected);
        var restored = WorldSocialMemory.Restore(crowded.Policy, selected);
        Assert.Equal(WorldSocialEvidenceResult.Duplicate, restored.Observe(Evidence(0)));
        Assert.Equal(WorldSocialEvidenceResult.Upgraded, restored.Observe(Evidence(1)));
        Assert.Equal(WorldSocialEvidenceResult.Duplicate, restored.Observe(Evidence(1)));
        Assert.Equal(1UL, Read(restored).IndependentEvents);
        Assert.Equal(WorldSocialEvidenceResult.Accepted, restored.Observe(Evidence(3, tick: 100)));
        Assert.Equal(2UL, Read(restored).IndependentEvents);
        // Repeated capture/restore must not keep expanding the ordinal namespace.
        for (var round = 0; round < 32; round++) {
            var next = WorldSocialMemory.Restore(crowded.Policy, selected).CaptureObserver(Person(0));
            AssertCheckpointEqual(selected, next); selected = next;
        }
    }

    [Fact]
    public void ObserverCapturePreservesAgedImpressionsWhenAllTheirReceiptsHaveExpired() {
        var policy = Policy();
        policy = policy with { Dimensions = [policy.Dimensions[0] with { RecoverySeconds = 4, ConfidenceDecaySeconds = 4 }, policy.Dimensions[1]] };
        var bank = Bank(policy);
        for (ulong i = 0; i < 12; i++) { bank.Observe(Evidence(i)); }
        bank.Advance(50401);
        Assert.Equal(0, bank.ReceiptCount);
        var selected = bank.CaptureObserver(Person(0));
        Assert.Equal(12UL, selected.NextOrdinal);
        var restored = WorldSocialMemory.Restore(bank.Policy, selected);
        Assert.Equal(Read(bank), Read(restored));
        foreach (var tick in new ulong[] { 60000, 90000, 201600 }) {
            bank.Advance(tick); restored.Advance(tick);
            Assert.Equal(Read(bank), Read(restored));
        }
        var fresh = Evidence(99, -One, tick: bank.EngineTick);
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(fresh));
        Assert.Equal(WorldSocialEvidenceResult.Accepted, restored.Observe(fresh));
        Assert.Equal(Read(bank), Read(restored));
    }

    [Fact]
    public void ObserverCaptureRetainsExpiredReceiptsAwaitingBoundedReclamation() {
        var bank = Bank(Policy() with { ExpiredReceiptsPerTick = 1 });
        bank.Observe(Evidence(key: Key(observer: 2)));
        for (ulong i = 0; i < 3; i++) { bank.Observe(Evidence(i)); }
        bank.Advance(50401);
        var selected = bank.CaptureObserver(Person(0));
        Assert.Equal(3, selected.Receipts.Count);
        var restored = WorldSocialMemory.Restore(bank.Policy, selected);
        for (ulong tick = 50402; tick <= 50404; tick++) {
            bank.Advance(tick); restored.Advance(tick);
            AssertCheckpointEqual(bank.CaptureObserver(Person(0)), restored.CaptureObserver(Person(0)));
        }
        Assert.Equal(WorldSocialEvidenceResult.Stale, restored.Observe(Evidence()));
    }

    [Fact]
    public void ObserverCaptureCompactsNearExhaustedOrdinalsWithoutOverflowOrChangingEventIdentity() {
        var bank = Bank(); bank.Observe(Evidence(0, source: 2)); bank.Observe(Evidence(1, source: 2));
        var original = bank.Capture();
        const ulong shift = ulong.MaxValue - 2;
        var exhausted = original with {
            NextOrdinal = ulong.MaxValue,
            Impressions = original.Impressions.Select(row => row with { FirstReceiptOrdinal = row.FirstReceiptOrdinal + shift }).ToArray(),
            Receipts = original.Receipts.Select(row => row with { Ordinal = row.Ordinal + shift }).ToArray(),
        };
        var full = WorldSocialMemory.Restore(bank.Policy, exhausted);
        Assert.Equal(WorldSocialEvidenceResult.SequenceExhausted, full.Observe(Evidence(2)));
        var selected = full.CaptureObserver(Person(0));
        AssertCheckpointEqual(bank.CaptureObserver(Person(0)), selected);
        var restored = WorldSocialMemory.Restore(bank.Policy, selected);
        Assert.Equal(WorldSocialEvidenceResult.Upgraded, restored.Observe(Evidence(1)));
        Assert.Equal(WorldSocialEvidenceResult.Duplicate, restored.Observe(Evidence(1)));
        Assert.Equal(WorldSocialEvidenceResult.Accepted, restored.Observe(Evidence(2)));
        Assert.Equal(3UL, Read(restored).IndependentEvents);
    }

    [Fact]
    public void ObserverIndexSurvivesHeadMiddleTailRemovalReuseAndShuffledRestore() {
        var bank = Bank(Policy(impressions: 12, receipts: 12) with { ExpiredReceiptsPerTick = 12 });
        for (var round = 0; round < 32; round++) {
            bank.Advance((ulong)(round + 1) * 50401);
            for (var subject = 0; subject < 4; subject++) {
                for (var observer = 0; observer < 3; observer++) {
                    Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence((ulong)round,
                        key: Key(observer, subject + 10), tick: bank.EngineTick, source: 99)));
                    Assert.Equal(WorldSocialEvidenceResult.Conflict, bank.Observe(Evidence((ulong)round, -One,
                        key: Key(observer, subject + 10), tick: bank.EngineTick, source: 98)));
                    Assert.Equal(WorldSocialEvidenceResult.Upgraded, bank.Observe(Evidence((ulong)round,
                        key: Key(observer, subject + 10), tick: bank.EngineTick)));
                }
            }
            foreach (var subject in new[] { 12, 13, 10, 11 }) {
                for (var observer = 0; observer < 3; observer++) {
                    Assert.True(bank.Forget(Key(observer, subject)));
                    var selected = bank.CaptureObserver(Person(observer));
                    Assert.DoesNotContain(selected.Impressions, row => row.Key.Subject == Person(subject));
                    Assert.Equal(4, selected.Receipts.Count);
                    Assert.All(selected.Impressions, row => Assert.Equal(Person(observer), row.Key.Observer));
                }
            }
            Assert.Equal(0, bank.ImpressionCount);
            var full = bank.Capture();
            bank = WorldSocialMemory.Restore(bank.Policy, full with { Receipts = full.Receipts.Reverse().ToArray() });
            for (var observer = 0; observer < 3; observer++) {
                Assert.Equal(WorldSocialEvidenceResult.Duplicate, bank.Observe(Evidence((ulong)round,
                    key: Key(observer, 10), tick: bank.EngineTick)));
            }
        }
        bank.Advance(33UL * 50401);
        Assert.Equal(0, bank.ReceiptCount);
        for (var observer = 0; observer < 3; observer++) { Assert.Empty(bank.CaptureObserver(Person(observer)).Receipts); }
    }

    [Fact]
    public void DisjointImpressionAndReceiptOwnersUseReservedStorageWithoutAllocating() {
        const int capacity = 128;
        var bank = Bank(Policy(capacity, capacity) with { ExpiredReceiptsPerTick = capacity });
        var events = Enumerable.Range(0, capacity * 2).Select(i => Evidence(key: Key(observer: i))).ToArray();
        void Populate(ulong tick) {
            bank.Advance(tick);
            for (var i = 0; i < capacity - 1; i++) {
                bank.Forget(events[i].Impression);
                if (bank.Observe(events[i] with { OccurredAt = tick }) != WorldSocialEvidenceResult.Accepted) { throw new InvalidOperationException(); }
            }
            bank.Advance(tick + 50401); // first population keeps impressions but loses receipts
            for (var i = capacity; i < events.Length; i++) {
                var evidence = events[i] with { OccurredAt = bank.EngineTick };
                if (bank.Observe(evidence) != WorldSocialEvidenceResult.Accepted || !bank.Forget(evidence.Impression)) { throw new InvalidOperationException(); }
            }
        }
        // Keep one free impression slot for transient admissions. Each one leaves a receipt-only owner behind;
        // the directory must hold both populations, not just the impression capacity.
        Populate(1);
        Assert.Equal(capacity - 1, bank.ImpressionCount); Assert.Equal(capacity, bank.ReceiptCount);
        var restored = WorldSocialMemory.Restore(bank.Policy, bank.Capture());
        Assert.Equal(bank.StateHash, restored.StateHash);
        for (var i = 0; i < capacity - 1; i++) {
            var selected = restored.CaptureObserver(Person(i)); Assert.Single(selected.Impressions); Assert.Empty(selected.Receipts);
        }
        for (var i = capacity; i < events.Length; i++) {
            var selected = restored.CaptureObserver(Person(i)); Assert.Empty(selected.Impressions); Assert.Single(selected.Receipts);
        }
        Populate(bank.EngineTick + 50401); // warm every path before allocation measurement
        var start = GC.GetAllocatedBytesForCurrentThread();
        Populate(bank.EngineTick + 50401);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Dense4096ObserverCapturePreservesEveryLocalLedgerWithoutScanningOtherOwners() {
        const int observers = 4096;
        const int neighbors = 16;
        var bank = Bank(Policy(observers * neighbors, observers * neighbors) with {
            ImpressionsPerObserver = neighbors, EvidenceAttemptsPerTick = observers,
        });
        for (var neighbor = 1; neighbor <= neighbors; neighbor++) {
            bank.Advance((ulong)neighbor);
            for (var observer = 0; observer < observers; observer++) {
                Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence((ulong)neighbor,
                    key: Key(observer, (observer + neighbor) % observers), tick: bank.EngineTick)));
            }
        }
        var hash = bank.StateHash;
        var captures = new WorldSocialMemoryCheckpoint[observers];
        for (var i = 0; i < 128; i++) { _ = bank.CaptureObserver(Person(i), 0); }
        var startBytes = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (var observer = 0; observer < observers; observer++) { captures[observer] = bank.CaptureObserver(Person(observer), 0); }
        var elapsed = Stopwatch.GetElapsedTime(start);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - startBytes;
        Assert.Equal(hash, bank.StateHash);
        for (var observer = 0; observer < observers; observer++) {
            var selected = captures[observer];
            Assert.Equal(0UL, selected.EngineTick);
            Assert.Equal(neighbors, selected.Impressions.Count); Assert.Equal(neighbors, selected.Receipts.Count);
            Assert.All(selected.Impressions, row => Assert.Equal(Person(observer), row.Key.Observer));
            Assert.All(selected.Receipts, row => Assert.Equal(Person(observer), row.Impression.Observer));
            Assert.Equal((ulong)neighbors, selected.NextOrdinal);
        }
        foreach (var observer in new[] { 0, 1, 1023, 2048, 4095 }) {
            var restored = WorldSocialMemory.Restore(bank.Policy, captures[observer]);
            for (var neighbor = 1; neighbor <= neighbors; neighbor++) {
                var key = Key(observer, (observer + neighbor) % observers);
                Assert.Equal(Read(bank, key), Read(restored, key));
                Assert.Equal(WorldSocialEvidenceResult.Duplicate, restored.Observe(Evidence((ulong)neighbor, key: key, tick: (ulong)neighbor)));
            }
        }
        output.WriteLine($"social observer capture: {observers} owners x {neighbors} impressions/receipts rebased to tick zero; {elapsed.TotalMilliseconds:F3}ms; {allocated} detached-checkpoint bytes; source hash={hash:X16}");
    }

    [Fact]
    public void OwnershipRemovalPreservesOtherObserversAndWorkStateIncludingForgottenReceipts() {
        var bank = Bank(Policy() with { ExpiredReceiptsPerTick = 1 });
        var keys = new[] {
            Key(), Key(subject: 2), Key(observer: 1, subject: 0),
            Key() with { Observer = Person(0, 2) }, Key() with { Observer = Person(0, authority: "other") },
        };
        foreach (var key in keys) { Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence(key: key))); }
        bank.Advance(50401); // One receipt reclaimed; the rest must remain removable even though expired.
        Assert.True(bank.Forget(keys[1]));
        var retained = bank.CaptureObserver(Person(0));
        Assert.Single(retained.Impressions); Assert.Single(retained.Receipts);
        var before = bank.Capture();
        var expected = before with {
            Impressions = before.Impressions.Where(row => row.Key.Observer != Person(0)).ToArray(),
            Receipts = before.Receipts.Where(row => row.Impression.Observer != Person(0)).ToArray(),
        };
        Assert.True(bank.RemoveObserver(Person(0)));
        AssertCheckpointEqual(expected, bank.Capture());
        Assert.Equal(WorldSocialMemory.Restore(bank.Policy, expected).StateHash, bank.StateHash);
        Assert.Single(retained.Impressions); Assert.Single(retained.Receipts); // Detached copies survive retirement.
        Assert.False(Read(bank).Known);
        foreach (var key in keys.Skip(2)) { Assert.True(Read(bank, key).Known); }
        var hash = bank.StateHash;
        Assert.False(bank.RemoveObserver(Person(0))); Assert.False(bank.RemoveObserver(Person(999)));
        Assert.Throws<ArgumentException>(() => bank.RemoveObserver(new("", 0, 1)));
        Assert.Throws<ArgumentException>(() => bank.RemoveObserver(Person(-1)));
        Assert.Equal(hash, bank.StateHash);
        // Receipt-only and impression-only owners also retire; no stale expiry entry can later name a recycled node.
        Assert.True(bank.Forget(keys[2])); Assert.True(bank.RemoveObserver(keys[2].Observer));
        bank.Advance(100000); bank.Advance(100001);
        foreach (var key in keys.Skip(3)) { Assert.True(bank.RemoveObserver(key.Observer)); }
        Assert.Equal(0, bank.ImpressionCount); Assert.Equal(0, bank.ReceiptCount);
        bank.Advance(100002); Assert.Equal(0, bank.ReclaimedReceipts);
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence(tick: bank.EngineTick)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexedExpiryMatchesSortedOracleThroughArbitraryRetirementAndRestore(bool reverseRestoreOrder) {
        const int count = 128;
        var bank = Bank(Policy(impressions: count, receipts: count) with { ExpiredReceiptsPerTick = 1 });
        for (var index = 0; index < count; index++) {
            Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence((ulong)index, key: Key(index, count))));
        }
        var source = bank.Capture();
        var offset = ulong.MaxValue - count;
        var expected = source with {
            NextOrdinal = ulong.MaxValue,
            Impressions = source.Impressions.Select(row => row with { FirstReceiptOrdinal = row.FirstReceiptOrdinal + offset }).ToArray(),
            Receipts = source.Receipts.Select((row, index) => row with {
                Ordinal = row.Ordinal + offset,
                LocalOccurredAt = index % 11 == 0 ? Int128.MinValue : -((index * 73) % 19),
            }).ToArray(),
        };
        bank = WorldSocialMemory.Restore(bank.Policy, reverseRestoreOrder
            ? expected with { Receipts = expected.Receipts.Reverse().ToArray() } : expected);
        AssertCheckpointEqual(expected, bank.Capture());
        for (var step = 0; step < count; step++) {
            var observer = Person((step * 53) % count);
            Assert.True(bank.RemoveObserver(observer));
            expected = expected with {
                Impressions = expected.Impressions.Where(row => row.Key.Observer != observer).ToArray(),
                Receipts = expected.Receipts.Where(row => row.Impression.Observer != observer).ToArray(),
            };
            var oldest = expected.Receipts.OrderBy(row => row.LocalOccurredAt).ThenBy(row => row.Ordinal).FirstOrDefault();
            var hadReceipts = expected.Receipts.Count != 0;
            expected = expected with {
                EngineTick = (ulong)(100000 + step), EvidenceAttempts = 0, ReclaimedReceipts = hadReceipts ? 1 : 0,
                Receipts = hadReceipts ? expected.Receipts.Where(row => row.Ordinal != oldest.Ordinal).ToArray() : expected.Receipts,
            };
            bank.Advance(expected.EngineTick);
            AssertCheckpointEqual(expected, bank.Capture());
            Assert.Equal(WorldSocialMemory.Restore(bank.Policy, expected).StateHash, bank.StateHash);
            if (step % 17 == 0) { bank = WorldSocialMemory.Restore(bank.Policy, bank.Capture()); }
        }
        Assert.Equal(0, bank.ImpressionCount); Assert.Equal(0, bank.ReceiptCount);
        Assert.Equal(WorldSocialEvidenceResult.SequenceExhausted, bank.Observe(Evidence(tick: bank.EngineTick)));
    }

    [Fact]
    public void IndexedExpiryRemovalRepairsBothDirectionsBeforeOrderedReclamation() {
        // This is already a valid four-way min heap. Removing index 5 moves 21 from the tail under 50:
        // repairing only downward hides 21 behind 50 and expires 30 first. Random removal can mask that bug.
        ulong[] times = [0, 50, 10, 20, 30, 51, 52, 53, 54, 11, 12, 13, 14, 21];
        for (var removed = 0; removed < times.Length; removed++) {
            var bank = Bank(Policy() with { ExpiredReceiptsPerTick = 1 }); bank.Advance(100);
            for (var index = 0; index < times.Length; index++) {
                Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence((ulong)index,
                    key: Key(index, times.Length), tick: times[index])));
            }
            Assert.True(bank.RemoveObserver(Person(removed)));
            var remaining = bank.Capture().Receipts.OrderBy(row => row.LocalOccurredAt).ThenBy(row => row.Ordinal).ToArray();
            for (var step = 0; step < remaining.Length; step++) {
                bank.Advance((ulong)(100000 + step));
                var actual = bank.Capture();
                Assert.Equal(1, bank.ReclaimedReceipts);
                Assert.Equal(remaining.Skip(step + 1).OrderBy(row => row.Ordinal), actual.Receipts);
            }
        }
    }

    [Fact]
    public void Dense4096ObserverOwnershipChurnReusesAllStorageWithoutAllocationOrExpiryTombstones() {
        const int observers = 4096;
        const int neighbors = 16;
        var policy = Policy(impressions: observers * neighbors, receipts: observers * neighbors) with {
            ImpressionsPerObserver = neighbors, EvidenceAttemptsPerTick = observers * neighbors,
        };
        var bank = Bank(policy);
        var events = new WorldSocialEvidence[observers * neighbors];
        for (var observer = 0; observer < observers; observer++) {
            for (var neighbor = 0; neighbor < neighbors; neighbor++) {
                events[(observer * neighbors) + neighbor] = Evidence((ulong)neighbor,
                    key: Key(observer, (observer + neighbor + 1) % observers));
            }
        }
        void ReplaceOwners(ulong tick) {
            bank.Advance(tick);
            for (var observer = 0; observer < observers; observer++) {
                bank.RemoveObserver(Person(observer));
                for (var neighbor = 0; neighbor < neighbors; neighbor++) {
                    var evidence = events[(observer * neighbors) + neighbor] with { OccurredAt = tick };
                    if (bank.Observe(evidence) != WorldSocialEvidenceResult.Accepted) {
                        throw new InvalidOperationException("ownership churn failed to release all reserved storage");
                    }
                }
            }
            _ = bank.StateHash;
        }
        for (ulong tick = 1; tick <= 4; tick++) { ReplaceOwners(tick); }
        var bytes = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
        for (ulong tick = 5; tick <= 8; tick++) { ReplaceOwners(tick); }
        var elapsed = Stopwatch.GetElapsedTime(start); var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Assert.Equal(0, allocated);
        Assert.Equal(events.Length, bank.ImpressionCount); Assert.Equal(events.Length, bank.ReceiptCount);
        var checkpoint = bank.Capture();
        Assert.All(checkpoint.Impressions, row => Assert.Equal(1UL, row.IndependentEvents));
        Assert.All(checkpoint.Receipts, row => Assert.Equal(8UL, row.OccurredAt));
        Assert.Equal(WorldSocialMemory.Restore(bank.Policy, checkpoint).StateHash, bank.StateHash);
        for (var observer = 0; observer < observers; observer++) { Assert.True(bank.RemoveObserver(Person(observer))); }
        bank.Advance(1000000);
        Assert.Equal(0, bank.ImpressionCount); Assert.Equal(0, bank.ReceiptCount); Assert.Equal(0, bank.ReclaimedReceipts);
        output.WriteLine($"social ownership churn: {observers} owners x {neighbors} impressions/receipts x 4 replacements; {elapsed.TotalMilliseconds:F3}ms; {allocated} steady-state bytes");
    }

    private static void AssertCheckpointEqual(WorldSocialMemoryCheckpoint expected, WorldSocialMemoryCheckpoint actual) {
        Assert.Equal(expected.PolicyIdentity, actual.PolicyIdentity); Assert.Equal(expected.EngineTick, actual.EngineTick);
        Assert.Equal(expected.EvidenceAttempts, actual.EvidenceAttempts); Assert.Equal(expected.ReclaimedReceipts, actual.ReclaimedReceipts);
        Assert.Equal(expected.NextOrdinal, actual.NextOrdinal); Assert.Equal(expected.Impressions, actual.Impressions);
        Assert.Equal(expected.Receipts, actual.Receipts);
    }

    [Theory]
    [InlineData(100000UL, 0UL)]
    [InlineData(0UL, 100000UL)]
    [InlineData(ulong.MaxValue - 60000, 0UL)]
    [InlineData(100000UL, ulong.MaxValue - 60000)]
    public void RebasingPreservesAgesDecayAndDuplicateSemanticsAcrossIndependentClockOrigins(ulong sourceStart, ulong destinationStart) {
        var policy = Policy();
        policy = policy with { Dimensions = [policy.Dimensions[0] with { RecoverySeconds = 2, ConfidenceDecaySeconds = 2 }, policy.Dimensions[1]] };
        var source = Bank(policy); source.Advance(sourceStart);
        var report = Evidence(source: 2, tick: sourceStart);
        Assert.Equal(WorldSocialEvidenceResult.Accepted, source.Observe(report));
        source.Advance(sourceStart + 100);
        var hash = source.StateHash;
        var carried = source.CaptureObserver(Person(0), destinationStart);
        Assert.Equal(destinationStart, carried.EngineTick);
        Assert.Equal(sourceStart, Assert.Single(carried.Receipts).OccurredAt);
        var destination = WorldSocialMemory.Restore(source.Policy, carried);
        Assert.Equal(Read(source), Read(destination));
        Assert.Equal(hash, source.StateHash);
        Assert.Equal(WorldSocialEvidenceResult.Duplicate, destination.Observe(report));
        // The original timestamp can be far in the destination's future: a known event still upgrades once.
        Assert.Equal(WorldSocialEvidenceResult.Upgraded, source.Observe(report with { Source = null }));
        Assert.Equal(WorldSocialEvidenceResult.Upgraded, destination.Observe(report with { Source = null }));
        Assert.Equal(Read(source), Read(destination));
        var sourceClock = source.EngineTick;
        foreach (var elapsed in new ulong[] { 1, 1000, 50299, 50300 }) {
            source.Advance(sourceClock + elapsed); destination.Advance(destinationStart + elapsed);
            Assert.Equal(Read(source), Read(destination));
            Assert.Equal(WorldSocialEvidenceResult.Duplicate, destination.Observe(report));
        }
        source.Advance(sourceClock + 50301); destination.Advance(destinationStart + 50301);
        Assert.Equal(0, source.ReceiptCount); Assert.Equal(0, destination.ReceiptCount);
        Assert.Equal(Read(source), Read(destination));
        // Once the receipt is gone, a relay must still supply the actual projected occurrence, not arrival time.
        Assert.Equal(WorldSocialEvidenceResult.Stale, destination.Observe(report with { LocalOccurredAt = (Int128)destinationStart - 100 }));
    }

    [Fact]
    public void RepeatedClockRebasesDoNotResetAgeOrMakeForgottenRumorsEffectiveAgain() {
        var bank = Bank(); bank.Advance(100000);
        var report = Evidence(source: 2, tick: bank.EngineTick);
        bank.Observe(report); bank.Advance(bank.EngineTick + 10);
        for (var crossing = 0; crossing < 128; crossing++) {
            var target = crossing % 3 == 0 ? 0UL : crossing % 3 == 1 ? ulong.MaxValue - 1000 : 321UL;
            bank = WorldSocialMemory.Restore(bank.Policy, bank.CaptureObserver(Person(0), target));
            Assert.Equal((ulong)(10 + crossing), Read(bank).AgeTicks);
            Assert.Equal(report.OccurredAt, Assert.Single(bank.Capture().Receipts).OccurredAt);
            Assert.Equal(WorldSocialEvidenceResult.Duplicate, bank.Observe(report));
            bank.Advance(target + 1);
        }
        Assert.True(bank.Forget(Key()));
        bank = WorldSocialMemory.Restore(bank.Policy, bank.CaptureObserver(Person(0), 0));
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence(1, -One, source: 3)));
        var relearned = Read(bank);
        Assert.Equal(WorldSocialEvidenceResult.Duplicate, bank.Observe(report with { Source = null }));
        Assert.Equal(relearned, Read(bank));
    }

    [Fact]
    public void ImportedEventProjectionPreservesOriginalTimeAndCannotRefreshRetainedEvidence() {
        var bank = Bank(Policy() with { ExpiredReceiptsPerTick = 1 });
        var earlier = Evidence(0, tick: 100000, source: 2) with { LocalOccurredAt = -1 };
        var report = Evidence(1, tick: 200000, source: 2) with { LocalOccurredAt = 0 };
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(earlier));
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(report));
        Assert.Equal(WorldSocialEvidenceResult.Future, bank.Observe(Evidence(2) with { LocalOccurredAt = 1 }));
        Assert.Equal(WorldSocialEvidenceResult.Stale, bank.Observe(Evidence(2) with { LocalOccurredAt = Int128.MinValue }));
        bank.Advance(50401);
        Assert.Single(bank.Capture().Receipts); // the second expired receipt is still awaiting its work allowance
        Assert.Equal(WorldSocialEvidenceResult.Stale, bank.Observe(report with { LocalOccurredAt = bank.EngineTick, Source = null }));
        Assert.Equal(WorldSocialEvidenceResult.Invalid, bank.Observe(report with { OccurredAt = report.OccurredAt + 1 }));
        bank.Advance(50402);
        Assert.Empty(bank.Capture().Receipts);
        Assert.Equal(WorldSocialEvidenceResult.Stale, bank.Observe(report));
    }

    [Fact]
    public void ExtremeImportedAgesSaturateReadbackAndDecayWithoutOverflowAndFailedRebaseIsReadOnly() {
        var policy = Policy();
        policy = policy with { Dimensions = [policy.Dimensions[0] with { RecoverySeconds = 1, ConfidenceDecaySeconds = 1 }, policy.Dimensions[1]] };
        var bank = Bank(policy); bank.Observe(Evidence());
        var saved = bank.Capture();
        saved = saved with {
            Impressions = [saved.Impressions[0] with { UpdatedAt = Int128.MinValue }],
            Receipts = [saved.Receipts[0] with { LocalOccurredAt = Int128.MinValue }],
        };
        bank = WorldSocialMemory.Restore(bank.Policy, saved);
        Assert.Equal(ulong.MaxValue, Read(bank).AgeTicks);
        Assert.Equal(0, Read(bank).Value); Assert.Equal(0, Read(bank).Weight);
        var farClock = WorldSocialMemory.Restore(bank.Policy, bank.CaptureObserver(Person(0), ulong.MaxValue));
        Assert.Equal(Read(bank), Read(farClock));
        Assert.Equal(WorldSocialEvidenceResult.Stale, farClock.Observe(Evidence()));
        bank.Advance(1); Assert.Equal(0, bank.ReceiptCount);
        var hash = bank.StateHash;
        Assert.Throws<OverflowException>(() => bank.CaptureObserver(Person(0), 0));
        Assert.Equal(hash, bank.StateHash);
        Assert.Equal(Read(bank), Read(WorldSocialMemory.Restore(bank.Policy, bank.Capture())));
    }

    [Fact]
    public void ObserverImportPreservesClockAgesForgettingAndOtherOwnersAcrossDifferentStorageBudgets() {
        var source = Bank(); source.Advance(100000);
        var oldReport = Evidence(source: 2, tick: source.EngineTick);
        source.Observe(oldReport); source.Forget(Key()); source.Observe(Evidence(1, -One, tick: source.EngineTick));
        source.Observe(Evidence(2, key: Key(subject: 3), tick: source.EngineTick)); source.Forget(Key(subject: 3));
        source.Observe(Evidence(3, key: Key(observer: 99), tick: source.EngineTick));
        source.Advance(100010);
        var exported = source.CaptureObserver(Person(0)); var sourceHash = source.StateHash;
        var destination = Bank(Policy(impressions: 16, receipts: 16) with { EvidenceAttemptsPerTick = 1, ExpiredReceiptsPerTick = 1 });
        destination.Observe(Evidence(key: Key(observer: 5)));
        var resident = Read(destination, Key(observer: 5)); var before = destination.Capture();
        Assert.True(destination.TryImportObserver(Person(0), source.Policy, exported, out var reason), reason);
        Assert.Empty(reason); Assert.Equal(sourceHash, source.StateHash);
        Assert.Equal(before.EngineTick, destination.EngineTick); Assert.Equal(before.EvidenceAttempts, destination.EvidenceAttempts);
        Assert.Equal(before.ReclaimedReceipts, destination.ReclaimedReceipts); Assert.Equal(resident, Read(destination, Key(observer: 5)));
        Assert.Equal(Read(source), Read(destination)); Assert.False(Read(destination, Key(subject: 3)).Known);
        Assert.False(Read(destination, Key(observer: 99)).Known);
        Assert.Equal(exported.Impressions, destination.CaptureObserver(Person(0), source.EngineTick).Impressions);
        Assert.Equal(exported.Receipts, destination.CaptureObserver(Person(0), source.EngineTick).Receipts);
        var imported = destination.Capture();
        Assert.True(imported.Receipts.Skip(1).All(row => row.LocalOccurredAt == -10 && row.OccurredAt == 100000));
        Assert.Equal(WorldSocialEvidenceResult.WorkLimited, destination.Observe(oldReport)); // import does not replenish work
        destination.Advance(1);
        Assert.Equal(WorldSocialEvidenceResult.Duplicate, destination.Observe(oldReport with { Source = null }));
        destination.Advance(2);
        Assert.Equal(WorldSocialEvidenceResult.Duplicate, destination.Observe(Evidence(2, key: Key(subject: 3), tick: 100000)));
        var hash = destination.StateHash;
        Assert.False(destination.TryImportObserver(Person(0), source.Policy, exported, out reason));
        Assert.Contains("already owned", reason); Assert.Equal(hash, destination.StateHash);
        ((WorldSocialImpressionCheckpoint[])exported.Impressions)[0] = default;
        ((WorldSocialReceiptCheckpoint[])exported.Receipts)[0] = default;
        Assert.Equal(hash, destination.StateHash);
        Assert.Equal(hash, WorldSocialMemory.Restore(destination.Policy, destination.Capture()).StateHash);
    }

    [Fact]
    public void ImportRefusesAllMalformedForeignAndOverflowingRecordsWithoutAnyPartialChange() {
        var source = Bank(); source.Advance(1);
        source.Observe(Evidence(tick: 1)); source.Observe(Evidence(1, key: Key(subject: 2), tick: 1));
        var packet = source.CaptureObserver(Person(0));
        var first = packet.Impressions[0]; var last = packet.Impressions[1]; var receipt = packet.Receipts[0];
        var destination = Bank(); destination.Observe(Evidence(key: Key(observer: 9)));
        var hash = destination.StateHash;
        var invalid = new[] {
            packet with { PolicyIdentity = "wrong" }, packet with { EvidenceAttempts = -1 },
            packet with { Impressions = null! }, packet with { Receipts = null! },
            packet with { Impressions = [first, last with { Value = long.MaxValue }] },
            packet with { Impressions = [first, last with { Key = Key(observer: 7) }] },
            packet with { Receipts = [receipt, packet.Receipts[1] with { Impression = Key(observer: 7) }] },
            packet with { Receipts = [receipt, receipt] }, packet with { Impressions = [first, first] },
            packet with { Impressions = [first, last with { UpdatedAt = Int128.MinValue }] }, // destination rebase underflows
            packet with { Receipts = [receipt, packet.Receipts[1] with { LocalOccurredAt = Int128.MinValue }] },
            packet with { Impressions = [first with { IndependentEvents = ulong.MaxValue }], Receipts = [], NextOrdinal = ulong.MaxValue },
        };
        foreach (var candidate in invalid) {
            Assert.False(destination.TryImportObserver(Person(0), source.Policy, candidate, out var reason));
            Assert.NotEmpty(reason); Assert.Equal(hash, destination.StateHash);
        }
        Assert.False(destination.TryImportObserver(Person(-1), source.Policy, packet, out _));
        Assert.Throws<ArgumentNullException>(() => destination.TryImportObserver(Person(0), null!, packet, out _));
        Assert.Throws<ArgumentNullException>(() => destination.TryImportObserver(Person(0), source.Policy, null!, out _));
        Assert.Equal(hash, destination.StateHash);
        Assert.True(destination.TryImportObserver(Person(0), source.Policy, packet, out var accepted), accepted);
    }

    [Fact]
    public void ImportHonorsBothQuotasAndCannotOverwriteAReceiptOnlyOwner() {
        var source = Bank(); source.Observe(Evidence()); source.Observe(Evidence(1, key: Key(subject: 2)));
        var packet = source.CaptureObserver(Person(0));
        foreach (var policy in new[] {
            Policy(impressions: 1), Policy() with { ImpressionsPerObserver = 1 }, Policy(receipts: 1),
        }) {
            var destination = Bank(policy); var hash = destination.StateHash;
            Assert.False(destination.TryImportObserver(Person(0), source.Policy, packet, out var reason));
            Assert.Contains("capacity", reason); Assert.Equal(hash, destination.StateHash);
        }
        var full = Bank(Policy(impressions: 2, receipts: 2)); full.Observe(Evidence(key: Key(observer: 9)));
        var before = full.StateHash;
        Assert.False(full.TryImportObserver(Person(0), source.Policy, packet, out _)); Assert.Equal(before, full.StateHash);
        Assert.True(full.RemoveObserver(Person(9)));
        Assert.True(full.TryImportObserver(Person(0), source.Policy, packet, out var accepted), accepted);
        Assert.True(full.Forget(Key())); Assert.True(full.Forget(Key(subject: 2)));
        before = full.StateHash;
        Assert.False(full.TryImportObserver(Person(0), source.Policy, packet, out var owned));
        Assert.Contains("already owned", owned); Assert.Equal(before, full.StateHash);
    }

    [Fact]
    public void ImportRequiresEquivalentMemoryMeaningButNotEquivalentStorageOrWorkBudgets() {
        var policy = Policy(); var source = Bank(policy); source.Observe(Evidence()); var packet = source.CaptureObserver(Person(0));
        var incompatible = new[] {
            policy with { Dimensions = policy.Dimensions.Reverse().ToArray() },
            policy with { Dimensions = [policy.Dimensions[0] with { Baseline = 0.5m }, policy.Dimensions[1]] },
            policy with { EvidenceLifetimeSeconds = 2 }, policy with { ReliabilityDimension = null },
            policy with { UnfamiliarReliability = 0.25m }, policy with { ReportWeight = 0.25m }, policy with { DirectWeight = 2 },
        };
        foreach (var candidate in incompatible) {
            var destination = Bank(candidate); var hash = destination.StateHash;
            Assert.False(destination.TryImportObserver(Person(0), source.Policy, packet, out var reason));
            Assert.Contains("semantics differ", reason); Assert.Equal(hash, destination.StateHash);
        }
        var compatible = Bank(policy with { ImpressionCapacity = 4, ImpressionsPerObserver = 2, ReceiptCapacity = 3,
            EvidenceAttemptsPerTick = 1, ExpiredReceiptsPerTick = 2 });
        Assert.True(compatible.TryImportObserver(Person(0), source.Policy, packet, out var accepted), accepted);
        Assert.Equal(Read(source), Read(compatible));
    }

    [Fact]
    public void EmptyImportsDoNotConsumeOrdinalsAndNonemptyImportsCompactUnrelatedAdmissionHistory() {
        var source = Bank(); source.Observe(Evidence());
        var packet = source.Capture();
        packet = packet with {
            Impressions = [packet.Impressions[0] with { FirstReceiptOrdinal = ulong.MaxValue - 1 }],
            Receipts = [packet.Receipts[0] with { Ordinal = ulong.MaxValue - 1 }], NextOrdinal = ulong.MaxValue,
        };
        var destination = Bank(); destination.Observe(Evidence(key: Key(observer: 9)));
        Assert.True(destination.TryImportObserver(Person(0), source.Policy, packet, out var accepted), accepted);
        Assert.Equal(2UL, destination.Capture().NextOrdinal);
        var exhausted = WorldSocialMemory.Restore(source.Policy, source.Capture() with { NextOrdinal = ulong.MaxValue });
        var empty = source.CaptureObserver(Person(123)) with { NextOrdinal = ulong.MaxValue };
        var hash = exhausted.StateHash;
        Assert.True(exhausted.TryImportObserver(Person(123), source.Policy, empty, out accepted), accepted);
        Assert.Equal(hash, exhausted.StateHash);
    }

    [Fact]
    public void CheckpointValidationRefusesLyingCollectionCountsWithoutAllocatingThePolicyBank() {
        var bank = Bank(); bank.Observe(Evidence()); var checkpoint = bank.Capture();
        foreach (var count in new[] { -1, 0, 2 }) {
            var malformed = checkpoint with { Receipts = new ClaimedRows<WorldSocialReceiptCheckpoint>(count, checkpoint.Receipts) };
            Assert.Throws<ArgumentException>(() => WorldSocialMemory.Restore(bank.Policy, malformed));
            var destination = Bank(); var hash = destination.StateHash;
            Assert.False(destination.TryImportObserver(Person(0), bank.Policy, malformed, out _));
            Assert.Equal(hash, destination.StateHash);
        }
    }

    private sealed class ClaimedRows<T>(int count, IReadOnlyList<T> rows) : IReadOnlyList<T> {
        public int Count => count;
        public T this[int index] => rows[index];
        public IEnumerator<T> GetEnumerator() => rows.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void ImportScratchDoesNotScaleWithSourcePolicyCapacity() {
        var source = Bank(Policy(impressions: 16, receipts: 16));
        for (var subject = 1; subject <= 16; subject++) { source.Observe(Evidence((ulong)subject, key: Key(subject: subject))); }
        var packet = source.CaptureObserver(Person(0));
        var largePolicy = CompiledWorldSocialPolicy.Compile(Policy(impressions: CompiledWorldSocialPolicy.MaximumEntries,
            receipts: CompiledWorldSocialPolicy.MaximumEntries) with { ImpressionsPerObserver = 16 });
        var largePacket = packet with { PolicyIdentity = largePolicy.Identity };
        var destination = Bank(Policy(impressions: 16, receipts: 16));
        long Run(CompiledWorldSocialPolicy policy, WorldSocialMemoryCheckpoint incoming) {
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 32; iteration++) {
                destination.RemoveObserver(Person(0));
                if (!destination.TryImportObserver(Person(0), policy, incoming, out var reason)) { throw new InvalidOperationException(reason); }
            }
            return GC.GetAllocatedBytesForCurrentThread() - bytes;
        }
        // Warm both paths past tier-up well before measuring: a single warm-up pair leaves the measured call racing
        // a background re-JIT of TryImportObserver's shared generic-collection callees, whose completion timing
        // depends on how much unrelated work the rest of the suite is putting on the thread pool -- an eight-round
        // margin (256 calls per policy) keeps the measured allocation stable regardless of full-suite composition.
        for (var warmup = 0; warmup < 8; warmup++) { _ = Run(source.Policy, packet); _ = Run(largePolicy, largePacket); }
        var small = Run(source.Policy, packet); var large = Run(largePolicy, largePacket);
        Assert.Equal(small, large);
        Assert.Equal(Read(source), Read(destination));
        output.WriteLine($"social import scratch: 16 incoming impressions/receipts x 32; capacity 16 versus {CompiledWorldSocialPolicy.MaximumEntries}: {small}/{large} bytes");
    }

    [Fact]
    public void Dense4096ObserverComponentRoundTripPreservesEveryMemoryAndReleasesSourceStorage() {
        const int observers = 4096;
        const int neighbors = 16;
        var source = Bank(Policy(impressions: observers * neighbors, receipts: observers * neighbors) with {
            ImpressionsPerObserver = neighbors, EvidenceAttemptsPerTick = observers * neighbors,
        });
        var destination = Bank(Policy(impressions: observers * neighbors, receipts: observers * neighbors) with {
            ImpressionsPerObserver = neighbors, EvidenceAttemptsPerTick = 1, ExpiredReceiptsPerTick = 1,
        });
        source.Advance(1000);
        for (var observer = 0; observer < observers; observer++) {
            for (var neighbor = 1; neighbor <= neighbors; neighbor++) {
                Assert.Equal(WorldSocialEvidenceResult.Accepted, source.Observe(Evidence((ulong)neighbor,
                    key: Key(observer, (observer + neighbor) % observers), tick: source.EngineTick)));
            }
        }
        source.Advance(1010); var initial = Read(source);
        static void Move(WorldSocialMemory from, WorldSocialMemory to, int observer) {
            var address = Person(observer);
            var packet = from.CaptureObserver(address);
            if (!to.TryImportObserver(address, from.Policy, packet, out var reason)) { throw new InvalidOperationException(reason); }
            if (!from.RemoveObserver(address)) { throw new InvalidOperationException("source ownership disappeared before retirement"); }
        }
        Move(source, destination, 0); Move(destination, source, 0); // warm the same operations without changing logical beliefs
        var bytes = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
        for (var observer = 0; observer < observers; observer++) { Move(source, destination, observer); }
        var outbound = Stopwatch.GetElapsedTime(start);
        var outboundBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Assert.Equal(0, source.ImpressionCount); Assert.Equal(0, source.ReceiptCount);
        Assert.Equal(observers * neighbors, destination.ImpressionCount); Assert.Equal(observers * neighbors, destination.ReceiptCount);
        Assert.Equal(initial, Read(destination));
        source.Advance(1011); destination.Advance(1);
        bytes = GC.GetAllocatedBytesForCurrentThread(); start = Stopwatch.GetTimestamp();
        for (var observer = observers - 1; observer >= 0; observer--) { Move(destination, source, observer); }
        var inbound = Stopwatch.GetElapsedTime(start); var inboundBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Assert.Equal(0, destination.ImpressionCount); Assert.Equal(0, destination.ReceiptCount);
        Assert.Equal(observers * neighbors, source.ImpressionCount); Assert.Equal(observers * neighbors, source.ReceiptCount);
        for (var observer = 0; observer < observers; observer++) {
            for (var neighbor = 1; neighbor <= neighbors; neighbor++) {
                Assert.Equal(initial with { AgeTicks = 11 }, Read(source, Key(observer, (observer + neighbor) % observers)));
            }
        }
        Assert.Equal(source.StateHash, WorldSocialMemory.Restore(source.Policy, source.Capture()).StateHash);
        output.WriteLine($"social component round-trip: {observers} owners x {neighbors} memories; outbound {outbound.TotalMilliseconds:F3}ms/{outboundBytes}B; return {inbound.TotalMilliseconds:F3}ms/{inboundBytes}B; not a transport or frame-time test");
    }

    [Fact]
    public void PolicyCompilationIsStrictAndCopiesTheMutableAuthoringList() {
        var source = Policy(); var dimensions = source.Dimensions.ToList(); source = source with { Dimensions = dimensions };
        var compiled = CompiledWorldSocialPolicy.Compile(source); var identity = compiled.Identity;
        dimensions[0] = dimensions[0] with { Baseline = 1 };
        Assert.Equal(0, compiled.Dimensions[0].Baseline); Assert.Equal(identity, compiled.Identity);
        var json = JsonSerializer.Serialize(source, WorldJsonContext.Default.WorldSocialPolicy);
        var parsed = JsonSerializer.Deserialize(json, WorldJsonContext.Default.WorldSocialPolicy)!;
        Assert.Equal(JsonSerializer.Serialize(parsed, WorldJsonContext.Default.WorldSocialPolicy), json);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json.Insert(1, "\"typo\":1,"), WorldJsonContext.Default.WorldSocialPolicy));
        var invalid = new[] {
            source with { Dimensions = [] }, source with { Dimensions = [dimensions[0], dimensions[0]] },
            source with { ImpressionCapacity = 0 }, source with { ReceiptCapacity = int.MaxValue },
            source with { ImpressionsPerObserver = source.ImpressionCapacity + 1 }, source with { EvidenceAttemptsPerTick = 0 },
            source with { ExpiredReceiptsPerTick = 0 }, source with { EvidenceLifetimeSeconds = 0 },
            source with { EvidenceLifetimeSeconds = 0.0000001m }, source with { ReliabilityDimension = "kindness" },
            source with { UnfamiliarReliability = 2 }, source with { DirectWeight = -1 },
            source with { DirectWeight = 0.0000001m }, source with { ReportWeight = 0.0000001m },
            source with { Dimensions = [dimensions[0] with { Minimum = 1 }] },
            source with { Dimensions = [dimensions[0] with { PriorWeight = 0.0000001m }] },
            source with { Dimensions = [dimensions[0] with { FollowUpBoost = 17 }] },
            source with { Dimensions = [dimensions[0] with { RecoverySeconds = -1 }] },
        };
        foreach (var candidate in invalid) { Assert.Throws<ArgumentException>(() => CompiledWorldSocialPolicy.Compile(candidate)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dense4096ObserverWorkloadHasExactBudgetsReplayAndNoSteadyStateAllocations(bool fullStorage) {
        const int observers = 4096;
        var policy = Policy(impressions: observers * 8, receipts: observers * 16) with {
            ImpressionsPerObserver = 8, EvidenceAttemptsPerTick = observers, ExpiredReceiptsPerTick = observers,
        };
        var bank = Bank(policy);
        var events = new WorldSocialEvidence[observers];
        for (var i = 0; i < observers; i++) { events[i] = Evidence(key: Key(observer: i, subject: (i + 1) % observers)); }
        if (fullStorage) {
            ulong sequence = 0;
            for (var repetition = 0; repetition < 2; repetition++) {
                for (var dimension = 0; dimension < 2; dimension++) {
                    for (var neighbor = 1; neighbor <= 4; neighbor++) {
                        bank.Advance(++sequence);
                        for (var observer = 0; observer < observers; observer++) {
                            Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence(sequence,
                                key: Key(observer, (observer + neighbor) % observers, dimension), tick: bank.EngineTick)));
                        }
                    }
                }
            }
            Assert.Equal(policy.ImpressionCapacity, bank.ImpressionCount);
            Assert.Equal(policy.ReceiptCapacity, bank.ReceiptCount);
        }
        void Step(ulong tick) {
            bank.Advance((tick + 100) * 50401);
            for (var i = 0; i < observers; i++) {
                var e = events[i] with { Event = events[i].Event with { Sequence = tick }, OccurredAt = bank.EngineTick };
                if (bank.Observe(e) != WorldSocialEvidenceResult.Accepted || !bank.TryRead(e.Impression, out _)) {
                    throw new InvalidOperationException("dense social workload refused");
                }
            }
            _ = bank.StateHash;
        }
        for (ulong tick = 1; tick <= 100; tick++) { Step(tick); }
        var clock = new Stopwatch(); var start = GC.GetAllocatedBytesForCurrentThread(); clock.Start();
        for (ulong tick = 101; tick <= 300; tick++) { Step(tick); }
        clock.Stop(); var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(0, allocated);
        Assert.Equal(fullStorage ? policy.ImpressionCapacity : observers, bank.ImpressionCount);
        Assert.Equal(fullStorage ? policy.ReceiptCapacity : observers, bank.ReceiptCount);
        Assert.Equal(observers, bank.EvidenceAttempts); Assert.Equal(observers, bank.ReclaimedReceipts);
        var restored = WorldSocialMemory.Restore(bank.Policy, bank.Capture()); Assert.Equal(bank.StateHash, restored.StateHash);
        output.WriteLine($"social: 4096 observers x 200 steps; fullStorage={fullStorage}; {clock.Elapsed.TotalMilliseconds:F3}ms; {allocated} steady-state bytes; hash={bank.StateHash:X16}");
    }
}
