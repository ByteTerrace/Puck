using System.Reflection;
using Puck.Commands;
using Puck.Maths;
using Puck.Networking;
using Puck.Physics.Motion;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// A slot's continuation is its sections, each encoded by a static function of the section alone
/// (<see cref="IContinuationSection{TSelf}"/>). These laws hold the two halves a section's encoder owes: every field
/// of every section reaches its bytes, so a field added to a section cannot be left out of its digest, and what a
/// section writes it reads back. The sections are found by walking the entry's own visit order, so a section added
/// there is covered here with no list to update.
/// </summary>
public sealed class ContinuationSectionLawTests {
    // Two valid values of a leaf type whose wire form refuses arbitrary field values: the unset value a fresh section
    // holds and one that differs from it. A reference type or a constrained leaf a section gains needs a row here;
    // the law fails by name until it has one, and never skips a field.
    private static readonly Dictionary<Type, (object? Baseline, object Mutated)> Samples = new() {
        [typeof(PlayerIntent)] = (default(PlayerIntent), default(PlayerIntent).WithChannel(ordinal: 0, value: FixedQ4816.One)),
        [typeof(Principal)] = (default(Principal), Principal.Seat(slot: 1)),
        [typeof(IntentSource)] = (IntentSource.Live, IntentSource.Idle),
        [typeof(GrantSubject)] = (GrantSubject.All, GrantSubject.Body(index: 3)),
        [typeof(WorldMobilityIdentity)] = (
            new WorldMobilityIdentity(Incarnation: new WorldEntityAddress(Authority: "a", Index: 1, Generation: 1), Epoch: 1UL, DepartedFrom: new WorldEntityAddress(Authority: "a", Index: 1, Generation: 1)),
            new WorldMobilityIdentity(Incarnation: new WorldEntityAddress(Authority: "a", Index: 1, Generation: 1), Epoch: 2UL, DepartedFrom: new WorldEntityAddress(Authority: "b", Index: 2, Generation: 3))
        ),
        [typeof(WorldAdmissionTrustIdentity)] = (null, new WorldAdmissionTrustIdentity(Mode: WorldAdmissionTrustMode.SignsDirectly, Domain: "domain", Subject: "subject", Algorithm: null, KeyHash: null)),
        [typeof(WorldContinuumTrajectory)] = (
            new WorldContinuumTrajectory(PreviousPosition: FixedVector3.Zero, SourceTick: 1UL, ContinuumStartEngineTick: 0UL, ContinuumEndEngineTick: 1UL, ConsumedThroughEngineTick: 1UL, BoundaryEvents: 1),
            new WorldContinuumTrajectory(PreviousPosition: FixedVector3.UnitY, SourceTick: 2UL, ContinuumStartEngineTick: 1UL, ContinuumEndEngineTick: 3UL, ConsumedThroughEngineTick: 4UL, BoundaryEvents: 2)
        ),
        [typeof(string)] = (null, "x"),
    };

    private static object? DefaultOf(Type type) => ((type.IsValueType && (Nullable.GetUnderlyingType(nullableType: type) is null))
        ? Activator.CreateInstance(type: type)
        : null
    );
    private static object ArrayOf(Type element, object? value) {
        var array = Array.CreateInstance(elementType: element, length: 1);

        array.SetValue(index: 0, value: (value ?? DefaultOf(type: element)));
        return array;
    }
    private static string FieldName(FieldInfo field) => (field.Name.StartsWith(value: '<')
        ? field.Name[1..field.Name.IndexOf(value: '>')]
        : field.Name
    );

    // A timed move writes its fields only while it is active (it has a duration), so its leaves are varied against an
    // active move; being active at all is its own leaf.
    private static List<(string Path, object? Baseline, object? Mutated)> TimedMoveVariants() {
        var active = new WorldTimedBodyMove(
            Motion: new FixedTimedMove(Displacement: FixedVector3.UnitX, Yaw: FixedQ4816.One, DurationTicks: 10UL, Curve: BodyMotionCurve.Linear),
            StartYaw: FixedQ4816.One,
            ElapsedTicks: 1UL
        );

        return [
            (".active", default(WorldTimedBodyMove), active),
            (".Motion.Displacement", active, active with { Motion = active.Motion with { Displacement = FixedVector3.UnitY } }),
            (".Motion.Yaw", active, active with { Motion = active.Motion with { Yaw = FixedQ4816.Zero } }),
            (".Motion.DurationTicks", active, active with { Motion = active.Motion with { DurationTicks = 11UL } }),
            (".Motion.Curve", active, active with { Motion = active.Motion with { Curve = BodyMotionCurve.Smoothstep } }),
            (".StartYaw", active, active with { StartYaw = FixedQ4816.Zero }),
            (".ElapsedTicks", active, active with { ElapsedTicks = 2UL }),
        ];
    }

