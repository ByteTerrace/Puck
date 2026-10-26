using System.Security.Cryptography;
using System.Text;
using Puck.Commands;
using Puck.Scripting;
using Puck.Testing;
using Puck.World.Addons;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Exercises bulk memory, authority attenuation, shared dispatch budgets and fuel through a real guest.</summary>
public sealed class AddonBodyPosesLawTests {
    [Theory]
    [InlineData("bulk")]
    [InlineData("single-first")]
    [InlineData("bad-input")]
    [InlineData("bad-output")]
    [InlineData("bad-count")]
    [InlineData("outer-handle")]
    [InlineData("fuel")]
    public void BulkReadsStayInsideMemoryAuthorityBudgetAndFuel(string mode) {
        using var directory = new TemporaryDirectory(prefix: "puck-body-poses-");
        var malformed = mode is "bad-input" or "bad-output" or "bad-count" or "outer-handle";
        var singleFirst = mode == "single-first";
        var bulkOrdinal = singleFirst ? 1 : 0;
        var firstVerdict = singleFirst ? 8 : 1;
        var check = malformed
            ? "(if (i64.ne (i64.load (i32.const 16384)) (i64.const 123)) (then unreachable))"
            : $$"""
              (if (i32.ne (i32.load8_u (i32.const 16388)) (i32.const {{firstVerdict}})) (then unreachable))
              (if (i32.ne (i32.load8_u (i32.const 16452)) (i32.const 8)) (then unreachable))
              (if (i32.ne (i32.load8_u (i32.const 16516)) (i32.const 6)) (then unreachable))
              (if (i32.ne (i32.load8_u (i32.const 16580)) (i32.const 9)) (then unreachable))
              (if (i32.ne (i32.load (i32.const 16448)) (i32.const -1)) (then unreachable))
              (if (i64.ne (i64.load (i32.const 16464)) (i64.const 0)) (then unreachable))
              (if (i64.ne (i64.load (i32.const 16504)) (i64.const 0)) (then unreachable))
              {{(singleFirst ? "" : "(if (i64.ne (i64.load (i32.const 16440)) (i64.const 65536)) (then unreachable))")}}
              """;
        var wat = $$"""
            (module
              (memory (export "memory") 1)
              (data (i32.const 256) "\02\00\0e\00\00\00\00\00\00\00\00\00\00\00\00\00\03\00\00\00\00\00\00\00\00\00\00\00\00\00\00\00")
              ;; Two body-0 handles, a held but unrequested body-1 handle, and an invalid generation.
              (data (i32.const 12288) "\00\00\00\00\00\00\00\00\01\00\01\00\00\00\ff\ff")
              (data (i32.const 16384) "\7b\00\00\00\00\00\00\00")
              (global $ran (mut i32) (i32.const 0))
              (func (export "puck_abi_version") (result i32) (i32.const 1))
              (func (export "puck_channels_ptr") (result i32) (i32.const 256))
              (func (export "puck_channels_count") (result i32) (i32.const 2))
              (func (export "puck_out_ptr") (result i32) (i32.const 4096))
              (func (export "puck_out_cap") (result i32) (i32.const 2))
              (func (export "puck_in_ptr") (result i32) (i32.const 8192))
              (func (export "puck_in_cap") (result i32) (i32.const 8))
              (func (export "puck_on_tick") (param $count i32) (result i32)
                (local $i i32) (local $p i32) (local $answered i32)
                (if (global.get $ran) (then
                  (loop $answers
                    (local.set $p (i32.add (i32.const 8192) (i32.mul (local.get $i) (i32.const 32))))
                    (if (i32.and (i32.eq (i32.load8_u (local.get $p)) (i32.const 2))
                          (i32.eq (i32.load16_u offset=2 (local.get $p)) (i32.const {{bulkOrdinal}})))
                      (then
                        (local.set $answered (i32.const 1))
                        (if (i32.ne (i32.load8_u offset=8 (local.get $p)) (i32.const {{(malformed ? 11 : 10)}})) (then unreachable))))
                    (local.set $i (i32.add (local.get $i) (i32.const 1)))
                    (br_if $answers (i32.lt_u (local.get $i) (local.get $count))))
                  (if (i32.eqz (local.get $answered)) (then unreachable))
                  {{check}}
                  (return (i32.const 0))))
                (global.set $ran (i32.const 1))
                {{(singleFirst ? "(i32.store8 (i32.const 4096) (i32.const 1))" : "")}}
                (i32.store8 (i32.const {{4096 + bulkOrdinal * 32}}) (i32.const 1))
                (i32.store16 (i32.const {{4098 + bulkOrdinal * 32}}) (i32.const {{(mode == "outer-handle" ? 1 : 0)}}))
                (i32.store16 (i32.const {{4102 + bulkOrdinal * 32}}) (i32.const 13))
                (i64.store (i32.const {{4104 + bulkOrdinal * 32}}) (i64.const {{(mode == "bad-input" ? -1 : 12288)}}))
                (i64.store (i32.const {{4112 + bulkOrdinal * 32}}) (i64.const {{(mode == "bad-count" ? 4097 : mode == "fuel" ? 4096 : 4)}}))
                (i64.store (i32.const {{4120 + bulkOrdinal * 32}}) (i64.const {{(mode == "bad-output" ? 65535 : 16384)}}))
                (i32.const {{bulkOrdinal + 1}})))
            """;
        var bytes = Encoding.UTF8.GetBytes(wat);
        var path = directory.PathOf("guest.wat").Replace('\\', '/');
        File.WriteAllBytes(path, bytes);
        var principal = Principal.Addon("batch");
        var definition = Fixtures.BuildDocument() with {
            AddonsRaw = [new WorldAddonRow("batch", path,
                "sha256/" + Convert.ToHexStringLower(SHA256.HashData(bytes)), mode == "fuel" ? 50000UL : 1000000UL, true,
                [new WorldCapabilityRequest(WorldCapability.Observe, GrantSubject.Body(0))])],
            GrantsRaw = [
                new WorldGrant(principal, WorldCapability.Observe, GrantSubject.Body(0), Exclusive: false, Budget: 1),
                new WorldGrant(principal, WorldCapability.Observe, GrantSubject.Body(1), Exclusive: false, Budget: 1),
            ],
        };
        using var fixture = Fixtures.FreshServer(definition);
        fixture.JoinSeat();
        // The fabricated second handle really is held; only the manifest prevents its pose from leaking.
        Assert.True(fixture.Server.Grants.HandleTable(principal, WorldCapability.Observe).TryMintFor(GrantSubject.Body(1), out var withheld));
        Assert.Equal(1, withheld.Index);
        Assert.Equal(1, withheld.Generation);
        using var runtime = WorldAddonRuntime.Create(fixture.Server.Definition, fixture.Server);
        fixture.Step();
        fixture.Step();
        var cost = Assert.Single(runtime.DescribeCost());
        Assert.Equal(mode == "fuel" ? AddonState.Faulted : AddonState.Enabled, cost.State);
        Assert.Equal(0UL, cost.TotalAnswersDropped);
        if (mode == "fuel") { Assert.Contains("OutOfFuel", cost.FaultDetail); }
    }
    [Fact]
    public void ReadStateTextWithMalformedCellKeyRefusesInsteadOfThrowingFormatException() {
        using var directory = new TemporaryDirectory(prefix: "puck-read-state-text-");
        const string Wat = """
            (module
              (memory (export "memory") 1)
              (data (i32.const 256) "\02\00\11\00\00\00\00\00\00\00\00\00\00\00\00\00\03\00\00\00\00\00\00\00\00\00\00\00\00\00\00\00")
              (global $ran (mut i32) (i32.const 0))
              (func (export "puck_abi_version") (result i32) (i32.const 1))
              (func (export "puck_channels_ptr") (result i32) (i32.const 256))
              (func (export "puck_channels_count") (result i32) (i32.const 2))
              (func (export "puck_out_ptr") (result i32) (i32.const 4096))
              (func (export "puck_out_cap") (result i32) (i32.const 1))
              (func (export "puck_in_ptr") (result i32) (i32.const 8192))
              (func (export "puck_in_cap") (result i32) (i32.const 4))
              (func (export "puck_on_tick") (param $count i32) (result i32)
                (if (global.get $ran) (then (return (i32.const 0))))
                (global.set $ran (i32.const 1))
                (i32.store8 (i32.const 4096) (i32.const 1))
                (i32.store16 (i32.const 4098) (i32.const 0))
                (i32.store16 (i32.const 4100) (i32.const 0))
                (i32.store16 (i32.const 4102) (i32.const 6))
                (i64.store (i32.const 4104) (i64.const 0))
                (i64.store (i32.const 4112) (i64.const 0))
                (i64.store (i32.const 4120) (i64.const 0))
                (i32.const 1)))
            """;
        var bytes = Encoding.UTF8.GetBytes(s: Wat);
        var path = directory.PathOf(name: "reader.wat").Replace(oldChar: '\\', newChar: '/');

        File.WriteAllBytes(path: path, bytes: bytes);

        var principal = Principal.Addon(name: "reader");
        var subject = GrantSubject.State(name: "notes");
        var definition = Fixtures.BuildDocument().WithWorldState(rows: [
            new WorldStateRow(
                Name: CellName.Parse(candidate: "notes"),
                Kind: CellKind.Text,
                Capacity: 4,
                Cells: [new StateCell(Key: CellName.Parse(candidate: "greeting"), Value: CellValue.Text(value: "hello"))]
            ),
        ]) with {
            AddonsRaw = [new WorldAddonRow("reader", path,
                ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes))), 100000UL, true,
                [new WorldCapabilityRequest(Capability: WorldCapability.Observe, Subject: subject)])],
            GrantsRaw = [new WorldGrant(
                Grantee: principal,
                Capability: WorldCapability.Observe,
                Subject: subject,
                Exclusive: false,
                Budget: 4
            )],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        Assert.True(condition: fixture.Server.Grants.HandleTable(principal: principal, capability: WorldCapability.Observe).TryMintFor(subject: subject, handle: out var handle));
        Assert.Equal(0, handle.Index);
        Assert.Equal(0, handle.Generation);
        using var runtime = WorldAddonRuntime.Create(definition: fixture.Server.Definition, server: fixture.Server);

        fixture.Step();
        fixture.Step();

        var cost = Assert.Single(collection: runtime.DescribeCost());

        Assert.Equal(AddonState.Enabled, cost.State);
    }
    [Fact]
    public void OutOfFuelDuringHostChargedRequestStopsRemainingBatchEntries() {
        using var directory = new TemporaryDirectory(prefix: "puck-fuel-batch-");
        const string Wat = """
            (module
              (memory (export "memory") 1)
              (data (i32.const 256) "\02\00\11\00\00\00\00\00\00\00\00\00\00\00\00\00\03\00\00\00\00\00\00\00\00\00\00\00\00\00\00\00")
              (data (i32.const 12288) "{\"name\":\"counter\"}")
              (func (export "puck_abi_version") (result i32) (i32.const 1))
              (func (export "puck_channels_ptr") (result i32) (i32.const 256))
              (func (export "puck_channels_count") (result i32) (i32.const 2))
              (func (export "puck_out_ptr") (result i32) (i32.const 4096))
              (func (export "puck_out_cap") (result i32) (i32.const 2))
              (func (export "puck_in_ptr") (result i32) (i32.const 8192))
              (func (export "puck_in_cap") (result i32) (i32.const 4))
              (func (export "puck_on_tick") (param $count i32) (result i32)
                (local $burn i32)
                (local.set $burn (i32.const 7000))
                (loop $work
                  (local.set $burn (i32.sub (local.get $burn) (i32.const 1)))
                  (br_if $work (local.get $burn)))
                ;; First cell: UpsertStateCell (47) with length 8000 that exhausts remaining fuel.
                (i32.store8 (i32.const 4096) (i32.const 1))
                (i32.store16 (i32.const 4098) (i32.const 0))
                (i32.store16 (i32.const 4100) (i32.const 0))
                (i32.store16 (i32.const 4102) (i32.const 1))
                (i64.store (i32.const 4104) (i64.const 47))
                (i64.store (i32.const 4112) (i64.const 12288))
                (i64.store (i32.const 4120) (i64.const 8000))
                ;; Second cell in the same batch: RemoveStateRow (45) that must not decode or enqueue after OutOfFuel.
                (i32.store8 (i32.const 4128) (i32.const 1))
                (i32.store16 (i32.const 4130) (i32.const 0))
                (i32.store16 (i32.const 4132) (i32.const 0))
                (i32.store16 (i32.const 4134) (i32.const 1))
                (i64.store (i32.const 4136) (i64.const 45))
                (i64.store (i32.const 4144) (i64.const 12288))
                (i64.store (i32.const 4152) (i64.const 18))
                (i32.const 2)))
            """;
        var bytes = Encoding.UTF8.GetBytes(s: Wat);
        var path = directory.PathOf(name: "mutator.wat").Replace(oldChar: '\\', newChar: '/');

        File.WriteAllBytes(path: path, bytes: bytes);

        var principal = Principal.Addon(name: "mutator");
        var sectionSubject = GrantSubject.Section(section: WorldSection.State);
        var rowSubject = GrantSubject.State(name: "counter");
        var definition = Fixtures.BuildDocument().WithWorldState(rows: [
            new WorldStateRow(
                Name: CellName.Parse(candidate: "counter"),
                Kind: CellKind.Int,
                Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Int(value: 1L))]
            ),
        ]) with {
            AddonsRaw = [new WorldAddonRow("mutator", path,
                ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes))), 50000UL, true, [
                    new WorldCapabilityRequest(Capability: WorldCapability.Mutate, Subject: sectionSubject),
                    new WorldCapabilityRequest(Capability: WorldCapability.Edit, Subject: rowSubject),
                ])],
            GrantsRaw = [
                new WorldGrant(
                    Grantee: principal,
                    Capability: WorldCapability.Mutate,
                    Subject: sectionSubject,
                    Exclusive: false,
                    Budget: 8,
                    KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.State)
                ),
                new WorldGrant(
                    Grantee: principal,
                    Capability: WorldCapability.Edit,
                    Subject: rowSubject,
                    Exclusive: false
                ),
            ],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        Assert.True(condition: fixture.Server.Grants.HandleTable(principal: principal, capability: WorldCapability.Mutate).TryMintFor(subject: sectionSubject, handle: out var handle));
        Assert.Equal(0, handle.Index);
        Assert.Equal(0, handle.Generation);
        using var runtime = WorldAddonRuntime.Create(definition: fixture.Server.Definition, server: fixture.Server);

        fixture.Step();

        var cost = Assert.Single(collection: runtime.DescribeCost());

        Assert.Equal(AddonState.Faulted, cost.State);
        Assert.Contains("OutOfFuel during host query", cost.FaultDetail);
        Assert.Single(collection: fixture.Server.ExportDefinition().State);
    }
}
