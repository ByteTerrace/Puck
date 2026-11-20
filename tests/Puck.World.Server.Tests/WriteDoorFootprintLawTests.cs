using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>The write door's footprint, enumerated from the code rather than from a hand-built corpus: every state name
/// position the registry reaches in every value a writer submits (<see cref="WriteDoorProbes"/>), every mutation
/// kind's classification, and every installed value a write replaces, keyed and whole, are each probed against worlds
/// where the probe row is withheld, holds other cells, or is absent, with the probe row public as the control. A
/// position, kind or section the code gains is probed without a list of its own, and one the door misses fails.</summary>
public sealed class WriteDoorFootprintLawTests {
    private const string Probe = "vault";
    private static readonly Principal Writer = Principal.Seat(slot: 1);

    private enum Vault : byte { Absent, Withheld, WithheldOther, Public }

    // The door's answer in each world: refused naming a row, or admitted.
    private static string Answer(WorldDefinition document, WorldMutation mutation, IMachineValidationCatalog? machines = null) =>
        WorldRecipientView.ForComposition(document, Writer, machines: machines).Admits(mutation, out var refused) ? "admitted" : $"refused {refused}";
    private static IReadOnlyDictionary<Vault, string> Answers(Func<Vault, WorldDefinition> document, Func<WorldMutation> mutation, IMachineValidationCatalog? machines = null) =>
        Enum.GetValues<Vault>().ToDictionary(vault => vault, vault => Answer(document(vault), mutation(), machines));
    private static WorldDefinition Base(Vault vault) => WriteDoorNoninterferenceLawTests.Document((WriteDoorNoninterferenceLawTests.Vault)(byte)vault);