    // Every way a value of the type can differ from its unset value in one leaf: the path to the leaf, the value
    // without the difference and the value with it.
    private static List<(string Path, object? Baseline, object? Mutated)> Variants(Type type) {
        if (type == typeof(WorldTimedBodyMove)) {
            return TimedMoveVariants();
        }
        if (Samples.TryGetValue(key: type, value: out var sample)) {
            return [(string.Empty, sample.Baseline, sample.Mutated)];
        }
        if (type == typeof(bool)) {
            return [(string.Empty, false, true)];
        }
        if (type == typeof(UInt128)) {
            return [(string.Empty, UInt128.Zero, UInt128.One)];
        }
        if (type.IsPrimitive) {
            return [(string.Empty, Activator.CreateInstance(type: type), Convert.ChangeType(conversionType: type, provider: System.Globalization.CultureInfo.InvariantCulture, value: 1))];
        }
        if (type.IsEnum) {
            var unset = Activator.CreateInstance(type: type)!;

            return [(string.Empty, unset, Enum.GetValues(enumType: type).Cast<object>().First(predicate: value => !value.Equals(obj: unset)))];
        }
        if (Nullable.GetUnderlyingType(nullableType: type) is { } underlying) {
            // Present and unset is the leaf type's own unset value, which for a constrained leaf is its sample's.
            var unset = (Samples.TryGetValue(key: underlying, value: out var leaf) ? leaf.Baseline : DefaultOf(type: underlying));
            var variants = new List<(string, object?, object?)> { ("?", null, unset) };

            foreach (var (path, baseline, mutated) in Variants(type: underlying)) {
                variants.Add(item: (path, (baseline ?? unset), mutated));
            }
            return variants;
        }
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(System.Collections.Immutable.ImmutableArray<>))) {
            var element = type.GetGenericArguments()[0];
            var create = typeof(System.Collections.Immutable.ImmutableArray).GetMethods()
                .Single(predicate: static method => ((method.Name == nameof(System.Collections.Immutable.ImmutableArray.Create)) && (method.GetParameters() is [{ ParameterType.IsArray: true }])))
                .MakeGenericMethod(typeArguments: element);
            var variants = new List<(string, object?, object?)>();

            foreach (var (path, baseline, mutated) in Variants(type: element)) {
                variants.Add(item: (
                    $"[0]{path}",
                    create.Invoke(obj: null, parameters: [ArrayOf(element: element, value: baseline)]),
                    create.Invoke(obj: null, parameters: [ArrayOf(element: element, value: mutated)])
                ));
            }
            return variants;
        }
        if (type.IsGenericType && ((type.GetGenericTypeDefinition() == typeof(ReadOnlyMemory<>)) || (type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)))) {
            var element = type.GetGenericArguments()[0];
            var memory = (type.GetGenericTypeDefinition() == typeof(ReadOnlyMemory<>));
            var variants = new List<(string, object?, object?)>();

            foreach (var (path, baseline, mutated) in Variants(type: element)) {
                var before = ArrayOf(element: element, value: baseline);
                var after = ArrayOf(element: element, value: mutated);

                variants.Add(item: ($"[0]{path}", (memory ? Activator.CreateInstance(type, before) : before), (memory ? Activator.CreateInstance(type, after) : after)));
            }
            return variants;
        }
        if (type.IsValueType) {
            var variants = new List<(string, object?, object?)>();

            foreach (var field in type.GetFields(bindingAttr: (BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))) {
                foreach (var (path, baseline, mutated) in Variants(type: field.FieldType)) {
                    var before = Activator.CreateInstance(type: type)!;
                    var after = Activator.CreateInstance(type: type)!;

                    field.SetValue(obj: before, value: (baseline ?? DefaultOf(type: field.FieldType)));
                    field.SetValue(obj: after, value: mutated);
                    variants.Add(item: ($".{FieldName(field: field)}{path}", before, after));
                }
            }
            return variants;
        }

        throw new InvalidOperationException(message: $"no sample values for {type}: add a row to {nameof(Samples)}.");
    }

    private sealed class SectionLaw : IContinuationSectionVisitor {
        private readonly WireWriter m_writer = new();

        public List<WorldContinuationSectionKind> Order { get; } = [];
        public int Fields { get; private set; }

        private byte[] Encode<TSection>(in TSection section) where TSection : struct, IContinuationSection<TSection> {
            m_writer.Reset();
            TSection.Write(section: in section, writer: m_writer);
            return m_writer.WrittenSpan.ToArray();
        }

        public void Visit<TSection>(ref TSection section) where TSection : struct, IContinuationSection<TSection> {
            Order.Add(item: TSection.Section);
            foreach (var (path, baseline, mutated) in Variants(type: typeof(TSection))) {
                var name = $"{typeof(TSection).Name}{path}";
                var before = ((TSection)baseline!);
                var after = ((TSection)mutated!);
                var bytes = Encode(section: in after);

                Assert.True(
                    condition: (WorldAuthorityCheckpointCodec.SectionDigest(section: in before, writer: m_writer) != WorldAuthorityCheckpointCodec.SectionDigest(section: in after, writer: m_writer)),
                    userMessage: $"{name} does not reach the section's digest"
                );

                var reader = new WireReader(bytes: bytes);
                var read = TSection.Read(reader: ref reader);

                Assert.True(condition: reader.TryFinish(failure: out var failure), userMessage: $"{name} does not read back: {failure}");
                Assert.True(condition: bytes.AsSpan().SequenceEqual(other: Encode(section: in read)), userMessage: $"{name} reads back as another value");
                Fields++;
            }
        }
    }

    // THE LAW: every field of every section reaches the section's digest, and a section reads back what it wrote. Each
    // leaf of each section is set alone to a value that differs from its unset one; the digest must move, the bytes
    // must decode to the end, and the decoded section must encode to the same bytes. The red leg is an encoder that
    // skips a field, or a reader that reads two fields in the other order.
    [Fact]
    public void EveryFieldOfEverySectionReachesItsDigestAndReadsBack() {
        var entry = default(WorldPopulationEntryCheckpoint);
        var law = new SectionLaw();

        WorldPopulationEntryCheckpoint.VisitSections(entry: ref entry, visitor: ref law);

        Assert.Equal(expected: Enum.GetValues<WorldContinuationSectionKind>(), actual: law.Order);
        Assert.True(condition: (law.Fields > 200), userMessage: $"the law reached {law.Fields} fields");
    }
    // THE LAW: a population's continuation fold reads a slot's folded sections and, beside the slots, the parts of
    // its tail the fold names; it reads nothing else. The rendered look and the register values move no fold (the
    // arena folds the registers itself); a section field, a resting body's contact-field observation and a profile
    // each do; an awake body's observation is current by construction and is not read.
    [Fact]
    public void TheFoldReadsTheFoldedSectionsAndTheTailPartsItNames() {
        using var fixture = Fixtures.FreshServer();

        _ = fixture.JoinSeat();
        fixture.Step();

        var population = fixture.Server.Population;
        var entry = population.Capture().Entries.Single(predicate: static row => (row.Index == 0));

        ulong Fold(WorldPopulationEntryCheckpoint image) {
            var hash = Fnv1aHash.Create();

            WorldAuthorityCheckpointCodec.AppendPopulationEntries(capacity: population.Capacity, entries: [image], hash: ref hash);
            return hash.Value;
        }

        var steady = Fold(image: entry);
        var live = Fnv1aHash.Create();

        population.AppendContinuationHash(hash: ref live);
        Assert.Equal(expected: live.Value, actual: steady);
        Assert.Equal(expected: steady, actual: Fold(image: entry with { Look = entry.Look with { CatalogRig = ((byte)(entry.Look.CatalogRig + 1)) } }));
        Assert.Equal(expected: steady, actual: Fold(image: entry with { Tail = entry.Tail with { Registers = new long[] { 7L, 9L } } }));
        Assert.Equal(expected: steady, actual: Fold(image: entry with { Tail = entry.Tail with { ContactFieldObservationCurrent = false } }));
        Assert.NotEqual(expected: steady, actual: Fold(image: entry with { Body = entry.Body with { Motion = entry.Body.Motion with { InMedium = !entry.Body.Motion.InMedium } } }));
        Assert.NotEqual(expected: steady, actual: Fold(image: entry with { Body = entry.Body with { Pose = entry.Body.Pose with { Yaw = (entry.Body.Pose.Yaw + FixedQ4816.One) } } }));
        Assert.NotEqual(expected: steady, actual: Fold(image: entry with { Admission = entry.Admission with { TravelTurn = FixedQ4816.FromRawBits(value: 1L) } }));

        var asleep = entry with { Body = entry.Body with { Clocks = entry.Body.Clocks with { AsleepSinceTick = 5UL } } };

        Assert.NotEqual(expected: Fold(image: asleep), actual: Fold(image: asleep with { Tail = asleep.Tail with { ContactFieldObservationCurrent = false } }));
        Assert.NotEqual(
            expected: steady,
            actual: Fold(image: entry with { Tail = entry.Tail with { Profile = new WorldIdentityProjection(ColorHex: "#102030", Id: "visitor", MoveSpeed: null, Name: "Visitor", TurnSpeed: null) } })
        );
    }
}
