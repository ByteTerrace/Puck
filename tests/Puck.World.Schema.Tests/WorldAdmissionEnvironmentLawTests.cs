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

    [InlineData("bytes", false)]
    [InlineData("file", false)]
    [InlineData("async", false)]
    [InlineData("bytes", true)]
    [InlineData("file", true)]
    [InlineData("async", true)]
    [Theory]
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

                Assert.NotNull(@object: result.Admission);
                Assert.Empty(value: result.Reason);
                Assert.Same(result.Admission.Definition, result.Admission.Compilation.Definition);
                loaded = result.Admission.Definition;
            } else {
                WorldDefinitionAdmission? admission;
                string reason;
                bool accepted;

                if (path == "file") {
                    var file = Path.Combine(path1: directory.FullName, path2: "world.json");

                    File.WriteAllBytes(bytes: bytes, path: file);
                    accepted = WorldDefinitionLoader.TryLoadFileForAdmission(file, out admission, out _, out reason);
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

            Assert.True(condition: WorldDefinitionLoader.TryLoadForAdmission(WorldDefinitionSerialization.Serialize(definition: definition),
                "in-memory", out var admission, out var reason), userMessage: reason);
            Assert.NotNull(@object: admission);
            Assert.Equal(actual: checks, expected: 1);
            var compilation = admission.Compilation;
            var report = compilation.CostReport;

            reject = true;
            Assert.False(condition: WorldDefinitionValidator.TryCompleteAdmission(admission: admission, machines: null, neighbours: null, reason: out reason));
            Assert.Equal(actual: checks, expected: 2);
            Assert.Contains(actualString: reason, expectedSubstring: "bindingOverlays[0] ('test')");
            Assert.Contains(actualString: reason, expectedSubstring: "missing.command");
            Assert.False(condition: WorldDefinitionValidator.TryValidate(admission.Definition, out var fullReason, null));
            Assert.Equal(actual: reason, expected: fullReason);
            reject = false;
            Assert.True(condition: WorldDefinitionValidator.TryCompleteAdmission(admission: admission, machines: null, neighbours: null, reason: out reason), userMessage: reason);
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

            Assert.True(condition: WorldDefinitionValidator.TryAdmit(admission: out var admission, definition: definition, machines: null, neighbours: null, reason: out var reason), userMessage: reason);
            InputSourceVocabularyHook.IsKnownSourceId = _ => false;
            Assert.False(condition: WorldDefinitionValidator.TryCompleteAdmission(admission: admission!, machines: null, neighbours: null, reason: out reason));
            Assert.Contains(actualString: reason, expectedSubstring: "icons.badges[0].source");
            Assert.False(condition: WorldDefinitionValidator.TryValidate(definition, out var fullReason, null));
            Assert.Equal(actual: reason, expected: fullReason);
            InputSourceVocabularyHook.IsKnownSourceId = _ => true;
            GamepadFamilyVocabularyHook.IsKnownFamilyName = _ => false;
            Assert.False(condition: WorldDefinitionValidator.TryCompleteAdmission(admission: admission!, machines: null, neighbours: null, reason: out reason));
            Assert.Contains(actualString: reason, expectedSubstring: "icons.badges[0].overrides[0].family");
            Assert.False(condition: WorldDefinitionValidator.TryValidate(definition, out fullReason, null));
            Assert.Equal(actual: reason, expected: fullReason);
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

            Assert.True(condition: WorldDefinitionValidator.TryAdmit(admission: out var admission, definition: definition, machines: null, neighbours: null, reason: out var reason), userMessage: reason);
            ContextFamilyVocabularyHook.ReservedFamilyNames = ["stance"];
            Assert.False(condition: WorldDefinitionValidator.TryCompleteAdmission(admission: admission!, machines: null, neighbours: null, reason: out reason));
            Assert.Contains(actualString: reason, expectedSubstring: "seatModes[0].name");
            Assert.False(condition: WorldDefinitionValidator.TryValidate(definition, out var fullReason, null));
            Assert.Equal(actual: reason, expected: fullReason);
        } finally {
            ContextFamilyVocabularyHook.ReservedFamilyNames = previous;
        }
    }
    [Fact]
    public void FinalReceiptOwnsTheDrawnValuesAndAnInvalidLoadReturnsNoReceipt() {
        var definition = Document().WithWorldState(rows: [new WorldStateRow(Name: CellName.Parse(candidate: "roll"), Kind: CellKind.Int,
            Draw: new Draw(Generator: new StateGenerator(Source: GeneratorSource.UniformRange, RangeMin: 1, RangeMax: 6),
                Timing: DrawTiming.Boot))]);

        Assert.True(condition: WorldDefinitionLoader.TryLoadForAdmission(WorldDefinitionSerialization.Serialize(definition: definition),
            "draw", out var admission, out var reason), userMessage: reason);
        Assert.NotNull(@object: admission);
        Assert.InRange(admission.Definition.AuthoredState[0].Cells![0].Value.Raw, 1L, 6L);
        Assert.Null(@object: admission.Definition.HostRaw);
        Assert.Null(value: admission.Definition.PopulationRaw);
        Assert.Same(admission.Definition, admission.Compilation.Definition);
        Assert.True(condition: admission.AppliesTo(definition: admission.Definition, machines: null));
        Assert.False(condition: admission.AppliesTo(definition: admission.Definition with { }, machines: null));
        Assert.False(condition: WorldDefinitionLoader.TryLoadForAdmission("{"u8.ToArray(), "invalid", out admission, out reason));
        Assert.Null(@object: admission);
        Assert.NotEmpty(collection: reason);
    }
}