    [Fact]
    public void EveryStatePositionOfEverySubmittedValueIsRefusedAlikeWhetherItsRowIsWithheldOrAbsent() {
        var documents = Enum.GetValues<Vault>().ToDictionary(vault => vault, Base);
        var failures = new List<string>();
        var probes = 0;
        var admittedWhenPublic = 0;
        var refusedWhenPublic = new List<string>();
        foreach (var (kind, member, model, list) in WriteDoorProbes.SubmittedModels()) {
            foreach (var position in WriteDoorProbes.StatePositions(model)) {
                var value = WriteDoorProbes.Build(model, position, Probe, out var reason);
                var site = $"{kind.Name}.{member.Name}: {position}";
                if (value is null) { failures.Add($"{site}: no probe can be spelled there ({reason})"); continue; }
                probes++;
                var mutation = WriteDoorProbes.Submit(kind, member, value, list, Writer);
                var withheld = Answer(documents[Vault.Withheld], mutation);
                if (withheld != $"refused {Probe}") { failures.Add($"{site}: {withheld} where the row is withheld"); }
                foreach (var vault in new[] { Vault.Absent, Vault.WithheldOther }) {
                    if (Answer(documents[vault], mutation) is var other && other != withheld) { failures.Add($"{site}: {other} where the row is {vault}, {withheld} where withheld"); }
                }
                if (Answer(documents[Vault.Public], mutation) != $"refused {Probe}") { admittedWhenPublic++; }
                // A refusal naming whatever public row the position spells is the derived footprint's (a transform that does
                // not resolve), never the probe row's policy.
                else if (WriteDoorProbes.Build(model, position, WriteDoorProbes.Filler, out _) is { } control &&
                    Answer(documents[Vault.Public], WriteDoorProbes.Submit(kind, member, control, list, Writer)) == $"refused {WriteDoorProbes.Filler}") { admittedWhenPublic++; }
                else { refusedWhenPublic.Add(site); }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(probes > 1000, $"only {probes} positions were probed; the registry walk no longer reaches the submitted values");
        // The control: a writer who reads the probe row is not refused by the probe's name.
        Assert.True(admittedWhenPublic == probes, $"{probes - admittedWhenPublic} of {probes} probes are refused by the probe's name though the row is public: {string.Join("; ", refusedWhenPublic)}");
    }

    // Every kind outside a program section either has the door resolve the installed value it replaces or names where
    // its footprint is judged, and no kind does both.
    [Fact]
    public void EveryMutationKindIsClassifiedByTheFootprintItReplaces() {
        var unclassified = new List<string>();
        foreach (var kind in typeof(WorldMutation).GetNestedTypes().Where(kind => typeof(WorldMutation).IsAssignableFrom(kind) && !kind.IsAbstract)) {
            if (WriteDoorProbes.SectionOf(kind).IsProgram()) { continue; }
            var resolves = WorldRecipientMutations.ResolvesReplaced(kind);
            var elsewhere = WorldRecipientMutations.JudgedElsewhere.ContainsKey(kind);
            if (resolves == elsewhere) { unclassified.Add($"{kind.Name} ({(resolves ? "both" : "neither")})"); }
        }
        Assert.True(unclassified.Count == 0, string.Join(", ", unclassified));
    }

    // Where each section a write replaces lives in the document, by its row type: a keyed section's rows, or a section
    // replaced whole. A kind the door resolves must find its section here, so a new one is probed when it is written.
    private static readonly Dictionary<Type, Func<WorldDefinition, Array, WorldDefinition>> KeyedSections = new() {
        [typeof(WorldMachine)] = (document, rows) => document with { MachinesRaw = [.. rows.Cast<WorldMachine>()] },
        [typeof(WorldKit)] = (document, rows) => document with { KitRowsRaw = [.. rows.Cast<WorldKit>()] },
        [typeof(WorldPrototype)] = (document, rows) => document with { CreationsRaw = [.. document.Creations, .. rows.Cast<WorldPrototype>()] },
        [typeof(WorldAddonRow)] = (document, rows) => document with { AddonsRaw = [.. rows.Cast<WorldAddonRow>()] },
        [typeof(WorldCamera)] = (document, rows) => document with { CamerasRaw = [.. rows.Cast<WorldCamera>()] },
        [typeof(WorldScreen)] = (document, rows) => document with { ScreensRaw = [.. rows.Cast<WorldScreen>()] },
        [typeof(WorldSpeaker)] = (document, rows) => document with { SpeakersRaw = [.. rows.Cast<WorldSpeaker>()] },
        [typeof(WorldTune)] = (document, rows) => document with { TunesRaw = [.. rows.Cast<WorldTune>()] },
        [typeof(WorldPatch)] = (document, rows) => document with { PatchesRaw = [.. rows.Cast<WorldPatch>()] },
        [typeof(WorldLook)] = (document, rows) => document with { LookRowsRaw = [.. rows.Cast<WorldLook>()] },
        [typeof(WorldBindingOverlay)] = (document, rows) => document with { BindingOverlaysRaw = [.. rows.Cast<WorldBindingOverlay>()] },
        [typeof(WorldViewLayout)] = (document, rows) => document with { ViewsRaw = document.Views with { Layouts = [.. rows.Cast<WorldViewLayout>()] } },
        [typeof(WorldViewGraph)] = (document, rows) => document with { ViewsRaw = document.Views with { Graphs = [.. rows.Cast<WorldViewGraph>()] } },
        [typeof(WorldHudPanel)] = (document, rows) => document with { HudRaw = document.Hud with { Panels = [.. rows.Cast<WorldHudPanel>()] } },
        [typeof(WorldHudElement)] = (document, rows) => document with {
            HudRaw = document.Hud with { Panels = [new WorldHudPanel(Id: Key, Rect: new WorldHudRect(Height: 1f, Width: 1f, X: 0f, Y: 0f), Layer: WorldHudLayer.Over,
                Style: WorldHudPanelStyle.Chip, Elements: [.. rows.Cast<WorldHudElement>()])] },
        },
        [typeof(DynamicsRow)] = (document, rows) => document with { DynamicsRaw = [.. rows.Cast<DynamicsRow>()] },
        [typeof(WorldCurveRow)] = (document, rows) => document with { CurvesRaw = [.. rows.Cast<WorldCurveRow>()] },
    };
    private static readonly Dictionary<Type, (Type Section, Func<WorldDefinition, object, WorldDefinition> Install)> WholeSections = new() {
        [typeof(WorldMutation.SetInputModes)] = (typeof(WorldInputModes), (document, value) => document with { InputModes = (WorldInputModes)value }),
        [typeof(WorldMutation.SetInputHold)] = (typeof(WorldInputHoldAuthoring), (document, value) => document with { InputHoldRaw = (WorldInputHoldAuthoring)value }),
        [typeof(WorldMutation.SetKitAssignment)] = (typeof(WorldRowAssignment), (document, value) => document with { AssignmentRaw = (WorldRowAssignment)value }),
        [typeof(WorldMutation.SetLookAssignment)] = (typeof(WorldRowAssignment), (document, value) => document with { LookAssignmentRaw = (WorldRowAssignment)value }),
        [typeof(WorldMutation.SetSpawns)] = (typeof(WorldSpawnPoint), (document, value) => document with { SpawnPointsRaw = [(WorldSpawnPoint)value] }),
        [typeof(WorldMutation.SetMotion)] = (typeof(WorldMotionDefaults), (document, value) => document with { MotionRaw = (WorldMotionDefaults)value }),
        [typeof(WorldMutation.SetPopulationDefaults)] = (typeof(WorldBodiesDefaults), (document, value) => document with { PopulationRaw = (WorldBodiesDefaults)value }),
        [typeof(WorldMutation.SetPopulationDistribution)] = (typeof(WorldDistribution), (document, value) => document with { PopulationRaw = document.Population with { DistributionRaw = (WorldDistribution)value } }),
        [typeof(WorldMutation.SetPopulationCensus)] = (typeof(SeatActivationPolicy), (document, value) => document with { PopulationRaw = document.Population with { SeatActivationRaw = [(SeatActivationPolicy)value] } }),
        [typeof(WorldMutation.SetRenderDefaults)] = (typeof(WorldRenderDefaults), (document, value) => document with { RenderRaw = (WorldRenderDefaults)value }),
        [typeof(WorldMutation.SetAudioDefaults)] = (typeof(WorldAudioDefaults), (document, value) => document with { AudioRaw = (WorldAudioDefaults)value }),
        [typeof(WorldMutation.SetAuthoringDefaults)] = (typeof(WorldPlacementPolicyDefaults), (document, value) => document with { AuthoringRaw = (WorldPlacementPolicyDefaults)value }),
        [typeof(WorldMutation.SetCollision)] = (typeof(WorldCollision), (document, value) => document with { CollisionRaw = (WorldCollision)value }),
        [typeof(WorldMutation.SetHostDefaults)] = (typeof(WorldHostDefaults), (document, value) => document with { HostRaw = (WorldHostDefaults)value }),
        [typeof(WorldMutation.SetViewDefaults)] = (typeof(WorldViewDefaults), (document, value) => document with { ViewsRaw = (WorldViewDefaults)value }),
        [typeof(WorldMutation.SetViewSeatRig)] = (typeof(WorldCameraProgram), (document, value) => document with { ViewsRaw = document.Views with { SeatRigRaw = (WorldCameraProgram)value } }),
        [typeof(WorldMutation.SetViewSeatControl)] = (typeof(WorldSeatViewControl), (document, value) => document with { ViewsRaw = document.Views with { SeatControlRaw = (WorldSeatViewControl)value } }),
        [typeof(WorldMutation.SetViewPost)] = (typeof(WorldViewPostPass), (document, value) => document with { ViewsRaw = document.Views with { Post = [(WorldViewPostPass)value] } }),
        [typeof(WorldMutation.SetPlayerDefaults)] = (typeof(WorldPlayerDefaults), (document, value) => document with { PlayerDefaultsRaw = (WorldPlayerDefaults)value }),
        [typeof(WorldMutation.SetPlayerSeatLook)] = (typeof(WorldSeatCameraFeel), (document, value) => document with { PlayerDefaultsRaw = document.PlayerDefaults with { SeatLookRaw = (WorldSeatCameraFeel)value } }),
        [typeof(WorldMutation.SetHudDefaults)] = (typeof(WorldHudDefaults), (document, value) => document with { HudRaw = document.Hud with { Defaults = (WorldHudDefaults)value } }),
    };
    // The key every probe row is built under (the least text its serializer admits), and the one a decoy carries.
    private const string Key = WriteDoorProbes.Filler;

    // Every installed value a write replaces or removes is its footprint: a keyed row under the key composition
    // matches (a decoy at the first list position under another key, so a lookup by position is caught), and a
    // section replaced whole. Each binds the probe row at its first state position; the write itself spells only a
    // public row.
    [Fact]
    public void EveryInstalledValueAWriteReplacesIsRefusedAlikeWhetherItsRowIsWithheldOrAbsent() {
        var failures = new List<string>();
        var probed = 0;
        var upserts = WriteDoorProbes.SubmittedModels().Where(entry => entry.Kind.Name.StartsWith("Upsert", StringComparison.Ordinal))
            .ToDictionary(entry => entry.Kind.Name["Upsert".Length..], entry => entry);
        foreach (var kind in typeof(WorldMutation).GetNestedTypes().Where(kind => typeof(WorldMutation).IsAssignableFrom(kind) && !kind.IsAbstract && WorldRecipientMutations.ResolvesReplaced(kind))) {
            Func<Vault, WorldDefinition> document;
            Func<WorldMutation> mutation;
            if (WholeSections.TryGetValue(kind, out var whole)) {
                if (WriteDoorProbes.StatePositions(whole.Section).FirstOrDefault() is not { } position) { continue; }
                var installed = WriteDoorProbes.Build(whole.Section, position, Probe, out var reason);
                if (installed is null) { failures.Add($"{kind.Name}: no installed {whole.Section.Name} binds the probe ({reason})"); continue; }
                document = vault => whole.Install(Base(vault), installed);
                mutation = () => Stamped(kind);
            } else {
                var noun = kind.Name.StartsWith("Upsert", StringComparison.Ordinal) ? kind.Name["Upsert".Length..]
                    : kind.Name.StartsWith("Remove", StringComparison.Ordinal) ? kind.Name["Remove".Length..]
                    : kind.Name.StartsWith("Commit", StringComparison.Ordinal) ? kind.Name["Commit".Length..] : kind.Name;
                if (!upserts.TryGetValue(noun, out var upsert)) { failures.Add($"{kind.Name}: no upsert names its section's row type"); continue; }
                if (!KeyedSections.TryGetValue(upsert.Model, out var install)) { failures.Add($"{kind.Name}: {upsert.Model.Name} is not located in the law's section table"); continue; }
                if (WriteDoorProbes.StatePositions(upsert.Model).FirstOrDefault() is not { } position) { continue; }
                var target = WriteDoorProbes.Build(upsert.Model, position, Probe, out var reason);
                var literal = WriteDoorProbes.Build(upsert.Model, position, "pub", out _);
                if ((target is null) || (literal is null)) { failures.Add($"{kind.Name}: no installed {upsert.Model.Name} binds the probe ({reason})"); continue; }
                var decoy = Rekey(literal, upsert.Model);
                var rows = Array.CreateInstance(upsert.Model, 2);
                rows.SetValue(decoy, 0);
                rows.SetValue(target, 1);
                document = vault => install(Base(vault), rows);
                mutation = (kind == upsert.Kind) ? () => WriteDoorProbes.SetMember(Stamped(kind), upsert.Member, literal, upsert.List) : () => Stamped(kind);
            }
            probed++;
            var answers = Answers(document, mutation);
            if (!answers[Vault.Withheld].StartsWith("refused", StringComparison.Ordinal)) { failures.Add($"{kind.Name}: {answers[Vault.Withheld]} though the value it replaces binds a withheld row"); }
            if ((answers[Vault.Absent] != answers[Vault.Withheld]) || (answers[Vault.WithheldOther] != answers[Vault.Withheld])) {
                failures.Add($"{kind.Name}: answers differ: {string.Join(", ", answers.Select(answer => $"{answer.Key} {answer.Value}"))}");
            }
            if (answers[Vault.Public] != "admitted") { failures.Add($"{kind.Name}: {answers[Vault.Public]} though the row it replaces binds is public"); }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(probed > 30, $"only {probed} replacing kinds were probed");
    }
    // A mutation whose name members all carry the probe rows' key, and whose other members are empty.
    private static WorldMutation Stamped(Type kind) {
        var mutation = (WorldMutation)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(kind);
        typeof(WorldMutation).GetProperty(nameof(WorldMutation.Principal))!.SetValue(mutation, Writer);
        foreach (var property in kind.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(property => property.CanWrite)) {
            if (property.PropertyType == typeof(string)) { property.SetValue(mutation, Key); }
        }
        return mutation;
    }
    // The same row under another key: each top-level text the least value filled becomes "y", each zero one.
    private static object Rekey(object row, Type type) {
        var node = (JsonObject)JsonSerializer.SerializeToNode(row, WorldJsonContext.Default.Options.GetTypeInfo(type))!;
        foreach (var (name, value) in node.ToArray()) {
            if (value is JsonValue leaf && leaf.TryGetValue<string>(out var text) && text == Key) { node[name] = "y"; }
            else if (value is JsonValue number && number.TryGetValue<int>(out var whole) && whole == 0) { node[name] = 1; }
        }
        return node.Deserialize(WorldJsonContext.Default.Options.GetTypeInfo(type))!;
    }

    // A creation whose id is bound to a public row is keyed by that row's live value, which the door does not read:
    // any removal or replacement in its section may target it, so its hidden shape binding is the write's footprint
    // whatever id the write spells.
    [Fact]
    public void ARowWhoseKeyIsBoundIsAFootprintOfEveryWriteToItsSection() {
        var position = Assert.Single(WriteDoorProbes.StatePositions(typeof(WorldPrototype)), site => site.Field == WorldNameRegistry.CreationBinding);
        var node = JsonSerializer.SerializeToNode(WriteDoorProbes.Build(typeof(WorldPrototype), position, Probe, out var reason), WorldJsonContext.Default.WorldPrototype)!;
        Assert.True(node is not null, reason);
        node["id"] = "state.pub";
        var bound = node.Deserialize(WorldJsonContext.Default.WorldPrototype)!;
        WorldDefinition Document(Vault vault) => Base(vault) with { CreationsRaw = [.. Base(vault).Creations, bound] };
        foreach (var write in new Func<WorldMutation>[] { () => new WorldMutation.RemoveCreation(Writer, "secret-piece"), () => new WorldMutation.RemoveCreation(Writer, "other") }) {
            var answers = Answers(Document, write);
            Assert.StartsWith("refused", answers[Vault.Withheld], StringComparison.Ordinal);
            Assert.Equal(answers[Vault.Withheld], answers[Vault.Absent]);
            Assert.Equal(answers[Vault.Withheld], answers[Vault.WithheldOther]);
            Assert.Equal("admitted", answers[Vault.Public]);
        }
    }

    // A screen is keyed by its engine index, never its list position: removing index 3 reads the screen at index 3,
    // and removing index 0, which no screen holds, reads nothing.
    [Fact]
    public void AScreenIsFoundByItsIndexNotItsPosition() {
        var position = WriteDoorProbes.StatePositions(typeof(WorldScreen)).First();
        var screen = (WorldScreen)WriteDoorProbes.Build(typeof(WorldScreen), position, Probe, out var reason)!;
        Assert.True(screen is not null, reason);
        WorldDefinition Document(Vault vault) => Base(vault) with { ScreensRaw = [screen with { Index = 3 }] };
        var third = Answers(Document, () => new WorldMutation.RemoveScreen(Writer, 3));
        var first = Answers(Document, () => new WorldMutation.RemoveScreen(Writer, 0));
        var upsert = Answers(Document, () => new WorldMutation.UpsertScreen(Writer, screen with { Index = 3, Origin = JsonSerializer.Deserialize<DocumentVector3>("[0,0,0]", DocumentJsonOptions.Shared)! }));

        Assert.StartsWith("refused", third[Vault.Withheld], StringComparison.Ordinal);
        Assert.StartsWith("refused", upsert[Vault.Withheld], StringComparison.Ordinal);
        Assert.All(Enum.GetValues<Vault>(), vault => Assert.Equal("admitted", first[vault]));
        Assert.Equal("admitted", third[Vault.Public]);
    }

    // A machine's configuration names a state row where its provider's descriptor says so, which only the host's
    // catalog knows: the door reads it through that catalog, in a submitted machine and in an installed one.
    [Fact]
    public void AMachineConfigurationNamesAStateRowWhereItsProviderSays() {
        var catalog = new WatchCatalog();
        WorldMachine Machine(string watched) => new("panel", WatchCatalog.Engine, JsonSerializer.SerializeToElement(new JsonObject { ["watch"] = watched }));
        var submitted = Answers(Base, () => new WorldMutation.UpsertMachine(Writer, Machine(Probe)), catalog);
        var removed = Answers(vault => Base(vault) with { MachinesRaw = [Machine(Probe)] }, () => new WorldMutation.RemoveMachine(Writer, "panel"), catalog);

        Assert.Equal($"refused {Probe}", submitted[Vault.Withheld]);
        Assert.Equal(submitted[Vault.Withheld], submitted[Vault.Absent]);
        Assert.Equal(submitted[Vault.Withheld], submitted[Vault.WithheldOther]);
        Assert.StartsWith("refused", removed[Vault.Withheld], StringComparison.Ordinal);
        Assert.Equal(removed[Vault.Withheld], removed[Vault.Absent]);
        Assert.Equal("admitted", submitted[Vault.Public]);
        Assert.Equal("admitted", removed[Vault.Public]);
    }
    private sealed class WatchCatalog : IMachineValidationCatalog {
        public const string Engine = "probe.watch";
        private static readonly MachineEngineDescriptor Descriptor = new(Engine, "a probe engine", new MachineObjectDescriptor("probe.watch.v1", [
            new MachineFieldDescriptor("watch", MachineFieldKind.String, "the row it watches", Role: MachineFieldRole.StateReference),
        ]), [], [], [], [], []);
        public bool TryDescriptor(string engineId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MachineEngineDescriptor? descriptor) {
            descriptor = (engineId == Engine) ? Descriptor : null;
            return descriptor is not null;
        }
        public bool IsRegistered(string engineId) => engineId == Engine;
        public bool RequiresPreparation(string contentPath) => false;
        public bool CanPrepare(string engineId, string contentPath) => false;
        public bool HasContentProvider(string engineId) => false;
        public int ImageHeaderLength(string engineId) => 0;
        public string? RefuseImage(string engineId, ReadOnlySpan<byte> header, long imageLength) => "no image";
    }

    // A cell's own audience is a declaration its row carries: a write into the audience row widens who reads that
    // cell, so it is refused for a writer who cannot read the row whole, whatever the cell holds.
    [Fact]
    public void AWriteIntoACellsAudienceRowIsRefusedAlikeWhateverTheCellHolds() {
        var answers = new List<string>();
        foreach (var held in new[] { 3, 7 }) {
            var audience = new WorldStateRow(CellName.Parse(candidate: "audience"), CellKind.Text, Domain: StateDomain.Keys.Instance,
                Cells: [new StateCell(CellName.Parse(candidate: "first"), CellValue.Text(value: "seat0"))]);
            var secret = new WorldStateRow(CellName.Parse(candidate: "secret"), CellKind.Int,
                Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: held), Visibility: new StateVisibility(ReadersFrom: "audience"))]);
            var document = new WorldDefinition(StateRaw: new WorldStateSection(World: [audience, secret]));
            var write = new WorldMutation.UpsertStateCell(Writer, "audience", "second", 0, WorldDocumentWriteKind.Set, Text: "seat1");
            answers.Add(WorldRecipientView.ForComposition(document, Writer).Admits(write, out var refused) ? "admitted" : $"refused {refused}");
        }
        Assert.Equal(answers[0], answers[1]);
        Assert.Equal("refused audience", answers[0]);
    }

