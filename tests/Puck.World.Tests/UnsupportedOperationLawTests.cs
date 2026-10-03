using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST (acceptance law 6): every refusal the engine classifies as an intentionally unsupported operation
/// (<see cref="RefusalAttribute.Unsupported"/>) refuses before any state changes. The theory's rows are the classified
/// set read from <see cref="RefusalCatalog"/>, so a refusal joins the law by being classified, and a classified refusal
/// with no arrangement here fails by name. For each, twins A and B boot from one document and are arranged alike; A
/// attempts the operation, which refuses naming the classified id, and both then step. Their document bytes,
/// authoritative hashes, journal lengths and encoded checkpoints (or the checkpoint's refusal) are equal, and so is
/// every surface the row reads outside the checkpoint through its witness. The legal variant of the operation is then
/// shown to change what the refusal left alone.
/// </summary>
public sealed class UnsupportedOperationLawTests {
    private sealed class Twin : IDisposable {
        private readonly TemporaryDirectory m_directory = new(prefix: "puck-unsupported-operation-");

        public Twin(WorldDefinition definition) {
            Fixture = Fixtures.FreshServer(definition: definition);
            Tape = new WorldReplayTape(
                addonHostFactory: static (_, _) => new NullAddonHost(),
                engines: [],
                liveServer: Fixture.Server,
                machineHostFactory: Fixtures.MachineHostFactory,
                profiles: Fixture.Server.Profiles,
                stateRoot: new WorldStateRoot(path: m_directory.RootPath),
                transport: new LoopbackTransport(server: Fixture.Server)
            );
        }

        public WorldFixture Fixture { get; }
        public WorldServer Server => Fixture.Server;
        public WorldReplayTape Tape { get; }

        public void Dispose() {
            if (Tape.Mode == WorldReplayMode.Recording) {
                _ = Tape.CancelRecording();
            }
            Fixture.Dispose();
            m_directory.Dispose();
        }
        public void Step() {
            Fixture.Step();
            if (Tape.Mode == WorldReplayMode.Recording) {
                Tape.NoteTick();
            }
        }
        public void Arm() => Assert.True(condition: Tape.TryBeginRecording(name: "twin", refusal: out var refusal), userMessage: refusal);
        // Everything the checkpoint captures, spelled so two twins compare as one string.
        public string State() {
            var tick = (Server.NextInputTick - 1UL);
            var checkpoint = (Server.TryCaptureCheckpoint(checkpoint: out var captured, hostRow: WorldAuthorityHostRowCheckpoint.Empty, reason: out var reason)
                ? Convert.ToHexString(inArray: WorldAuthorityCheckpointCodec.Encode(checkpoint: captured!))
                : $"refused: {reason}");

            return string.Join(separator: "\n", values: [
                $"document={Convert.ToHexString(inArray: WorldDefinitionSerialization.Serialize(definition: Server.Definition))}",
                $"hash={WorldStateHashComposition.HashAuthoritative(server: Server, tick: tick)}",
                $"journal={Server.JournalLength}",
                $"checkpoint={checkpoint}",
            ]);
        }
    }

    // How one classified refusal is arranged and attempted: the document both twins boot from, what both do first, the
    // operation A attempts (answering its refusal text), the witness over every surface the operation touches outside
    // the checkpoint, and the legal variant: how its own twin is arranged, and the operation, answering whether it
    // succeeded.
    private sealed record Arrangement(
        Func<WorldDefinition> Document,
        Action<Twin> Arrange,
        Func<Twin, string> Operate,
        Func<Twin, string> Witness,
        Action<Twin> ArrangeLegal,
        Func<Twin, bool> Legal
    );

    private static WorldDefinition Pulled(WorldIdentity owned) => (owned.Document! with { Identity = owned.Document.Identity! with { Name = "Pulled" } });
    private static string Catalog(Twin twin) {
        var owned = twin.Server.Profiles.BootProfile;

        return $"recording={twin.Server.Profiles.Recording} owned={owned.Name} document={Convert.ToHexString(inArray: WorldDefinitionSerialization.Serialize(definition: owned.Document!))}";
    }
    private static string Taps(Twin twin) =>
        $"mode={twin.Tape.Mode} recording={twin.Server.Profiles.Recording} mutationTap={(twin.Server.MutationTap is not null)} rebuildTap={(twin.Server.RebuildTap is not null)} arrivalTap={(twin.Server.ArrivalTap is not null)} departureTap={(twin.Server.DepartureTap is not null)}";

