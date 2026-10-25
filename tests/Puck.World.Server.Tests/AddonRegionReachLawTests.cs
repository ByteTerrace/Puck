using System.Security.Cryptography;
using System.Text;
using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.Scripting;
using Puck.Testing;
using Puck.World.Addons;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Real guest delivery and mount narration agree on manifest reach without extending grants.</summary>
[Collection(ConsoleRedirectionCollection.Name)]
public sealed class AddonRegionReachLawTests {
    [InlineData("named")]
    [InlineData("wildcard")]
    [InlineData("denied")]
    [InlineData("empty")]
    [Theory]
    public void RegionEdgesAndMountReportUseTheSameReach(string mode) {
        using var directory = new TemporaryDirectory(prefix: "puck-region-reach-");
        var wildcard = (mode is "wildcard" or "denied");
        var delivered = (mode is "named" or "wildcard");
        var northIndex = (wildcard ? -1 : 0);
        var eastIndex = (wildcard ? -1 : 2);
        var wat = $$"""
            (module
              (memory (export "memory") 1)
              (data (i32.const 256) "\02\00\01\00\00\00\00\00\00\00\00\00\00\00\00\00\03\00\00\00\00\00\00\00\00\00\00\00\00\00\00\00")
              (global $ticks (mut i32) (i32.const 0))
              (func (export "puck_abi_version") (result i32) (i32.const 1))
              (func (export "puck_channels_ptr") (result i32) (i32.const 256))
              (func (export "puck_channels_count") (result i32) (i32.const 2))
              (func (export "puck_out_ptr") (result i32) (i32.const 4096))
              (func (export "puck_out_cap") (result i32) (i32.const 0))
              (func (export "puck_in_ptr") (result i32) (i32.const 8192))
              (func (export "puck_in_cap") (result i32) (i32.const 64))
              (func (export "puck_on_tick") (param $count i32) (result i32)
                (local $i i32) (local $p i32) (local $events i32) (local $b i64)
                (global.set $ticks (i32.add (global.get $ticks) (i32.const 1)))
                (block $done (loop $next
                  (br_if $done (i32.ge_u (local.get $i) (local.get $count)))
                  (local.set $p (i32.add (i32.const 8192) (i32.mul (local.get $i) (i32.const 32))))
                  (if (i32.eq (i32.load8_u (local.get $p)) (i32.const 3)) (then
                    (if (i64.ne (i64.load offset=16 (local.get $p)) (i64.const 0)) (then unreachable))
                    (if (i32.ne (i32.load8_u offset=9 (local.get $p))
                          (select (i32.const 1) (i32.const 2) (i32.eq (global.get $ticks) (i32.const 2))))
                      (then unreachable))
                    (local.set $b (i64.load offset=24 (local.get $p)))
                    (if (i64.ne (local.get $b)
                          (select (i64.const {{eastIndex}}) (i64.const {{northIndex}}) (i32.eqz (local.get $events))))
                      (then unreachable))
                    (local.set $events (i32.add (local.get $events) (i32.const 1)))))
                  (local.set $i (i32.add (local.get $i) (i32.const 1))) (br $next)))
                (if (i32.ne (local.get $events)
                      (select (i32.const {{(delivered ? 2 : 0)}}) (i32.const 0)
                        (i32.or (i32.eq (global.get $ticks) (i32.const 2)) (i32.eq (global.get $ticks) (i32.const 4)))))
                  (then unreachable))
                (i32.const 0)))
            """;
        var bytes = Encoding.UTF8.GetBytes(s: wat);
        var path = directory.PathOf(name: "regions.wat").Replace(newChar: '/', oldChar: '\\');

        File.WriteAllBytes(bytes: bytes, path: path);
        var principal = Principal.Addon(name: "regions");
        var north = GrantSubject.Region(name: "north");
        var east = GrantSubject.Region(name: "east");
        var hidden = GrantSubject.Region(name: "hidden");
        var definition = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var placement = definition.Placements[0]! with {
            Position = new Vector3(x: 0, y: 1000, z: 0),
            Solid = null,
            Region = new WorldPlacementRegion(Radius: 10),
        };

        definition = definition with {
            PlacementsRaw = new WorldPlacementsSection(Rows: [
                placement with { Id = "east" }, placement with { Id = "hidden" }, placement with { Id = "north" },
            ]),
            AddonsRaw = [new WorldAddonRow("regions", path,
                ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes))), 1000000UL, true,
                ((mode == "empty") ? [] : (wildcard ? [new WorldCapabilityRequest(Capability: WorldCapability.Observe, Subject: GrantSubject.All)] : [
                    new WorldCapabilityRequest(Capability: WorldCapability.Observe, Subject: north),
                    new WorldCapabilityRequest(Capability: WorldCapability.Observe, Subject: hidden),
                    new WorldCapabilityRequest(Capability: WorldCapability.Observe, Subject: east),
                    new WorldCapabilityRequest(Capability: WorldCapability.Observe, Subject: north),
                ])))],
            GrantsRaw = ((mode == "denied") ? [] : [
                new WorldGrant(principal, WorldCapability.Observe, north, Exclusive: false, Budget: 4, EventBudget: 4),
                new WorldGrant(principal, WorldCapability.Observe, east, Exclusive: false, Budget: 4, EventBudget: 4),
                new WorldGrant(principal, WorldCapability.Drive, GrantSubject.Body(index: 0), Exclusive: false, Budget: 4),
            ]),
        };
        using var fixture = Fixtures.FreshServer(definition);
        var body = fixture.JoinSeat();
        using var narration = new StringWriter();
        var original = Console.Error;
        WorldAddonRuntime runtime;

        try {
            Console.SetError(newError: narration);
            runtime = WorldAddonRuntime.Create(definition: fixture.Server.Definition, server: fixture.Server);
        } finally {
            Console.SetError(newError: original);
        }
        using (runtime) {
            body.Pose(FixedVector3.FromVector3(value: new Vector3(x: 0, y: 1000, z: 0)), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
            fixture.Step();
            fixture.Step();
            body.Pose(FixedVector3.FromVector3(value: new Vector3(x: 0, y: 2000, z: 0)), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
            fixture.Step();
            fixture.Step();
            var cost = Assert.Single(collection: runtime.DescribeCost());

            Assert.Equal(AddonState.Enabled, cost.State);
            Assert.Equal((delivered ? 4UL : 0UL), cost.EventCellsDelivered);
            Assert.Equal(0UL, cost.EventGaps);
        }
        var report = narration.ToString();

        if (delivered) {
            Assert.Contains(actualString: report, expectedSubstring: "granted: observe/region:east, observe/region:north");
            Assert.Contains(actualString: report, expectedSubstring: ((mode == "wildcard") ? "withheld: (none)" : "withheld: observe/region:hidden"));
            Assert.Contains(actualString: report, expectedSubstring: "holds beyond its manifest (inert — never materialized): drive/body:0");
        } else {
            Assert.Contains(actualString: report, expectedSubstring: "granted: (none)");
            Assert.Contains(actualString: report, expectedSubstring: ((mode == "denied") ? "withheld: observe/all" : "withheld: (none)"));
        }
    }
}