    // The declared relation is transitive over dependents: a row keyed over a row keyed over the written one is read
    // when the write moves its domain, so a hidden one two steps away refuses the write whatever it holds.
    [Fact]
    public void ARowTwoStepsDownADomainChainIsTheWritesFootprint() {
        List<string> Answers(bool hidden, string[] held) {
            var pub = new WorldStateRow(CellName.Parse(candidate: "pub"), CellKind.Int,
                Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: 1)), new StateCell(CellName.Parse(candidate: "b"), CellValue.Int(value: 2))]);
            var mid = new WorldStateRow(CellName.Parse(candidate: "mid"), CellKind.Int, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "pub")),
                Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: 1)), new StateCell(CellName.Parse(candidate: "b"), CellValue.Int(value: 1))]);
            var secret = new WorldStateRow(CellName.Parse(candidate: "secret"), CellKind.Bool, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "mid")),
                Visibility: hidden ? new StateVisibility(Readers: []) : null,
                Cells: [.. held.Select(key => new StateCell(CellName.Parse(candidate: key), CellValue.Bool(value: true)))]);
            var view = WorldRecipientView.ForComposition(new WorldDefinition(StateRaw: new WorldStateSection(World: [pub, mid, secret])), Writer);
            WorldMutation[] writes = [new WorldMutation.RemoveStateCell(Writer, "pub", "a"), new WorldMutation.RemoveStateRow(Writer, "pub")];
            return [.. writes.Select(write => view.Admits(write, out var refused) ? "admitted" : $"refused {refused}")];
        }
        var empty = Answers(hidden: true, held: []);

        Assert.Equal(empty, Answers(hidden: true, held: ["a"]));
        Assert.All(empty, answer => Assert.Equal("refused pub", answer));
        Assert.All(Answers(hidden: false, held: ["a"]), answer => Assert.Equal("admitted", answer));
    }
}
