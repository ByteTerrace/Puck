using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="ForwardingWorkCounterSource"/>: a reading never goes down when the owner replaces the instance
/// behind it or retires one of the instances it runs beside it, the retired instance's totals are carried exactly once,
/// the live instances are summed, and the forwarder keeps the source contract — a
/// declared kind reads zero before any target, an undeclared kind is unavailable, and only a same-named instance can
/// stand behind it.
/// </summary>
public sealed class ForwardingWorkCounterSourceLawTests {
    private static readonly WorkKind Visits = new(name: "test.source.visits", unit: "lanes", workClass: WorkClass.Deterministic);
    private static readonly WorkKind Probes = new(name: "test.source.probes", unit: "count", workClass: WorkClass.Deterministic);
    private static readonly WorkKind[] Kinds = [Visits, Probes];

    private sealed class Counter(string name = "test.source") : IWorkCounterSource {
        private WorkCount m_probes;
        private WorkCount m_visits;

        public string Name => name;
        public ReadOnlySpan<WorkKind> WorkKinds => Kinds;

        public void Count(long visits, long probes) {
            m_visits.Add(amount: visits);
            m_probes.Add(amount: probes);
        }
        public bool TryRead(WorkKind kind, out long value) {
            if (ReferenceEquals(objA: kind, objB: Visits)) {
                value = m_visits.Value;

                return true;
            }

            if (ReferenceEquals(objA: kind, objB: Probes)) {
                value = m_probes.Value;

                return true;
            }

            value = 0L;

            return false;
        }
    }

    private static long Read(IWorkCounterSource source, WorkKind kind) {
        Assert.True(condition: source.TryRead(kind: kind, value: out var value));

        return value;
    }

    [Fact]
    public void ReadingsNeverGoDownAcrossARetarget() {
        var forwarder = new ForwardingWorkCounterSource(kinds: Kinds, name: "test.source");
        var first = new Counter();
        var second = new Counter();

        forwarder.Retarget(target: first);
        first.Count(probes: 2L, visits: 40L);

        var before = Read(kind: Visits, source: forwarder);

        forwarder.Retarget(target: second);

        // The replacement starts from zero; read directly it would go down, which is what the forwarder exists to stop.
        Assert.True(condition: (Read(kind: Visits, source: second) < before));
        Assert.Equal(actual: Read(kind: Visits, source: forwarder), expected: 40L);
        Assert.Equal(actual: Read(kind: Probes, source: forwarder), expected: 2L);

        second.Count(probes: 1L, visits: 5L);
        Assert.Equal(actual: Read(kind: Visits, source: forwarder), expected: 45L);
        Assert.Equal(actual: Read(kind: Probes, source: forwarder), expected: 3L);
    }
    [Fact]
    public void RetargetingToTheCurrentInstanceCarriesNothingTwice() {
        var forwarder = new ForwardingWorkCounterSource(kinds: Kinds, name: "test.source");
        var only = new Counter();

        forwarder.Retarget(target: only);
        only.Count(probes: 0L, visits: 7L);
        forwarder.Retarget(target: only);
        Assert.Equal(actual: Read(kind: Visits, source: forwarder), expected: 7L);
    }
    [Fact]
    public void ItKeepsTheSourceContract() {
        var forwarder = new ForwardingWorkCounterSource(kinds: Kinds, name: "test.source");

        Assert.Equal(actual: forwarder.Name, expected: "test.source");
        Assert.Equal(actual: forwarder.WorkKinds.ToArray(), expected: Kinds);
        Assert.Equal(actual: Read(kind: Visits, source: forwarder), expected: 0L);
        Assert.False(condition: forwarder.TryRead(kind: new WorkKind(name: "test.source.visits", unit: "lanes", workClass: WorkClass.Deterministic), value: out var undeclared));
        Assert.Equal(actual: undeclared, expected: 0L);
        _ = Assert.Throws<ArgumentException>(testCode: () => forwarder.Retarget(target: new Counter(name: "test.other")));
        _ = Assert.Throws<ArgumentException>(testCode: () => forwarder.Attach(instance: new Counter(name: "test.other")));
        _ = Assert.Throws<ArgumentException>(testCode: static () => new ForwardingWorkCounterSource(kinds: Kinds, name: "nodots"));
    }
    [Fact]
    public void AttachedInstancesAddToTheTargetAndCarryForwardWhenDetached() {
        var forwarder = new ForwardingWorkCounterSource(kinds: Kinds, name: "test.source");
        var primary = new Counter();
        var first = new Counter();
        var second = new Counter();

        forwarder.Retarget(target: primary);
        forwarder.Attach(instance: first);
        forwarder.Attach(instance: second);
        primary.Count(probes: 0L, visits: 100L);
        first.Count(probes: 1L, visits: 10L);
        second.Count(probes: 2L, visits: 20L);

        // Every live instance is summed.
        Assert.Equal(actual: Read(kind: Visits, source: forwarder), expected: 130L);
        Assert.Equal(actual: Read(kind: Probes, source: forwarder), expected: 3L);

        // A detached instance's totals stay, and what it counts afterwards is not read.
        forwarder.Detach(instance: first);
        first.Count(probes: 5L, visits: 50L);
        Assert.Equal(actual: Read(kind: Visits, source: forwarder), expected: 130L);
        Assert.Equal(actual: Read(kind: Probes, source: forwarder), expected: 3L);

        // Attaching twice sums once, and detaching an instance that is not attached carries nothing.
        forwarder.Attach(instance: second);
        forwarder.Detach(instance: first);
        Assert.Equal(actual: Read(kind: Visits, source: forwarder), expected: 130L);

        // Retargeting the primary leaves the attached instances in place.
        forwarder.Retarget(target: new Counter());
        second.Count(probes: 0L, visits: 1L);
        Assert.Equal(actual: Read(kind: Visits, source: forwarder), expected: 131L);
    }
}
