using Puck.Abstractions.Machines;
using Puck.Commands;
using Puck.Testing;
using Puck.World.Machines;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST (acceptance law 6): every refusal the engine classifies as an intentionally unsupported operation
/// (<see cref="RefusalAttribute.Unsupported"/>) refuses before any state changes. The theory's rows are the classified
/// set read from <see cref="RefusalCatalog"/>, so a refusal joins the law by being classified, and a classified refusal
/// with no arrangement here fails by name. For each, twins A and B boot from one document and are arranged alike; A
/// attempts the operation, whose refusal names the classified id, and both then step. Their document bytes,
/// authoritative hashes, journal lengths and encoded checkpoints (or the checkpoint's refusal) are equal, and so is
/// every surface the row reads outside the checkpoint through its witness. The legal variant of the operation is then
/// shown to change what the refusal left alone.
/// </summary>
public sealed class UnsupportedOperationLawTests {
    private sealed class Twin : IDisposable {
        private readonly TemporaryDirectory m_directory = new(prefix: "puck-unsupported-operation-");

        public Twin(WorldDefinition definition, WorldMachineCatalog? machineCatalog = null) {
            Fixture = Fixtures.FreshServer(definition: definition, machineCatalog: machineCatalog);
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
    // operation A attempts (answering its refusal text) and how many steps it takes, which B steps alike, the witness
    // over every surface the operation touches outside the checkpoint, and the legal variant: how its own twin is
    // arranged, and the operation, answering whether it succeeded.
    private sealed record Arrangement(
        Func<WorldDefinition> Document,
        Action<Twin> Arrange,
        Func<Twin, string> Operate,
        Func<Twin, string> Witness,
        Action<Twin> ArrangeLegal,
        Func<Twin, bool> Legal,
        int OperationSteps = 0,
        Func<WorldMachineCatalog>? Catalog = null,
        Func<WorldMachineCatalog>? LegalCatalog = null
    );
    private const int UndoDepth = 3;

    // Five journaled writes into a journal bounded to three entries, the earliest two compacted into the base.
    private static void FillJournal(Twin twin) {
        for (var index = 0; (index < 5); index++) {
            twin.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
                Principal: Principal.Console,
                Row: new WorldStateRow(Name: CellName.Parse(candidate: $"probe{index}"), Kind: CellKind.Int)
            ));
            twin.Step();
            twin.Server.EnforceJournalDepth();
        }
    }
    // The machine operation laws' cabinet, with an operator granted Control over it.
    private static readonly Principal Operator = Principal.Addon(name: "operator");

    private static void GrantOperator(Twin twin) => twin.Server.Grant(
        new WorldGrant(Operator, WorldCapability.Control, GrantSubject.Machine(name: "cabinet"), false),
        Principal.Console
    );
    private static MachineOperationResult OperateCabinet(Twin twin) => Assert.IsType<WorldSubmissionResult.MachineOperation>(@object: WorldMachineOperationServerLawTests.Submit(
        twin.Server,
        Operator,
        WorldMachineOperationServerLawTests.Operation(generation: twin.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation, instance: "cabinet", model: "next")
    )).Result;
    private static string Cabinet(Twin twin) =>
        $"generation={twin.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation} configuration={twin.Server.Definition.Machines.Single(predicate: static row => (row.Name == "cabinet")).Configuration.GetRawText()}";
    private static WorldMachineCatalog Operating() => new([new WorldMachineOperationServerLawTests.OperationEngine()]);
    // The same device, from a provider that performs no operations.
    private static WorldMachineCatalog Inert() => new([new InertEngine(inner: new WorldMachineOperationServerLawTests.OperationEngine())]);

    private sealed class InertEngine(IMachineEngine inner) : IMachineEngine {
        public string Id => inner.Id;
        public MachineEngineDescriptor Descriptor => inner.Descriptor;

