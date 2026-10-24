using System.Text;
using Puck.Assets;
using Puck.Commands;
using Puck.Scripting;
using Puck.World.Addons;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Regression checks for named state authority and atomic cell writes at the guest boundary.</summary>
public sealed class AddonCellAndAskLawTests {
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
            var path = Path.Combine(directory.FullName, "guest.wat");
            File.WriteAllText(path, """
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
                """);
            using var engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);
            var module = new WasmModuleLoader(engine, new FileSystemAssetSource()).Load(path);
            using var instance = new AddonInstance(engine, module, new NoChannels(), new AddonDescriptor("guest", path, null, null, true));
            instance.Admit();
            var pump = new AddonSimulationPump();
            Assert.True(pump.Pump(instance, []));
            var ask = Assert.Single(pump.Asks.ToArray());
            Assert.Equal(AddonSubjectKind.State, ask.SubjectKind);
            Assert.Equal(6, ask.NameLength);
            Assert.Equal(12288, ask.SubjectIndex);
        } finally {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"row\":\"config\",\"key\":\"seed\",\"kind\":\"set\",\"value\":42}", true)]
    [InlineData("{\"row\":\"config\",\"key\":\"seed\",\"kind\":\"add\",\"value\":-1}", true)]
    [InlineData("{\"row\":\"config\",\"key\":\"seed\",\"kind\":\"multiply\",\"value\":42}", false)]
    [InlineData("{\"row\":\"config\",\"key\":\"seed\",\"key\":\"other\",\"kind\":\"set\",\"value\":42}", false)]
    public void CellWritesUseTheTypedMutationDoorAndRefuseAmbiguousPayloads(string payload, bool allowed) {
        var decoded = WorldAddonMutationDecoder.TryDecode(47, WorldSection.State, Encoding.UTF8.GetBytes(payload),
            Principal.Addon("guest"), out var mutation, out var error);
        Assert.Equal(allowed, decoded);
        if (allowed) {
            var cell = Assert.IsType<WorldMutation.UpsertStateCell>(mutation);
            Assert.Equal("config", cell.Row);
            Assert.Equal("seed", cell.Key);
        } else {
            Assert.Null(mutation);
            Assert.NotEmpty(error);
        }
    }

    [Theory]
    [InlineData("{\"row\":\"config\",\"key\":\"seed\"}", true)]
    [InlineData("{\"row\":\"config\",\"key\":\"seed\",\"extra\":1}", false)]
    [InlineData("{\"row\":\"config\",\"row\":\"other\",\"key\":\"seed\"}", false)]
    public void CellRemovalUsesTheSameStrictDecoder(string payload, bool allowed) {
        var ordinal = WorldMutationKindCatalog.All().Single(entry => entry.Type == typeof(WorldMutation.RemoveStateCell)).Ordinal;
        var decoded = WorldAddonMutationDecoder.TryDecode(ordinal, WorldSection.State, Encoding.UTF8.GetBytes(payload),
            Principal.Addon("guest"), out var mutation, out var error);
        Assert.Equal(allowed, decoded);
        if (allowed) {
            var cell = Assert.IsType<WorldMutation.RemoveStateCell>(mutation);
            Assert.Equal("config", cell.Row);
            Assert.Equal("seed", cell.Key);
        } else {
            Assert.Null(mutation);
            Assert.NotEmpty(error);
        }
    }
}
