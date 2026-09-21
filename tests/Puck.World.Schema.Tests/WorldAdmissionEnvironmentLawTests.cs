using Puck.Commands;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Schema.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AdmissionEnvironmentCollection {
    public const string Name = "admission-environment";
}

[Collection(AdmissionEnvironmentCollection.Name)]
public sealed class WorldAdmissionEnvironmentLawTests {
    private static WorldDefinition Document() => new(Simulation: new WorldSimulationDefaults(RateHz: 240));

    [Theory]
    [InlineData("bytes", false)]
    [InlineData("file", false)]
    [InlineData("async", false)]
    [InlineData("bytes", true)]
    [InlineData("file", true)]
    [InlineData("async", true)]
    public async Task BootValuesSettleBeforeTheOnlyFullAdmission(string path, bool draw) {
        var previous = BindingVocabularyHook.VocabularyCheck;
        var checks = 0;
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-boot-admission-");
        try {
            BindingVocabularyHook.VocabularyCheck = (_, _, _, _) => checks++;
            var definition = Document() with {
                BindingOverlaysRaw = [new WorldBindingOverlay(Id: "test", Document: new BindingProfileDocument(
                    Version: BindingProfileDocument.CurrentVersion, Modifiers: [], Chords: []))],
                GravityRaw = Document().Gravity,
            };
            definition = definition.WithWorldState(rows: [new WorldStateRow(Name: CellName.Parse(candidate: "force"), Kind: CellKind.Text,
                Cells: (draw ? null : [new StateCell(WorldStateRow.SlotKey, CellValue.Text(value: "[0,-2,0]"))]),
                Draw: (draw ? new Draw(Generator: new StateGenerator(Source: GeneratorSource.Markov,
                    Start: CellName.Parse(candidate: "start"), Contexts: [
                        new GeneratorContext(CellName.Parse(candidate: "start"), [new GeneratorAlternative("[0,-2,0]", 1, CellName.Parse(candidate: "end"))]),
                        new GeneratorContext(CellName.Parse(candidate: "end")),
                    ]), Timing: DrawTiming.Boot) : null))]);
            // Authored bytes may carry an unresolved reference; serialization of a live document expects its
            // computed properties to be readable, so construct this input at the document boundary.
            var json = JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition: definition))!;
            json["gravity"]!["uniform"] = "state.force";
            var bytes = Encoding.UTF8.GetBytes(s: json.ToJsonString());
            WorldDefinition? loaded;
            if (path == "async") {
                var result = await WorldDefinitionLoader.LoadAsync(bytes, "async", "boot",
                    (_, _) => throw new InvalidOperationException(message: "No neighbours are declared."), TestContext.Current.CancellationToken);
                Assert.NotNull(@object: result.Definition);
                Assert.Empty(value: result.Reason);
                loaded = result.Definition;
            } else {
                WorldDefinitionAdmission? admission;
                string reason;
                bool accepted;
                if (path == "file") {
                    var file = Path.Combine(path1: directory.FullName, path2: "world.json");
                    File.WriteAllBytes(bytes: bytes, path: file);
                    accepted = WorldDefinitionLoader.TryLoadFileForAdmission(file, out admission, out reason);
                } else {
                    accepted = WorldDefinitionLoader.TryLoadForAdmission(bytes, "bytes", out admission, out reason);
                }
                Assert.True(condition: accepted, userMessage: reason);
                Assert.NotNull(@object: admission);
                Assert.Same(admission.Definition, admission.Compilation.Definition);
                loaded = admission.Definition;
            }
            Assert.Equal(actual: checks, expected: 1);
            Assert.NotNull(@object: loaded.Gravity.Uniform);
            Assert.Equal(-2f, loaded.Gravity.Uniform.Value.Y);
            Assert.True(condition: WorldStateDocumentValues.HasReference(graph: loaded));
            Assert.Equal((draw ? 1L : 0L), loaded.AuthoredState[0].DrawCursor);
        } finally {
            BindingVocabularyHook.VocabularyCheck = previous;
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void LoaderValidatesOnceAndCompletionRechecksTheSameDelegateAgainstItsNewRegistry() {
        var previous = BindingVocabularyHook.VocabularyCheck;
        var reject = false;
        var checks = 0;
        try {
            BindingVocabularyHook.VocabularyCheck = (_, _, _, errors) => {
                checks++;
                if (reject) { errors.Add(item: "command 'missing.command' is not registered"); }
            };
            var definition = Document() with {
                BindingOverlaysRaw = [new WorldBindingOverlay(Id: "test", Document: new BindingProfileDocument(
                    Version: BindingProfileDocument.CurrentVersion, Modifiers: [], Chords: []))],
            };
            Assert.True(WorldDefinitionLoader.TryLoadForAdmission(WorldDefinitionSerialization.Serialize(definition: definition),
                "in-memory", out var admission, out var reason), reason);
            Assert.NotNull(admission);
            Assert.Equal(actual: checks, expected: 1);
            var compilation = admission.Compilation;
            var report = compilation.CostReport;
            reject = true;
            Assert.False(WorldDefinitionValidator.TryCompleteAdmission(admission, null, null, out reason));
            Assert.Equal(actual: checks, expected: 2);
            Assert.Contains("bindingOverlays[0] ('test')", reason);
            Assert.Contains("missing.command", reason);
            Assert.False(condition: WorldDefinitionValidator.TryValidate(admission.Definition, out var fullReason, null));
            Assert.Equal(fullReason, reason);
            reject = false;
            Assert.True(WorldDefinitionValidator.TryCompleteAdmission(admission, null, null, out reason), reason);
            Assert.Same(compilation, admission.Compilation);
            Assert.Same(report, admission.Compilation.CostReport);
        } finally {
            BindingVocabularyHook.VocabularyCheck = previous;
        }
    }

    [Fact]
    public void CompletionRechecksInputAndGamepadVocabularies() {
        var previousSource = InputSourceVocabularyHook.IsKnownSourceId;
        var previousFamily = GamepadFamilyVocabularyHook.IsKnownFamilyName;
        try {
            InputSourceVocabularyHook.IsKnownSourceId = _ => true;
            GamepadFamilyVocabularyHook.IsKnownFamilyName = _ => true;
            var definition = Document() with {
                IconsRaw = new WorldIconographySection(
                    IconsRaw: [new WorldIconRow(Name: "button", Label: "B")],
                    BadgesRaw: [new WorldIconBadgeRow(Source: "device.button", Icon: "button",
                        OverridesRaw: [new WorldIconBadgeOverride(Family: "test-pad", Icon: "button")])]),
            };
            Assert.True(WorldDefinitionValidator.TryAdmit(definition, null, null, out var admission, out var reason), reason);
            InputSourceVocabularyHook.IsKnownSourceId = _ => false;
            Assert.False(WorldDefinitionValidator.TryCompleteAdmission(admission!, null, null, out reason));
            Assert.Contains("icons.badges[0].source", reason);
            Assert.False(condition: WorldDefinitionValidator.TryValidate(definition, out var fullReason, null));
            Assert.Equal(fullReason, reason);
            InputSourceVocabularyHook.IsKnownSourceId = _ => true;
            GamepadFamilyVocabularyHook.IsKnownFamilyName = _ => false;
            Assert.False(WorldDefinitionValidator.TryCompleteAdmission(admission!, null, null, out reason));
            Assert.Contains("icons.badges[0].overrides[0].family", reason);
            Assert.False(condition: WorldDefinitionValidator.TryValidate(definition, out fullReason, null));
            Assert.Equal(fullReason, reason);
        } finally {
            InputSourceVocabularyHook.IsKnownSourceId = previousSource;
            GamepadFamilyVocabularyHook.IsKnownFamilyName = previousFamily;
        }
    }

    [Fact]
    public void CompletionRechecksReservedContextFamilies() {
        var previous = ContextFamilyVocabularyHook.ReservedFamilyNames;
        try {
            ContextFamilyVocabularyHook.ReservedFamilyNames = [];
            var definition = Document() with {
                SeatModesRaw = [new WorldSeatModeFamily(Name: "stance", DefaultState: "rest",
                    States: [new WorldSeatModeState(Name: "rest")])],
            };
            Assert.True(WorldDefinitionValidator.TryAdmit(definition, null, null, out var admission, out var reason), reason);
            ContextFamilyVocabularyHook.ReservedFamilyNames = ["stance"];
            Assert.False(WorldDefinitionValidator.TryCompleteAdmission(admission!, null, null, out reason));
            Assert.Contains("seatModes[0].name", reason);
            Assert.False(condition: WorldDefinitionValidator.TryValidate(definition, out var fullReason, null));
            Assert.Equal(fullReason, reason);
        } finally {
            ContextFamilyVocabularyHook.ReservedFamilyNames = previous;
        }
    }

    [Fact]
    public void FinalReceiptOwnsTheDrawnValuesAndAnInvalidLoadReturnsNoReceipt() {
        var definition = Document().WithWorldState(rows: [new WorldStateRow(Name: CellName.Parse(candidate: "roll"), Kind: CellKind.Int,
            Draw: new Draw(Generator: new StateGenerator(Source: GeneratorSource.UniformRange, RangeMin: 1, RangeMax: 6),
                Timing: DrawTiming.Boot))]);
        Assert.True(WorldDefinitionLoader.TryLoadForAdmission(WorldDefinitionSerialization.Serialize(definition: definition),
            "draw", out var admission, out var reason), reason);
        Assert.NotNull(admission);
        Assert.InRange(admission.Definition.AuthoredState[0].Cells![0].Value.Raw, 1L, 6L);
        Assert.Null(admission.Definition.HostRaw);
        Assert.Null(admission.Definition.PopulationRaw);
        Assert.Same(admission.Definition, admission.Compilation.Definition);
        Assert.True(admission.AppliesTo(admission.Definition, null));
        Assert.False(admission.AppliesTo(admission.Definition with { }, null));
        Assert.False(WorldDefinitionLoader.TryLoadForAdmission("{"u8.ToArray(), "invalid", out admission, out reason));
        Assert.Null(admission);
        Assert.NotEmpty(reason);
    }
}
