using System.Numerics;
using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>The session doors' noninterference laws, in the harness's manner: two worlds identical except for hidden
/// state, the same non-operator principal and inputs in both, and everything it receives compared byte for byte. A
/// write is judged by the writer's declared policy alone, so a session that observes nothing writes what its grants
/// name and learns nothing of a row withheld from it, not even whether one is declared; a query answers a session only
/// through the query door it would cross as a peer.</summary>
public sealed class SessionWriteDoorLawTests {
    private static readonly string[] Words = ["pub", "secret", "nothing", "k", "x", "0", "7"];
    private static readonly Dictionary<string, IReadOnlyList<string>> Slots = new() {
        ["row"] = ["pub", "secret", "nothing"],
        ["key"] = ["k", "x"],
        ["value"] = ["0", "7"],
        ["phase-row"] = ["secret", "nothing"],
        ["transform-json"] = [
            "{\"$type\":\"transfer\",\"from\":\"secret\",\"to\":\"pub\",\"selector\":\"Key\",\"key\":\"x\"}",
            "{\"$type\":\"transfer\",\"from\":\"nothing\",\"to\":\"pub\",\"selector\":\"Key\",\"key\":\"x\"}",
        ],
    };

    // A session admitted at Frames observes nothing, and its admission row mints no grant (the validator refuses one);
    // the operator grants it Mutate over the state section and Edit over 'pub' once it is admitted.
    private static WorldAdmissionEntry FramesWriter() => new(Disclosure: WorldDisclosureTier.Frames, Domain: "frames", Mode: WorldAdmissionTrustMode.FederatedAuthority, Grants: []);
    private static WorldDisclosureSession ObserveAsWriter(WorldFixture fixture, WorldDisclosureTranscript transcript, ulong seed) {
        var session = WorldDisclosureSession.Observe(seed: seed, server: fixture.Server, sourceAuthority: "frames", transcript: transcript);
        var principal = session.Principal!.Value;

        fixture.Server.Grant(actor: Principal.Console, grant: new WorldGrant(Budget: 64, Capability: WorldCapability.Mutate, Exclusive: false, Grantee: principal,
            KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.State), Subject: GrantSubject.Section(section: WorldSection.State)));
        fixture.Server.Grant(actor: Principal.Console, grant: new WorldGrant(Budget: 64, Capability: WorldCapability.Edit, Exclusive: false, Grantee: principal,
            Subject: GrantSubject.State(name: "pub")));
        return session;
    }
    // The pair differs only in whether 'secret', a row only seat 1 reads, is declared; it is declared first, so every
    // later row's ordinal moves with it too.
    private static WorldDefinition Document(bool secret) => Fixtures.BuildDocument() with {
        Admission = [FramesWriter()],
        StateRaw = new WorldStateSection(World: [
            .. (secret ? [new WorldStateRow(Name: CellName.Parse(candidate: "secret"), Kind: CellKind.Int, Visibility: new StateVisibility(Readers: ["seat1"]),
                Cells: [new StateCell(Key: CellName.Parse(candidate: "x"), Value: CellValue.Int(value: 9))])] : Array.Empty<WorldStateRow>()),
            new WorldStateRow(Name: CellName.Parse(candidate: "pub"), Kind: CellKind.Int, Capacity: 8,
                Cells: [new StateCell(Key: CellName.Parse(candidate: "a"), Value: CellValue.Int(value: 1))]),
        ]),
    };

    // One world of the pair: the Frames writer performs scripted writes into the public, the withheld and an absent row,
    // then a seeded draw of every session action over the same names; returns its transcript and the live 'pub' row.
    private static (WorldDisclosureTranscript Transcript, IReadOnlyList<StateCell> Pub) Run(bool secret) {
        using var fixture = Fixtures.FreshServer(definition: Document(secret: secret));
        var transcript = new WorldDisclosureTranscript(clock: () => fixture.Server.CompletedTick);
        var session = ObserveAsWriter(fixture: fixture, seed: 11UL, transcript: transcript);
        string[][] script = [
            ["set", "pub", "k", "7"], ["set", "secret", "x", "7"], ["set", "nothing", "x", "7"],
            ["remove", "secret", "x"], ["remove", "nothing", "x"], ["generate", "secret"], ["generate", "nothing"],
            ["guarded", "pub", "k", "0", "secret", "x", "9"], ["guarded", "pub", "k", "0", "nothing", "x", "9"],
            ["query.state", "secret"], ["query.where", "0"],
        ];
        var drawn = WorldDisclosureFuzzer.Draw(count: 48, from: 3UL, seed: 11UL, slots: Slots, until: 14UL, verbs: WorldDisclosureSession.Actions, words: Words);

        for (var tick = 1UL; (tick <= 16UL); tick++) {
            fixture.Step();
            if (tick == 1UL) {
                foreach (var words in script) { session.Act(words: words); }
            }
            foreach (var input in drawn.Where(predicate: input => (input.Tick == tick))) { session.Act(words: input.Words); }
        }
        session.Dispose();
        var pub = fixture.Server.ExportDefinition().State.Single(predicate: static row => (row.Name.Value == "pub")).Cells ?? [];
        return (transcript, pub);
    }

    [Fact]
    public void AFramesSessionIsAnsweredAlikeWhetherARowWithheldFromItIsDeclared() {
        var (declared, _) = Run(secret: true);
        var (absent, _) = Run(secret: false);

        Assert.Empty(collection: declared.Invalid);
        Assert.Empty(collection: absent.Invalid);
        Assert.Contains(declared.Events, entry => entry.Door.StartsWith(value: WorldDisclosureSession.OutcomeDoor, comparisonType: StringComparison.Ordinal));
        Assert.Empty(collection: WorldDisclosureComparison.Compare(left: declared, right: absent));
    }
    // The positive control: the same session's write into the row its Edit grant names lands, though it observes nothing.
    [Fact]
    public void AFramesSessionWritesTheRowItsEditGrantNames() {
        using var fixture = Fixtures.FreshServer(definition: Document(secret: true));
        var transcript = new WorldDisclosureTranscript(clock: () => fixture.Server.CompletedTick);
        var session = ObserveAsWriter(fixture: fixture, seed: 5UL, transcript: transcript);

        fixture.Step();
        // It reads nothing: its query door refuses it.
        Assert.True(condition: fixture.Server.AnswerSubmittedQuery(principal: session.Principal!.Value, query: new WorldQuery.StateObservations()).Refused);
        session.Act(words: ["set", "pub", "k", "7"]);
        fixture.Step();
        var pub = fixture.Server.ExportDefinition().State.Single(predicate: static row => (row.Name.Value == "pub")).Cells ?? [];
        session.Dispose();

        Assert.Contains(pub, cell => (cell.Key.Value == "k") && (cell.Value == CellValue.Int(value: 7)));
    }

    // A guard over the whole document is admitted or refused by whether any declaration withholds from its proposer, so
    // a recipient's is refused by its shape and named as the section, whatever the document declares and in what order.
    [Fact]
    public void AWholeDocumentGuardIsRefusedAlikeWhateverTheDocumentWithholds() {
        var outcomes = new List<string>();
        foreach (var first in new[] { "alpha", "beta", null }) {
            WorldStateRow[] rows = [
                .. ((first is { } name) ? [new WorldStateRow(Name: CellName.Parse(candidate: name), Kind: CellKind.Int, Visibility: new StateVisibility(Readers: [])) ] : Array.Empty<WorldStateRow>()),
                new WorldStateRow(Name: CellName.Parse(candidate: "pub"), Kind: CellKind.Int, Cells: [new StateCell(StateRow.SlotKey, CellValue.Int(value: 0))]),
            ];
            using var fixture = Fixtures.FreshServer(Fixtures.BuildDocument().WithWorldState(rows));
            var seat = Principal.Seat(slot: 1);
            var outcome = fixture.SubmitMutation(new WorldMutation.Batch(seat,
                [new WorldMutation.UpsertStateCell(seat, "pub", StateRow.SlotKey.Value, 1, WorldDocumentWriteKind.Set)],
                ExpectedDefinition: new string('0', 64)));
            outcomes.Add($"{outcome.Code}:{outcome.Detail}");
        }
        Assert.Single(collection: outcomes.Distinct());
        Assert.Equal($"{WorldRecipientMutations.NotDisclosedCode}:'state' is not disclosed to seat2", outcomes[0]);
    }

    // A local query names whom it answers: the same link answers the operator with a body's pose and a Frames session
    // with the query door's refusal, alike whatever the pose. body.where and its siblings ask through this one door as
    // their caller.
    [Fact]
    public void ALocalQueryAnswersAFramesSessionAlikeWhateverTheBodyIsDoing() {
        var answers = new List<(string Operator, string Session)>();
        foreach (var x in new[] { 2f, 5f }) {
            using var fixture = Fixtures.FreshServer(definition: Document(secret: false));
            var transcript = new WorldDisclosureTranscript(clock: () => fixture.Server.CompletedTick);
            var session = ObserveAsWriter(fixture: fixture, seed: 3UL, transcript: transcript);
            _ = fixture.JoinSeat();
            fixture.Server.ApplyCommand(new WorldCommand.SnapPose(Principal.Seat(slot: 0), 0, new Vector3(x: x, y: 0, z: 3), 0, 0, 0, SnapPoseMode.Pose));
            fixture.Step();
            var link = new LoopbackTransport(server: fixture.Server);
            string Ask(Principal principal) {
                var text = "unanswered";
                link.Query(completion: answer => text = $"refused={answer.Refused} {answer.Text}", principal: principal, query: new WorldQuery.PlayerWhere(Index: 0));
                return text;
            }
            answers.Add((Ask(Principal.Console), Ask(session.Principal!.Value)));
            session.Dispose();
        }
        Assert.NotEqual(answers[0].Operator, answers[1].Operator);
        Assert.Equal(answers[0].Session, answers[1].Session);
        Assert.StartsWith("refused=True", answers[0].Session, StringComparison.Ordinal);
    }
}
