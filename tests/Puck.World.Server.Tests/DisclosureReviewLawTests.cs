using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class DisclosureReviewLawTests {
    private static WorldDefinition PrivateDice() => Fixtures.BuildDocument().WithWorldState([
        new WorldStateRow(CellName.Parse("dice"), CellKind.Int,
            Draw: new Draw(Generator: new StateGenerator(Source: GeneratorSource.StreamDraw), Timing: DrawTiming.Event, PrivateStream: true))
    ]) with { Rules = [] };

    [Fact]
    public void SearchCannotSampleAnInstalledPrivateSite() {
        var keys = WorldPrivateDrawKeys.Create();
        using var fixture = Fixtures.FreshServer(PrivateDice(), drawKeys: keys);
        var arena = fixture.Server.Arena;
        Assert.True(arena.Catalog.TryResolve(StateLane.Document, "dice", out var row));
        var search = new ArenaSearchEffectHost(arena);
        var reads = keys.Reads;
        Assert.Null(arena.Rows[row.Ordinal].Draw!.Secret!.Open(WorldPrivateDrawKeys.Create()));
        Assert.False(search.Apply(Mutation.Generate(row.Ordinal), out var refusal));
        Assert.Contains("sampler", refusal.Reason);
        Assert.Equal(reads, keys.Reads);
        Assert.True(Apply(fixture.Server, new WorldMutation.Generate(Principal.Console, "dice")));
        Assert.True(keys.Reads > reads);
    }

    [Fact]
    public void ARefusedSavepointCannotRevealTheNextLiveWords() {
        var keys = WorldPrivateDrawKeys.Create();
        using var fixture = Fixtures.FreshServer(PrivateDice(), drawKeys: keys);
        var server = fixture.Server;
        long[] Sample() {
            var draw = server.Arena.Rows[0].Draw!;
            Assert.True(GeneratorEngine.DrawStream.TryOpen(draw.Generator!, CellKind.Int, new(11, 12), 0, null, out var stream, out var reason, draw.Secret!.Open(keys)), reason);
            return Enumerable.Range(0, 4).Select(_ => {
                Assert.True(stream.TryNext(out var word, out var why), why);
                return word;
            }).ToArray();
        }
        var outer = server.BeginStateTransaction();
        var inner = server.BeginStateTransaction();
        var refused = Sample();
        server.RewindStateTransaction(inner);
        var afterInner = Sample();
        server.RewindStateTransaction(outer);
        Assert.NotEqual(refused, afterInner);
        Assert.NotEqual(afterInner, Sample());
    }

    [Fact]
    public void HostedReloadDeltaCarriesTheKeyedInputAndInstallsWithLiveWords() {
        var keys = WorldPrivateDrawKeys.Create();
        using var fixture = Fixtures.FreshServer(Fixtures.BuildDocument() with { Rules = [] }, drawKeys: keys);
        var server = fixture.Server;
        var tray = PrivateDice().AuthoredState[0] with {
            Capacity = 2,
            Cells = [new(CellName.Parse("left"), CellValue.Int(0)), new(CellName.Parse("right"), CellValue.Int(0))]
        };
        var target = server.Definition.WithWorldState([..server.Definition.AuthoredState, tray]);
        Assert.True(WorldDefinitionLoader.TryDraw(target, "store", server.InstanceIdentity, out var preview, out var reason, WorldDrawSampler.Preview), reason);
        var delta = WorldReloadDelta.Create(server.Definition, server.DocumentVersion, preview);
        Assert.True(delta.TryPrepare(server.Definition, server.DocumentVersion, out var candidate, out reason), reason);
        Assert.Equal(0, candidate.AuthoredState.Single(r => r.Name == tray.Name).DrawCursor);
        var before = keys.Reads;
        var origin = new WorldRebuildOrigin.Store(Guid.NewGuid(), SafeName.Parse("table"));
        new LoopbackTransport(server).SubmitRebuild(new(WorldRebuildKind.Reload, null, origin, false, delta.TargetHash, delta), Principal.Console);
        fixture.Step();
        var live = server.ExportDefinition().AuthoredState.Single(r => r.Name == tray.Name);
        Assert.Equal(2, live.DrawCursor);
        Assert.Equal(2, keys.Reads - before);
        Assert.NotEqual(preview.AuthoredState.Last().Cells, live.Cells);
    }

    [Fact]
    public void ResetAndUndoRetirePrivateFutures() {
        var keys = WorldPrivateDrawKeys.Create();
        using var fixture = Fixtures.FreshServer(PrivateDice(), drawKeys: keys);
        var server = fixture.Server;
        var generate = new WorldMutation.Generate(Principal.Console, "dice");
        Assert.True(Apply(server, generate));
        // A rule can move a site between two edits in the document journal.
        Assert.True(server.Arena.TryWriteDrawCursor(0, 6));
        Assert.True(Apply(server, generate));
        var before = server.ExportDefinition().AuthoredState[0];
        Assert.True(Apply(server, new WorldMutation.UpsertStateCell(Principal.Console, "dice", StateRow.SlotKey.Value, 17, WorldDocumentWriteKind.Set)));
        server.EnqueueUndo(1, Principal.Console);
        fixture.Step();
        var restored = server.ExportDefinition().AuthoredState[0];
        Assert.Equal(before.Cells![0].Value, restored.Cells![0].Value);
        Assert.Equal(before.DrawCursor, restored.DrawCursor);
        Assert.True(keys.Generation > 0);
        Assert.True(Apply(server, new WorldMutation.RemoveStateRow(Principal.Console, "dice")));
        server.EnqueueUndo(1, Principal.Console);
        fixture.Step();
        Assert.Equal(before.Cells![0].Value, server.ExportDefinition().AuthoredState[0].Cells![0].Value);
        var generation = keys.Generation;
        server.EnqueueRebuild(new WorldRebuildRequest(WorldRebuildKind.Reset, null, null, true), Principal.Console);
        fixture.Step();
        Assert.True(keys.Generation > generation);
        Assert.Same(server.Definition.AuthoredState[0].Draw!.Secret, server.Arena.Rows[0].Draw!.Secret);
        Assert.Equal(1, server.ExportDefinition().AuthoredState[0].DrawCursor);
    }

    [Fact]
    public void SavedDocumentMovesAndResumesWithFreshKeys() {
        using var files = new Puck.Testing.TemporaryDirectory();
        using var fixture = Fixtures.FreshServer(PrivateDice(), drawKeys: WorldPrivateDrawKeys.Create());
        var generate = new WorldMutation.Generate(Principal.Console, "dice");
        Assert.True(Apply(fixture.Server, generate));
        var saved = fixture.Server.ExportDefinition();
        var original = files.PathOf("table.world.json");
        var moved = files.PathOf("moved.world.json");
        WorldDefinitionSerialization.Save(saved, original);
        File.Move(original, moved);
        var keys = WorldPrivateDrawKeys.Create();
        Assert.True(WorldDefinitionLoader.TryLoadFile(moved, out var loaded, out var reason, keys: keys), reason);
        using var resumed = Fixtures.FreshServer(loaded, drawKeys: keys);
        Assert.Equal(saved.AuthoredState[0].Cells![0].Value, resumed.Server.ExportDefinition().AuthoredState[0].Cells![0].Value);
        Assert.True(Apply(resumed.Server, generate));
        Assert.True(Apply(fixture.Server, generate));
        var next = resumed.Server.ExportDefinition().AuthoredState[0];
        Assert.Equal(saved.AuthoredState[0].DrawCursor + 1, next.DrawCursor);
        Assert.NotEqual(fixture.Server.ExportDefinition().AuthoredState[0].Cells![0].Value, next.Cells![0].Value);
    }

    [Fact]
    public void PrivateDrawEditSaveAndReloadResumeTheAuthorityStream() {
        var row = new WorldStateRow(CellName.Parse("dice"), CellKind.Int,
            Draw: new Draw(Generator: new StateGenerator(Source: GeneratorSource.StreamDraw), Timing: DrawTiming.Event, PrivateStream: true));
        var source = Fixtures.BuildDocument().WithWorldState([row]) with { Rules = [], Generation = new WorldGenerationDefaults(42) };
        Assert.True(WorldDefinitionLoader.TryDraw(source, "law", "boot", out var drawn, out var reason, WorldPrivateDrawKeys.Preview), reason);
        Assert.True(WorldDefinitionLoader.TryDraw(source, "law", "boot", out var repeat, out reason, WorldPrivateDrawKeys.Preview), reason);
        Assert.Equal(drawn.AuthoredState[0].Cells![0].Value, repeat.AuthoredState[0].Cells![0].Value);
        using var fixture = Fixtures.FreshServer(drawn);
        var server = fixture.Server;
        var visible = View(server, Principal.Console).Definition.AuthoredState[0];
        Assert.Null(visible.Draw!.Secret);
        var key = server.ExportDefinition().AuthoredState[0].Draw!.Secret;
        Assert.NotNull(key);
        Assert.True(Apply(server, new WorldMutation.UpsertStateRow(Principal.Console, drawn.AuthoredState[0])));
        Assert.True(Apply(server, new WorldMutation.UpsertStateRow(Principal.Console, visible with { Max = long.MaxValue })));
        Assert.Equal(key, server.ExportDefinition().AuthoredState[0].Draw!.Secret);
        var generate = new WorldMutation.Generate(Principal.Console, "dice");
        Assert.True(Apply(server, generate));
        var saved = server.ExportDefinition();
        Assert.True(WorldDefinitionLoader.TryLoadForAdmission(WorldDefinitionSerialization.Serialize(saved), "save", out var loaded, out reason, keys: WorldPrivateDrawKeys.Preview), reason);
        using var resumed = Fixtures.FreshServer(loaded!.Definition);
        for (var i = 0; i < 3; i++) {
            Assert.True(Apply(server, generate));
            Assert.True(Apply(resumed.Server, generate));
            var expected = server.ExportDefinition().AuthoredState[0];
            var actual = resumed.Server.ExportDefinition().AuthoredState[0];
            Assert.Equal(expected.DrawCursor, actual.DrawCursor);
            Assert.Equal(expected.Cells![0].Value, actual.Cells![0].Value);
        }
        Assert.DoesNotContain("\"secret\"", System.Text.Encoding.UTF8.GetString(WorldDefinitionSerialization.Serialize(saved)));
        var replacement = visible with { Draw = visible.Draw with { Timing = DrawTiming.TickPeriod } };
        Assert.True(Apply(server, new WorldMutation.UpsertStateRow(Principal.Console, replacement)));
        Assert.NotEqual(key, server.ExportDefinition().AuthoredState[0].Draw!.Secret);
        Assert.True(Apply(server, generate));
    }

    [Fact]
    public void ARemoteRebuildAndRowEditsRetainTheLiveStreamAndPosition() {
        var name = CellName.Parse("dice");
        var declared = new WorldStateRow(name, CellKind.Int,
            Draw: new Draw(Generator: new StateGenerator(Source: GeneratorSource.StreamDraw), Timing: DrawTiming.Event, PrivateStream: true));
        var document = Fixtures.BuildDocument().WithWorldState([declared]) with { Rules = [] };
        using var fixture = Fixtures.FreshServer(document);
        var server = fixture.Server;
        Assert.True(Apply(server, new WorldMutation.Generate(Principal.Console, "dice")));
        var before = server.ExportDefinition().AuthoredState[0];
        Assert.True(WorldDefinitionLoader.TryDraw(document, "foreign", "boot", out var foreign, out var reason, WorldPrivateDrawKeys.Create()), reason);
        var authored = System.Text.Json.Nodes.JsonNode.Parse(WorldDefinitionSerialization.Serialize(foreign))!;
        authored["state"]!["world"]![0]!["draw"]!["secret"] = "author-supplied";
        Assert.Throws<InvalidDataException>(() => WorldDefinitionSerialization.Deserialize(System.Text.Encoding.UTF8.GetBytes(authored.ToJsonString())));
        var request = new WorldRebuildRequest(WorldRebuildKind.Load, Definition: foreign,
            Origin: new WorldRebuildOrigin.File(Path.GetFullPath("private.world.json")), Force: true, ContentHash: "pin");
        Assert.True(WorldSubmissionCodec.TryEncodeRebuild(request, out var wire, out _));
        Assert.True(WorldSubmissionCodec.TryDecodeRebuild(wire, out var received, out _));
        Assert.Null(received!.Definition!.AuthoredState[0].Draw!.Secret);
        server.EnqueueRebuild(received, Principal.Console);
        fixture.Step();
        Assert.Equal(WorldBaseOriginKind.Load, server.Document.BaseOrigin.Kind);
        var installed = server.ExportDefinition().AuthoredState[0];
        Assert.Same(before.Draw!.Secret, installed.Draw!.Secret);
        Assert.True(installed.DrawCursor >= before.DrawCursor);
        Assert.True(Apply(server, new WorldMutation.UpsertStateRow(Principal.Console, declared with { DrawCursor = 0, DrawnMasks = [new(99)] })));
        Assert.Equal(installed.DrawCursor, server.ExportDefinition().AuthoredState[0].DrawCursor);
        Assert.Equal(installed.DrawnMasks, server.ExportDefinition().AuthoredState[0].DrawnMasks);
        Assert.True(Apply(server, new WorldMutation.RemoveStateRow(Principal.Console, name.Value)));
        Assert.True(Apply(server, new WorldMutation.UpsertStateRow(Principal.Console, declared)));
        Assert.NotEqual(installed.Draw.Secret!.Installation, server.ExportDefinition().AuthoredState[0].Draw!.Secret!.Installation);
    }

    [Fact]
    public void AuthorityCheckpointAndKeylessTapeReproduceInSeparateStores() {
        var row = new WorldStateRow(CellName.Parse("dice"), CellKind.Int,
            Draw: new Draw(Generator: new StateGenerator(Source: GeneratorSource.StreamDraw), Timing: DrawTiming.Event, PrivateStream: true));
        var definition = Fixtures.BuildDocument().WithWorldState([row]) with { Rules = [] };
        using var first = Fixtures.FreshServer(definition, drawKeys: WorldPrivateDrawKeys.Create());
        var secondKeys = WorldPrivateDrawKeys.Create();
        using var second = Fixtures.FreshServer(definition, drawKeys: secondKeys);
        Assert.True(first.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var start, out var reason), reason);
        var bytes = WorldAuthorityCheckpointCodec.Encode(start!);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(bytes, out var checkpoint, out reason), reason);
        first.Server.RestoreCheckpoint(checkpoint!);
        second.Server.RestoreCheckpoint(checkpoint!);
        var generate = new WorldMutation.Generate(Principal.Console, "dice");
        using var roots = new Puck.Testing.TemporaryDirectory();
        WorldReplayTape Tape(WorldServer server, string directory) => new(server, server.Profiles,
            new LoopbackTransport(server), [], Fixtures.MachineHostFactory, static (_, _) => new NullAddonHost(), new WorldStateRoot(directory));
        var recording = Tape(first.Server, roots.PathOf("first"));
        var verify = Tape(second.Server, roots.PathOf("second"));
        Assert.True(recording.TryBeginRecording("draw", out reason), reason);
        for (var i = 0; i < 3; i++) {
            foreach (var server in new[] { first.Server, second.Server }) {
                server.Submit(new SubmissionEnvelope(SubmissionEnvelope.LocalConnectionId, 0, i + 1, i + 1, Principal.Console,
                    new WorldSubmissionPayload.Mutation(generate), Guid.NewGuid()), _ => { });
            }
            first.Step();
            second.Step();
            recording.NoteTick();
            if (i == 1) {
                first.Server.EnqueueRebuild(new WorldRebuildRequest(WorldRebuildKind.Reset, null, null, true), Principal.Console);
                first.Step();
                recording.NoteTick();
            }
        }
        var stopped = recording.StopRecording();
        Assert.True(stopped.Verdict!.Passing, stopped.Verdict.Describe());
        var tapeBytes = File.ReadAllBytes(stopped.Path);
        Assert.DoesNotContain("\"secret\"", System.Text.Encoding.UTF8.GetString(WorldStateExport.ToCanonicalJson(first.Server)));
        var destination = verify.PathFor("draw");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(stopped.Path, destination);
        Assert.True(verify.Verify("draw").Passing);
        using var input = new MemoryStream(tapeBytes);
        var incomplete = WorldReplaySnapshot.Read(input);
        var ticks = Assert.IsType<WorldReplayTickInput[]>(incomplete.Ticks);
        ticks[0] = ticks[0] with { Authority = [] };
        WorldReplaySnapshot.WriteFile(verify.PathFor("incomplete"), incomplete);
        var generationBeforeDrive = secondKeys.Generation;
        Assert.True(verify.TryBeginDrive("incomplete", null, null, null, out reason), reason);
        Assert.True(secondKeys.Generation > generationBeforeDrive);
        var missing = Assert.Throws<InvalidDataException>(() => verify.DriveStep(() => Apply(second.Server, generate)));
        Assert.Contains("MissingPrivateDraw", missing.Message);
        verify.CancelDrive();
        Assert.True(Apply(second.Server, generate));
    }

    [Fact]
    public void ProgrammaticTextKeepsItsQuotes() {
        var row = new WorldStateRow(CellName.Parse("caption"), CellKind.Text);
        using var fixture = Fixtures.FreshServer(Fixtures.BuildDocument().WithWorldState([row]) with { Rules = [] });
        Assert.True(Apply(fixture.Server, new WorldMutation.UpsertStateCell(Principal.Console, "caption", StateRow.SlotKey.Value,
            0, WorldDocumentWriteKind.Set, RawToken: "\"x\"")));
        Assert.Equal("\"x\"", fixture.Server.ExportDefinition().AuthoredState[0].Cells![0].Value.AsText);
    }

    [Fact]
    public void QuotedNumericAddUsesTheCommandOperandBoundaries() {
        var row = new WorldStateRow(CellName.Parse("score"), CellKind.Int, Cells: [new StateCell(StateRow.SlotKey, CellValue.Int(7))]);
        using var fixture = Fixtures.FreshServer(Fixtures.BuildDocument().WithWorldState([row]) with { Rules = [] });
        var mutation = new WorldMutation.UpsertStateCell(Principal.Console, "score", StateRow.SlotKey.Value, 0, WorldDocumentWriteKind.Set,
            CommandTail: "\"5\" add");
        Assert.True(Apply(fixture.Server, mutation));
        Assert.Equal(12, fixture.Server.ExportDefinition().AuthoredState[0].Cells![0].Value.Raw);
    }

    private static bool Apply(WorldServer server, WorldMutation mutation) =>
        server.TryApplyMutation(mutation, server.CompletedTick, server.CompletedEngineTicks, SubmissionEnvelope.LocalConnectionId, 0, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplicaDrawsNeverCarryTheAuthoritySecret(bool restricted) {
        var definition = Fixtures.BuildDocument() with {
            Rules = [],
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                CellName.Parse("dice"), CellKind.Int,
                Draw: new Draw(Generator: new StateGenerator(Source: GeneratorSource.StreamDraw),
                    Timing: DrawTiming.Event, PrivateStream: true),
                Visibility: restricted ? new StateVisibility(Readers: ["seat1"]) : null)])
        };
        using var fixture = Fixtures.FreshServer(definition);
        var replica = View(fixture.Server, Principal.Seat(1));
        var observed = Assert.Single(replica.Definition.AuthoredState).Draw!;
        Assert.True(observed.PrivateStream);
        Assert.Null(observed.Secret);
        var operated = new WorldRecipientView(() => fixture.Server.Definition, () => fixture.Server.Arena,
            Principal.Seat(1), WorldDisclosureTier.Replica, () => true, new WorldDisclosureImages(),
            fixture.Server.AuthorityIdentity, () => fixture.Server.DeliveryTime, () => true);
        Assert.Null(Assert.Single(operated.Definition.AuthoredState).Draw!.Secret);
        Assert.Null(Assert.Single(WorldStateDisclosure.Disclose(
            fixture.Server.Definition, fixture.Server.Arena, Principal.Seat(1), unrestricted: true)
            .Definition.AuthoredState).Draw!.Secret);
        Assert.NotNull(Assert.Single(fixture.Server.ExportDefinition().AuthoredState).Draw!.Secret);
    }

    private static WorldRecipientView View(WorldServer server, Principal recipient, WorldDisclosureTier tier = WorldDisclosureTier.Replica) =>
        new(() => server.Definition, () => server.Arena, recipient, tier, () => true, new WorldDisclosureImages(),
            server.AuthorityIdentity, () => server.DeliveryTime, () => server.Grants.IsOperator(recipient));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawCompiledDiagnosticQueriesRequireAuthority(bool interaction) {
        using var fixture = Fixtures.FreshServer(Fixtures.BuildDocument());
        WorldQuery query = interaction ? new WorldQuery.Interactions() : new WorldQuery.Rules();
        Assert.True(fixture.Server.AnswerSubmittedQuery(query, Principal.Seat(0)).Refused);
        Assert.False(fixture.Server.AnswerSubmittedQuery(query, Principal.Console).Refused);
    }

    [Fact]
    public void PropertyQueriesReadTheCallersLiveView() {
        var definition = Fixtures.BuildDocument() with {
            Properties = new WorldPropertyRegistrySection(Names: ["secretTag", "publicTag"]),
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(CellName.Parse("secretTag"), CellKind.Int, Domain: StateDomain.Keys.Instance,
                    Visibility: new StateVisibility(Readers: ["seat1"]), Cells: [new StateCell(CellName.Parse("0"), CellValue.Int(1))]),
                new WorldStateRow(CellName.Parse("publicTag"), CellKind.Int, Domain: StateDomain.Keys.Instance,
                    CellVisibility: true, Visibility: new StateVisibility(), Cells: [new StateCell(CellName.Parse("0"), CellValue.Int(1))])])
        };
        using var fixture = Fixtures.FreshServer(definition);
        var host = fixture.Server.RuleHost;
        var query = new WorldQuery.Properties(BodyIndex: 0);
        Assert.Contains("secretTag", host.AnswerSubmittedQuery(query, Principal.Console).Text);
        Assert.Contains("secretTag", host.AnswerSubmittedQuery(query, Principal.Seat(0)).Text);
        var answer = host.AnswerSubmittedQuery(query, Principal.Seat(1));
        Assert.Contains("publicTag", answer.Text);
        Assert.DoesNotContain("secretTag", answer.Text);
        Assert.True(fixture.Server.Arena.Catalog.TryResolve(StateLane.Document, "publicTag", out var row));
        var key = fixture.Server.Arena.Keys.Intern(CellName.Parse("0"));
        Assert.True(fixture.Server.Arena.TryWriteVisibility(row.Ordinal, key, new StateVisibility(Readers: ["seat1"])));
        Assert.DoesNotContain("publicTag", host.AnswerSubmittedQuery(query, Principal.Seat(1)).Text);
        Assert.Contains("publicTag", host.AnswerSubmittedQuery(query, Principal.Console).Text);
        Assert.True(host.AnswerSubmittedQuery(query, Principal.Session(1, 1)).Refused);
    }

    [Fact]
    public void ReplicaDefinitionsUseTheSameFieldProjectionAsPresentation() {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(
                Lattices: [new WorldFieldTopology(Name: "region", Origin: new(0, 0, 0), CellSize: 1, Width: 2, Depth: 1)],
                World: [
                    new WorldStateRow(CellName.Parse("privateField"), CellKind.Fixed, Domain: new StateDomain.CellsOf("region"),
                        Field: new WorldStateFieldTrait(Initial: 0.75f), Visibility: new StateVisibility(Readers: ["seat1"])),
                    new WorldStateRow(CellName.Parse("publicField"), CellKind.Fixed, Domain: new StateDomain.CellsOf("region"),
                        Field: new WorldStateFieldTrait(Initial: 0.25f), Visibility: new StateVisibility(ReadersFrom: "readers")),
                    new WorldStateRow(CellName.Parse("readers"), CellKind.Text, Domain: StateDomain.Keys.Instance,
                        Cells: [new StateCell(CellName.Parse("viewer"), CellValue.Text("seat2"))])])
        };
        using var fixture = Fixtures.FreshServer(definition);
        var server = fixture.Server;
        var replica = View(server, Principal.Seat(1));
        void Check(WorldDefinition disclosed) {
            Assert.Equal("publicField", Assert.Single(Assert.Single(disclosed.FieldRegions).Fields).Name);
            Assert.Null(disclosed.AuthoredState.FirstOrDefault(row => row.Name.Value == "privateField")?.Field);
        }
        Check(replica.Definition);
        Assert.Equal(server.Definition.AuthoredState.Select(row => row.Name), replica.Definition.AuthoredState.Select(row => row.Name));
        Check(WorldStateDisclosure.Disclose(server.Definition, server.Arena, Principal.Seat(1)).Definition);
        Check(View(server, Principal.Seat(1), WorldDisclosureTier.Presentation).Definition);
        Assert.Equal(2, Assert.Single(View(server, Principal.Console).Definition.FieldRegions).Fields.Count);
        Assert.True(server.Arena.Catalog.TryResolve(StateLane.Document, "readers", out var row));
        var key = server.Arena.Keys.Intern(CellName.Parse("viewer"));
        Assert.True(server.Arena.TryWrite(row.Ordinal, key, CellValue.Text("seat1"), out var reason), reason);
        Assert.Empty(replica.Definition.FieldRegions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PresentationPreservesAnEmptyRowsStorageShape(bool hiddenCell) {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(CellName.Parse("keys"), CellKind.Int, Domain: StateDomain.Keys.Instance, Capacity: 8,
                    CellVisibility: true, Visibility: new StateVisibility(), Cells: hiddenCell ? [new StateCell(CellName.Parse("secret"), CellValue.Int(9),
                        Visibility: new StateVisibility(Readers: ["seat1"]))] : []),
                new WorldStateRow(CellName.Parse("slot"), CellKind.Int, Domain: StateDomain.Slot.Instance,
                    CellVisibility: true, Visibility: new StateVisibility(), Cells: [])])
        };
        using var fixture = Fixtures.FreshServer(definition);
        var view = View(fixture.Server, Principal.Seat(1), WorldDisclosureTier.Presentation);
        var hydrated = view.Definition;
        var keyed = Assert.Single(hydrated.AuthoredState, row => row.Name.Value == "keys");
        Assert.IsType<StateDomain.Keys>(keyed.Domain);
        Assert.Equal(8, keyed.Capacity);
        Assert.Empty(keyed.Cells!);
        Assert.IsType<StateDomain.Slot>(Assert.Single(hydrated.AuthoredState, row => row.Name.Value == "slot").Domain);
    }
}
