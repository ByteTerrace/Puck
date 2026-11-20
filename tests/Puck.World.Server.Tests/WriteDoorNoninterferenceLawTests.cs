using System.Text.Json;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>The write door answers a recipient alike whatever it cannot read: every name a submission spells,
/// wherever it spells it, is refused the same way whether the row it names is withheld from the writer or declared
/// nowhere, and whatever a withheld row holds. The sites are read from the code: every state name position the name
/// registry reads in each document value a writer can submit, and every entry of the mutation vocabulary's own table
/// (<see cref="WorldMutationNames.Fields"/>), so a position either learns reaches these laws without a list of
/// their own.</summary>
public sealed class WriteDoorNoninterferenceLawTests {
    private const string Probe = "vault";
    private static readonly Principal Writer = Principal.Seat(slot: 1);
    private static readonly string Unknown = new('0', 64);

    private enum Vault : byte { Absent, Withheld, WithheldOther, Public }

    // A world whose rows a writer reads whole, with the probe row absent, withheld from the writer (holding one of
    // two contents), or public: the documents the corpus's values are drawn from and submitted to.
    private static WorldDefinition Document(Vault vault) {
        var rows = new List<WorldStateRow> {
            new(CellName.Parse(candidate: "pub"), CellKind.Int, Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: 1))]),
            new(CellName.Parse(candidate: "spot"), CellKind.Text, Cells: [new StateCell(StateRow.SlotKey, CellValue.Text(value: "[0,0,0]"))]),
            new(CellName.Parse(candidate: "hand"), CellKind.Bool, Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "pub")),
                Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Bool(value: true))]),
        };
        if (vault != Vault.Absent) {
            rows.Add(new WorldStateRow(CellName.Parse(candidate: Probe), CellKind.Int,
                Visibility: (vault == Vault.Public) ? null : new StateVisibility(Readers: []),
                Cells: (vault == Vault.WithheldOther)
                    ? [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: 7)), new StateCell(CellName.Parse(candidate: "b"), CellValue.Int(value: 9))]
                    : [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: 1))]));
        }
        var unit = new WorldHudRect(Height: 1f, Width: 1f, X: 0f, Y: 0f);
        var document = Fixtures.BuildDocument().WithWorldState(rows) with {
            CreationsRaw = [CreationFixtures.Sphere(id: "piece", scale: 0.2f)],
            PlacementsRaw = new WorldPlacementsSection([
                new WorldPlacement("marker", "piece", JsonSerializer.Deserialize<DocumentVector3>("\"state.spot\"", DocumentJsonOptions.Shared)!, 0, 1),
            ], Policy: Fixtures.StandardAuthoring),
            HudRaw = new WorldHudSection(Defaults: new WorldHudDefaults(Enabled: true), Panels: [
                new WorldHudPanel(Id: "board", Rect: unit, Layer: WorldHudLayer.Over, Style: WorldHudPanelStyle.Chip, Elements: [
                    new WorldHudElement(Id: "score", Kind: WorldHudElementKind.Text, Rect: unit, Style: WorldHudStyleToken.Primary, Binding: "state.pub"),
                    new WorldHudElement(Id: "line", Kind: WorldHudElementKind.Text, Rect: unit, Style: WorldHudStyleToken.Primary, Template: "{state.pub.a} of {state.spot}"),
                ]),
            ]),
        };
        Assert.True(WorldStateDocumentValues.TryResolve(document, out document, out var reason), reason);
        return document;
    }

    // Every document value a writer can submit from the base world, each wrapped in the mutation that submits it.
    private static IEnumerable<(string Name, object Value, Type Type, Func<object, WorldMutation> Submit)> Values(WorldDefinition document) {
        foreach (var row in document.AuthoredState) {
            yield return ($"row {row.Name}", row, typeof(WorldStateRow), value => new WorldMutation.UpsertStateRow(Writer, (WorldStateRow)value));
        }
        foreach (var placement in document.Placements) {
            yield return ($"placement {placement.Id}", placement, typeof(WorldPlacement), value => new WorldMutation.UpsertPlacement(Writer, (WorldPlacement)value));
        }
        foreach (var panel in document.Hud.Panels) {
            yield return ($"panel {panel.Id}", panel, typeof(WorldHudPanel), value => new WorldMutation.UpsertHudPanel(Writer, (WorldHudPanel)value));
            foreach (var element in panel.Elements) {
                yield return ($"element {element.Id}", element, typeof(WorldHudElement), value => new WorldMutation.UpsertHudElement(Writer, panel.Id, (WorldHudElement)value));
            }
        }
        yield return ("transform", new StateTransform.Arrange("hand", "pub"), typeof(StateTransform), value => new WorldMutation.TransformState(Writer, (StateTransform)value));
    }

    // The mutation vocabulary's own state name positions, each placing a name at that one position.
    private static readonly Dictionary<(Type Owner, string Member), Func<string, WorldMutation>> Vocabulary = new() {
        [(typeof(WorldMutation.UpsertStateCell), nameof(WorldMutation.UpsertStateCell.Row))] = name => new WorldMutation.UpsertStateCell(Writer, name, "a", 2, WorldDocumentWriteKind.Set),
        [(typeof(WorldMutation.RemoveStateCell), nameof(WorldMutation.RemoveStateCell.Row))] = name => new WorldMutation.RemoveStateCell(Writer, name, "a"),
        [(typeof(WorldMutation.RemoveStateRow), nameof(WorldMutation.RemoveStateRow.Name))] = name => new WorldMutation.RemoveStateRow(Writer, name),
        [(typeof(WorldMutation.Generate), nameof(WorldMutation.Generate.Row))] = name => new WorldMutation.Generate(Writer, name),
        [(typeof(WorldMutation.SetProperty), nameof(WorldMutation.SetProperty.Name))] = name => new WorldMutation.SetProperty(Writer, name),
        [(typeof(WorldMutation.Batch), nameof(WorldMutation.Batch.ExpectedStateRows))] = name => Guarded(expectedDefinition: Unknown, rows: [name]),
        [(typeof(WorldStateExpectation), nameof(WorldStateExpectation.Row))] = name => Guarded(cells: [new WorldStateExpectation(name, "a")]),
        [(typeof(WorldDefinitionReadDependency), nameof(WorldDefinitionReadDependency.StateRows))] = name =>
            Guarded(inputs: new WorldDefinitionReadDependency([], [name], Unknown)),
        [(typeof(PhaseGuard), nameof(PhaseGuard.Row))] = name => new WorldMutation.TransformState(Writer, new StateTransform.Arrange("hand", "pub"), new PhaseGuard(name, 0)),
    };
    private static WorldMutation.Batch Guarded(string? expectedDefinition = null, IReadOnlyList<string>? rows = null,
        IReadOnlyList<WorldStateExpectation>? cells = null, WorldDefinitionReadDependency? inputs = null) =>
        new(Writer, [new WorldMutation.UpsertStateCell(Writer, "pub", "a", 3, WorldDocumentWriteKind.Set)],
            ExpectedDefinition: expectedDefinition, ExpectedCells: cells, ExpectedStateRows: rows, ExpectedInputs: inputs);

    // The corpus: every state name position of every submittable value respelled to the probe, one position at a
    // time, then every vocabulary position.
    private static List<(string Site, WorldMutation Mutation)> Corpus() {
        var corpus = new List<(string Site, WorldMutation Mutation)>();
        foreach (var (name, value, type, submit) in Values(Document(Vault.Absent))) {
            var positions = 0;
            WorldSpelledNames.Collect(value, type, (_, kind, _) => { if (WorldSpelledNames.NamesState(kind)) { positions++; } });
            Assert.True(positions > 0, $"{name} spells no state name; the registry no longer reads it");
            for (var position = 0; position < positions; position++) {
                var node = JsonSerializer.SerializeToNode(value, WorldJsonContext.Default.Options.GetTypeInfo(type));
                var seen = 0;
                var target = position;
                string? replaced = null;
                WorldModuleNamespace.Respell(node, type, (site, kind, spelled, _) => {
                    if (!WorldSpelledNames.NamesState(kind) || (seen++ != target)) { return null; }
                    replaced = $"{site.Owner.Name}.{site.Member} '{spelled}'";
                    return Probe;
                });
                var respelled = node.Deserialize(WorldJsonContext.Default.Options.GetTypeInfo(type))!;
                corpus.Add(($"{name} at {replaced}", submit(respelled)));
            }
        }
        foreach (var (site, mutation) in Vocabulary) {
            corpus.Add(($"{site.Owner.Name}.{site.Member}", mutation(Probe)));
        }
        return corpus;
    }

    // What the writer is told for each probe, in one world, submitted in corpus order with every grant the probes
    // need, so a probe the door missed reaches grants, composition and validation rather than stopping at a grant.
    private static List<string> Answers(Vault vault, IReadOnlyList<(string Site, WorldMutation Mutation)> corpus) {
        using var fixture = Fixtures.FreshServer(Document(vault));
        foreach (var section in Enum.GetValues<WorldSection>().Where(section => !section.IsProgram() && (WorldMutationKindCatalog.KindsOf(section) != default))) {
            fixture.Server.Grant(new WorldGrant(Grantee: Writer, Capability: WorldCapability.Mutate, Subject: GrantSubject.Section(section),
                Exclusive: false, Budget: 64, KindMask: WorldMutationKindCatalog.KindsOf(section)), Principal.Console);
        }
        foreach (var row in (vault == Vault.Absent) ? ["pub", "spot", "hand"] : new[] { "pub", "spot", "hand", Probe }) {
            fixture.Server.Grant(new WorldGrant(Grantee: Writer, Capability: WorldCapability.Edit, Subject: GrantSubject.State(row),
                Exclusive: false, KindMask: WorldMutationKindCatalog.KindsOf(WorldSection.State)), Principal.Console);
        }
        return [.. corpus.Select(probe => fixture.SubmitMutation(probe.Mutation) is var outcome
            ? $"{probe.Site}: {outcome.Decision} {outcome.Code} {outcome.Detail}" : string.Empty)];
    }

    [Fact]
    public void EveryNameASubmissionSpellsIsRefusedAlikeWhetherItsRowIsWithheldOrAbsent() {
        var corpus = Corpus();
        var withheld = Answers(Vault.Withheld, corpus);

        Assert.Equal(withheld, Answers(Vault.Absent, corpus));
        Assert.Equal(withheld, Answers(Vault.WithheldOther, corpus));
        Assert.All(withheld, answer => Assert.Contains(WorldRecipientMutations.NotDisclosedCode, answer, StringComparison.Ordinal));
        // The control: a writer who reads the probe row is not refused by its policy at every position.
        Assert.Contains(Answers(Vault.Public, corpus), answer => !answer.Contains(WorldRecipientMutations.NotDisclosedCode, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryStateNamePositionOfTheMutationVocabularyIsProbed() {
        var missing = WorldMutationNames.Fields.Where(field => WorldSpelledNames.NamesState(field.Kind) && !Vocabulary.ContainsKey((field.Owner, field.Member)))
            .Select(field => $"{field.Owner.Name}.{field.Member}").ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryMutationMemberIsANamePositionADocumentValueOrExcluded() =>
        Assert.True(WorldMutationNames.Uncovered.Count == 0, string.Join(", ", WorldMutationNames.Uncovered));

    // A kind that replaces or removes an installed value outside a program section must have the door read that
    // value, or its state names would escape the footprint. A state row is admitted with its name, a placement with
    // its chain, and a grant or group change is the grants' own transition.
    [Fact]
    public void EveryKindThatReplacesAnInstalledValueHasTheDoorReadIt() {
        var judgedElsewhere = new HashSet<Type> {
            typeof(WorldMutation.UpsertStateRow), typeof(WorldMutation.RemoveStateRow), typeof(WorldMutation.UpsertStateCell),
            typeof(WorldMutation.RemoveStateCell), typeof(WorldMutation.UpsertPlacement), typeof(WorldMutation.RemovePlacement),
            typeof(WorldMutation.UpsertGrant), typeof(WorldMutation.RemoveGrant), typeof(WorldMutation.UpsertGroupKind),
            typeof(WorldMutation.RemoveGroupKind),
        };
        var unresolved = typeof(WorldMutation).GetNestedTypes()
            .Where(kind => typeof(WorldMutation).IsAssignableFrom(kind) && !kind.IsAbstract &&
                (kind.Name.StartsWith("Upsert", StringComparison.Ordinal) || kind.Name.StartsWith("Remove", StringComparison.Ordinal)))
            .Where(kind => !judgedElsewhere.Contains(kind) &&
                !WorldMutationKindCatalog.SectionOf((WorldMutation)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(kind)).IsProgram())
            .Where(kind => !WorldRecipientMutations.ResolvesReplaced(kind))
            .Select(kind => kind.Name).ToArray();

        Assert.Empty(unresolved);
    }

    // A row's live audience lists who reads it, so a write into the audience row is judged by every row reading
    // through it: a reverse dependency the token-domain relation does not hold. A writer who cannot read the
    // audienced row would otherwise widen it to itself, and be refused or not by what that row holds.
    [Fact]
    public void AWriteIntoARowAWithheldDeclarationNamesIsRefusedAlikeWhateverThatRowHolds() {
        var answers = new List<string>();
        foreach (var held in new[] { false, true }) {
            var audience = new WorldStateRow(CellName.Parse(candidate: "audience"), CellKind.Text, Domain: StateDomain.Keys.Instance,
                Cells: [new StateCell(CellName.Parse(candidate: "first"), CellValue.Text(value: "seat1"))]);
            var secret = new WorldStateRow(CellName.Parse(candidate: "secret"), CellKind.Int, Visibility: new StateVisibility(ReadersFrom: "audience"),
                Cells: held ? [new StateCell(StateRow.SlotKey, CellValue.Int(value: 7))] : []);
            var document = new WorldDefinition(StateRaw: new WorldStateSection(World: [audience, secret]));
            var view = WorldRecipientView.ForComposition(document, Writer);
            var write = new WorldMutation.UpsertStateCell(Writer, "audience", "second", 0, WorldDocumentWriteKind.Set, Text: "seat2");
            answers.Add($"{view.Admits(write, out var refused)} {refused}");
        }
        Assert.Equal(answers[0], answers[1]);
        Assert.Equal("False audience", answers[0]);
    }
}