        public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) => inner.Create(audioSampleRate: audioSampleRate, contentBytes: contentBytes, options: options, savePath: savePath);
        public IMachineRuntime CreateMachine(MachineCreationRequest request) => inner.CreateMachine(request: request);
    }
    private static string Undo(Twin twin, int count) {
        var rejected = string.Empty;

        twin.Server.EchoTap = echo => {
            if (echo.Rejected) {
                rejected = echo.Message;
            }
        };
        twin.Server.EnqueueUndo(count: count, principal: Principal.Console);
        twin.Step();
        twin.Server.EchoTap = null;
        return rejected;
    }

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
        ["world.undo/PastHorizon"] = new(
            Arrange: FillJournal,
            ArrangeLegal: FillJournal,
            Document: static () => JournalDepthLawTests.WithDepth(depth: UndoDepth),
            Legal: static twin => (Undo(count: 1, twin: twin).Length == 0),
            Operate: static twin => Undo(count: (UndoDepth + 1), twin: twin),
            OperationSteps: 1,
            Witness: static twin => $"journal={twin.Server.JournalLength}"
        ),
        ["machine.operation/WhileRecording"] = new(
            Arrange: static twin => {
                twin.Arm();
                GrantOperator(twin: twin);
            },
            ArrangeLegal: GrantOperator,
            Catalog: Operating,
            Document: WorldMachineOperationServerLawTests.Document,
            Legal: static twin => (OperateCabinet(twin: twin).Status != MachineOperationStatus.Refused),
            LegalCatalog: Operating,
            Operate: static twin => (OperateCabinet(twin: twin).Reason ?? string.Empty),
            Witness: Cabinet
        ),
        ["machine.operation/ProviderWithoutOperations"] = new(
            Arrange: GrantOperator,
            ArrangeLegal: GrantOperator,
            Catalog: Inert,
            Document: WorldMachineOperationServerLawTests.Document,
            Legal: static twin => (OperateCabinet(twin: twin).Status != MachineOperationStatus.Unsupported),
            LegalCatalog: Operating,
            Operate: static twin => (OperateCabinet(twin: twin).Reason ?? string.Empty),
            Witness: Cabinet
        ),
    };

    private static IEnumerable<string> ClassifiedIds() => RefusalCatalog.All().Where(predicate: static entry => entry.Unsupported).Select(selector: static entry => $"{entry.Door}/{entry.Id}");
    public static TheoryData<string> Classified() => [.. ClassifiedIds()];

    [Theory]
    [MemberData(nameof(Classified))]
    public void AnUnsupportedOperationChangesNothingBeforeItRefuses(string refusal) {
        Assert.True(condition: Arrangements.TryGetValue(key: refusal, value: out var arrangement), userMessage: $"{refusal} is classified as an intentionally unsupported operation and law 6 has no arrangement for it");

        var definition = arrangement.Document();
        using var a = new Twin(definition: definition, machineCatalog: arrangement.Catalog?.Invoke());
        using var b = new Twin(definition: definition, machineCatalog: arrangement.Catalog?.Invoke());

        arrangement.Arrange(obj: a);
        arrangement.Arrange(obj: b);

        var refused = arrangement.Operate(arg: a);

        for (var step = 0; (step < arrangement.OperationSteps); step++) {
            b.Step();
        }
        Assert.Contains(expectedSubstring: refusal[(refusal.IndexOf(value: '/') + 1)..], actualString: refused, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: b.State(), actual: a.State());
        Assert.Equal(expected: arrangement.Witness(arg: b), actual: arrangement.Witness(arg: a));
        for (var step = 0; (step < 2); step++) {
            a.Step();
            b.Step();
        }
        Assert.Equal(expected: b.State(), actual: a.State());
        Assert.Equal(expected: arrangement.Witness(arg: b), actual: arrangement.Witness(arg: a));

        // The control: the legal variant, on a twin arranged for it, moves the witness the refusal left alone.
        using var legal = new Twin(definition: definition, machineCatalog: (arrangement.LegalCatalog ?? arrangement.Catalog)?.Invoke());

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
