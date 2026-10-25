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
}
