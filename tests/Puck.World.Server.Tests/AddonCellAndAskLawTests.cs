using System.Text;
using Puck.Assets;
using Puck.Commands;
using Puck.Confinement;
using Puck.Scripting;
using Puck.Testing;
using Puck.World.Addons;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Regression checks for named state authority and atomic cell writes at the guest boundary.</summary>
public sealed class AddonCellAndAskLawTests {
    [Theory]
    [InlineData(false, "secret", false)]
    [InlineData(true, "secret", false)]
    [InlineData(false, "absent", false)]
    [InlineData(true, "absent", false)]
    [InlineData(false, "secret", true)]
    [InlineData(true, "secret", true)]
    [InlineData(false, "absent", true)]
    [InlineData(true, "absent", true)]
    public void RestrictedCellBindsAndPreviouslyBoundWritesReturnNotDisclosedBeforeLookingUpAnyKey(bool present, string key, bool bindFirst) {
        using var directory = new TemporaryDirectory(prefix: "puck-bind-disclosure-");
        var wat = $$"""
            (module
              (memory (export "memory") 1)
              (data (i32.const 256) "\02\00\0f\00\00\00\00\00\00\00\00\00\00\00\00\00\03\00\00\00\00\00\00\00\00\00\00\00\00\00\00\00")
              (data (i32.const 12400) "hidden")
              (data (i32.const 12432) "{{key}}")
              (global $stage (mut i32) (i32.const 0))
              (func (export "puck_abi_version") (result i32) (i32.const 1))
              (func (export "puck_channels_ptr") (result i32) (i32.const 256))
              (func (export "puck_channels_count") (result i32) (i32.const 2))
              (func (export "puck_out_ptr") (result i32) (i32.const 4096))
              (func (export "puck_out_cap") (result i32) (i32.const 1))
              (func (export "puck_in_ptr") (result i32) (i32.const 8192))
              (func (export "puck_in_cap") (result i32) (i32.const 8))
              (func (export "puck_on_tick") (param $count i32) (result i32)
                (local $i i32) (local $p i32) (local $answered i32)
                (if (i32.eq (global.get $stage) (i32.const 3)) (then (return (i32.const 0))))
                (if (global.get $stage) (then
                  (loop $answers
                    (local.set $p (i32.add (i32.const 8192) (i32.mul (local.get $i) (i32.const 32))))
                    (if (i32.and (i32.eq (i32.load8_u (local.get $p)) (i32.const 2))
                          (i32.eqz (i32.load16_u offset=2 (local.get $p))))
                      (then
                        (local.set $answered (i32.const 1))
                        (if (i32.ne (i32.load8_u offset=8 (local.get $p))
                          (select (i32.const {{(int)AddonVerdict.Applied}}) (i32.const {{(int)AddonVerdict.NotDisclosed}})
                            (i32.and (i32.const {{(bindFirst ? 1 : 0)}}) (i32.eq (global.get $stage) (i32.const 1))))) (then unreachable))))
                    (local.set $i (i32.add (local.get $i) (i32.const 1)))
                    (br_if $answers (i32.lt_u (local.get $i) (local.get $count))))
                  (if (i32.eqz (local.get $answered)) (then unreachable))
                  (if (i32.and (i32.const {{(bindFirst ? 1 : 0)}}) (i32.eq (global.get $stage) (i32.const 1))) (then
                    (global.set $stage (i32.const 2))
                    (i32.store8 (i32.const 4096) (i32.const 1))
                    (i32.store16 (i32.const 4100) (i32.const 1))
                    (i32.store16 (i32.const 4102) (i32.const 15))
                    (i64.store (i32.const 4104) (i64.const 5))
                    (i64.store (i32.const 4112) (i64.const 0))
                    (return (i32.const 1))))
                  (global.set $stage (i32.const 3))
                  (return (i32.const 0))))
                (global.set $stage (i32.const 1))
                (i32.store (i32.const 12288) (i32.const 12400))
                (i32.store (i32.const 12292) (i32.const 12432))
                (i32.store16 (i32.const 12296) (i32.const 6))
                (i32.store16 (i32.const 12298) (i32.const {{key.Length}}))
                (i32.store8 (i32.const 4096) (i32.const 1))
                (i32.store16 (i32.const 4102) (i32.const 14))
                (i64.store (i32.const 4104) (i64.const 12288))
                (i64.store (i32.const 4112) (i64.const 1))
                (i32.const 1)))
            """;
        var bytes = Encoding.UTF8.GetBytes(wat);
        var path = directory.PathOf("guest.wat").Replace('\\', '/');
        File.WriteAllBytes(path, bytes);
        var definition = Fixtures.BuildDocument().WithWorldState([
            new WorldStateRow(CellName.Parse("hidden"), CellKind.Int, Capacity: 4, CellVisibility: !bindFirst,
                Cells: present ? [new StateCell(CellName.Parse("secret"), CellValue.Int(7), Visibility: bindFirst ? null : new StateVisibility(Readers: ["seat1"]))] : [])
        ]) with {
            AddonsRaw = [new WorldAddonRow("bind", path, "sha256/" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), 1000000UL, true)],
        };
        using var fixture = Fixtures.FreshServer(definition);
        using var runtime = WorldAddonRuntime.Create(fixture.Server.Definition, fixture.Server);
        fixture.Step();
        if (bindFirst) {
            Assert.True(fixture.Server.TryApplyMutation(new WorldMutation.UpsertStateRow(Principal.Console,
                definition.State[0] with { CellVisibility = true }), fixture.Server.CompletedTick,
                fixture.Server.CompletedEngineTicks, SubmissionEnvelope.LocalConnectionId, 0, false));
        }
        fixture.Step();
        fixture.Step();
        var cost = Assert.Single(runtime.DescribeCost());
        Assert.True(cost.State == AddonState.Enabled, cost.FaultDetail);
        Assert.Equal(0UL, cost.TotalAnswersDropped);
    }

    [Fact]
    public void ReusedRowDecodesKeepJournalCellsImmutableAndStillRejectDuplicateMembers() {
        var ordinal = WorldMutationKindCatalog.All().Single(predicate: entry => (entry.Type == typeof(WorldMutation.UpsertStateRow))).Ordinal;
        var actor = Principal.Addon(name: "guest");

        WorldMutation.UpsertStateRow Decode(string payload, WorldMutation? previous = null) {
            Assert.True(condition: WorldAddonMutationDecoder.TryDecode(ordinal, WorldSection.State, Encoding.UTF8.GetBytes(s: payload),
                actor, out var mutation, out var error, previous), userMessage: error);
            return Assert.IsType<WorldMutation.UpsertStateRow>(@object: mutation);
        }
        const string First = """{"name":"counter","kind":"int","cells":[{"key":"a","value":1},{"key":"b","value":2}]}""";
        var first = Decode(First);

        Assert.Same(first, Decode(payload: First, previous: first));
        var changed = Decode(payload: """{"name":"counter","kind":"int","cells":[{"key":"a","value":3},{"key":"b","value":2}]}""", previous: first);

        Assert.Equal(1, first.Row.Cells![0].Value.AsInt);
        Assert.Equal(3, changed.Row.Cells![0].Value.AsInt);
        Assert.Same(first.Row.Cells[1], changed.Row.Cells[1]);
        var shorter = Decode(payload: """{"name":"counter","kind":"int","cells":[{"key":"a","value":3}]}""", previous: changed);

        Assert.Single(collection: shorter.Row.Cells!);
        Assert.Equal(2, changed.Row.Cells.Count);
        Assert.Empty(collection: Decode(payload: """{"name":"counter","kind":"int","cells":[]}""", previous: shorter).Row.Cells!);
        Assert.False(condition: WorldAddonMutationDecoder.TryDecode(ordinal, WorldSection.State,
            Encoding.UTF8.GetBytes(s: """{"name":"counter","kind":"int","other":0,"other":1}"""), actor,
            out _, out var refusal, first));
        Assert.Equal(actual: refusal, expected: "UpsertStateRow: duplicate key 'other'");
    }

    private sealed class NoChannels : IAddonChannelResolver {
        public bool TryResolve(string name, out int ordinal, out AddonChannelValueShape shape) {
            ordinal = -1;
            shape = default;
            return false;
        }
    }

    [Fact]
    public void AStateAskKeepsItsAuthoredNameLengthThroughThePump() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-addon-state-ask-");

        try {
            var path = Path.Combine(path1: directory.FullName, path2: "guest.wat");

            File.WriteAllText(contents: """
                (module
                  (memory (export "memory") 1)
                  (data (i32.const 256) "\02\00\06\00\00\00\00\00\00\00\00\00\00\00\00\00\03\00\00\00\00\00\00\00\00\00\00\00\00\00\00\00")
                  (data (i32.const 12288) "config")
                  (func (export "puck_abi_version") (result i32) (i32.const 1))
                  (func (export "puck_channels_ptr") (result i32) (i32.const 256))
                  (func (export "puck_channels_count") (result i32) (i32.const 2))
                  (func (export "puck_out_ptr") (result i32) (i32.const 4096))
                  (func (export "puck_out_cap") (result i32) (i32.const 1))
                  (func (export "puck_in_ptr") (result i32) (i32.const 8192))
                  (func (export "puck_in_cap") (result i32) (i32.const 2))
                  (func (export "puck_on_tick") (param i32) (result i32)
                    (i32.store8 (i32.const 4096) (i32.const 2))
                    (i32.store16 (i32.const 4102) (i32.const 5))
                    (i64.store (i32.const 4104) (i64.const 12288))
                    (i64.store (i32.const 4112) (i64.const 2))
                    (i64.store (i32.const 4120) (i64.const 6))
                    (i32.const 1)))
                """, path: path);
            using var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);
            var module = new WasmModuleLoader(engine, new FileSystemAssetSource()).Load(path);
            using var instance = new AddonInstance(engine, module, new NoChannels(), new AddonDescriptor(Enabled: true, FuelPerTick: null, ModuleHash: null, ModulePath: path, Name: "guest"));

            instance.Admit();
            var pump = new AddonSimulationPump();

            Assert.True(condition: pump.Pump(input: [], instance: instance));
            var ask = Assert.Single(collection: pump.Asks.ToArray());

            Assert.Equal(AddonSubjectKind.State, ask.SubjectKind);
            Assert.Equal(6, ask.NameLength);
            Assert.Equal(12288, ask.SubjectIndex);
        } finally {
            ConfinedDirectory.RemoveTree(path: directory.FullName);
        }
    }
    [InlineData("{\"row\":\"config\",\"key\":\"seed\",\"kind\":\"set\",\"value\":42}", true)]
    [InlineData("{\"row\":\"config\",\"key\":\"seed\",\"kind\":\"add\",\"value\":-1}", true)]
    [InlineData("{\"row\":\"config\",\"key\":\"seed\",\"kind\":\"multiply\",\"value\":42}", false)]
    [InlineData("{\"row\":\"config\",\"key\":\"seed\",\"key\":\"other\",\"kind\":\"set\",\"value\":42}", false)]
    [Theory]
    public void CellWritesUseTheTypedMutationDoorAndRefuseAmbiguousPayloads(string payload, bool allowed) {
        var decoded = WorldAddonMutationDecoder.TryDecode(47, WorldSection.State, Encoding.UTF8.GetBytes(s: payload),
            Principal.Addon(name: "guest"), out var mutation, out var error);

        Assert.Equal(actual: decoded, expected: allowed);
        if (allowed) {
            var cell = Assert.IsType<WorldMutation.UpsertStateCell>(@object: mutation);

            Assert.Equal("config", cell.Row);
            Assert.Equal("seed", cell.Key);
        } else {
            Assert.Null(@object: mutation);
            Assert.NotEmpty(collection: error);
        }
    }
    [InlineData("{\"row\":\"config\",\"key\":\"seed\"}", true)]
    [InlineData("{\"row\":\"config\",\"key\":\"seed\",\"extra\":1}", false)]
    [InlineData("{\"row\":\"config\",\"row\":\"other\",\"key\":\"seed\"}", false)]
    [Theory]
    public void CellRemovalUsesTheSameStrictDecoder(string payload, bool allowed) {
        var ordinal = WorldMutationKindCatalog.All().Single(predicate: entry => (entry.Type == typeof(WorldMutation.RemoveStateCell))).Ordinal;
        var decoded = WorldAddonMutationDecoder.TryDecode(ordinal, WorldSection.State, Encoding.UTF8.GetBytes(s: payload),
            Principal.Addon(name: "guest"), out var mutation, out var error);

        Assert.Equal(actual: decoded, expected: allowed);
        if (allowed) {
            var cell = Assert.IsType<WorldMutation.RemoveStateCell>(@object: mutation);

            Assert.Equal("config", cell.Row);
            Assert.Equal("seed", cell.Key);
        } else {
            Assert.Null(@object: mutation);
            Assert.NotEmpty(collection: error);
        }
    }
}