    private static readonly IReadOnlyDictionary<string, Arrangement> Arrangements = new Dictionary<string, Arrangement>(comparer: StringComparer.Ordinal) {
        ["replay.record/ArmedAfterFirstStep"] = new(
            Arrange: static twin => twin.Step(),
            // The legal arm is the one before the world's first step.
            ArrangeLegal: static _ => { },
            Document: static () => Fixtures.BuildDocument(),
            Legal: static twin => twin.Tape.TryBeginRecording(name: "legal", refusal: out _),
            Operate: static twin => (twin.Tape.TryBeginRecording(name: "late", refusal: out var refusal) ? string.Empty : refusal),
            Witness: Taps
        ),
        ["storage.pull/PullWhileRecording"] = new(
            Arrange: static twin => {
                twin.Arm();
                twin.Step();
            },
            // The legal pull is the one no recording is under way for.
            ArrangeLegal: static twin => twin.Step(),
            Document: static () => Fixtures.BuildDocument(),
            Legal: static twin => (twin.Server.Profiles.ReplaceFromSync(document: Pulled(owned: twin.Server.Profiles.BootProfile), reason: out _) && (twin.Server.Profiles.BootProfile.Name == "Pulled")),
            Operate: static twin => (twin.Server.Profiles.ReplaceFromSync(document: Pulled(owned: twin.Server.Profiles.BootProfile), reason: out var reason) ? string.Empty : reason),
            Witness: Catalog
        ),
    };

    private static IEnumerable<string> ClassifiedIds() => RefusalCatalog.All().Where(predicate: static entry => entry.Unsupported).Select(selector: static entry => $"{entry.Door}/{entry.Id}");
    public static TheoryData<string> Classified() => [.. ClassifiedIds()];

    [Theory]
    [MemberData(nameof(Classified))]
    public void AnUnsupportedOperationChangesNothingBeforeItRefuses(string refusal) {
        Assert.True(condition: Arrangements.TryGetValue(key: refusal, value: out var arrangement), userMessage: $"{refusal} is classified as an intentionally unsupported operation and law 6 has no arrangement for it");

        var definition = arrangement.Document();
        using var a = new Twin(definition: definition);
        using var b = new Twin(definition: definition);

        arrangement.Arrange(obj: a);
        arrangement.Arrange(obj: b);

        var before = (State: a.State(), Witness: arrangement.Witness(arg: a));
        var refused = arrangement.Operate(arg: a);

        Assert.StartsWith(expectedStartString: $"{refusal[(refusal.IndexOf(value: '/') + 1)..]}:", actualString: refused, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: before.State, actual: a.State());
        Assert.Equal(expected: before.Witness, actual: arrangement.Witness(arg: a));
        for (var step = 0; (step < 2); step++) {
            a.Step();
            b.Step();
        }
        Assert.Equal(expected: b.State(), actual: a.State());
        Assert.Equal(expected: arrangement.Witness(arg: b), actual: arrangement.Witness(arg: a));

        // The control: the legal variant, on a twin arranged for it, moves the witness the refusal left alone.
        using var legal = new Twin(definition: definition);

        arrangement.ArrangeLegal(obj: legal);

        var witness = arrangement.Witness(arg: legal);

        Assert.True(condition: arrangement.Legal(arg: legal), userMessage: $"{refusal}: the legal variant refused");
        Assert.NotEqual(expected: witness, actual: arrangement.Witness(arg: legal));
    }
    [Fact]
    public void EveryArrangementNamesAClassifiedRefusal() {
        var classified = ClassifiedIds().ToHashSet(comparer: StringComparer.Ordinal);

        Assert.All(collection: Arrangements.Keys, action: key => Assert.Contains(expected: key, collection: classified));
    }
}
