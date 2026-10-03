using System.Numerics;

using Puck.SignedDistance;

using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Exercises <see cref="SdfLights.Pack"/> and <see cref="SdfSky.Pack"/>, which need no GPU device: the exact
/// records the tables write into the lights table and the sky's block and tables, with the host bakes.</summary>
public sealed class PackLightsAndSkyLawTests {
    private static SdfLight[] Packed(SdfLights lights) {
        var records = new SdfLight[SdfLights.MaxLights];

        lights.Pack(records: records);

        return records;
    }
    private static SdfLight Light(SdfLightKind kind, Vector3 direction, float weight, bool shadows = false) => new(
        Color: Vector3.One,
        Direction: direction,
        Kind: kind,
        Param: 0.1f,
        Shadows: shadows,
        Weight: weight
    );
    private static SdfSkyBlock Sky(SdfSky sky, SdfLights lights) {
        sky.Pack(
            block: out var block,
            lights: lights,
            softboxes: new SdfSoftbox[SdfSky.MaxSoftboxes],
            stops: new SdfSkyStop[SdfSky.MaxStops]
        );

        return block;
    }

    [Fact]
    public void FifthLight_PacksIntoItsOwnRecord() {
        var lights = new SdfLights { Count = 5 };

        lights.Set(index: 0, light: Light(direction: Vector3.UnitY, kind: SdfLightKind.Directional, shadows: true, weight: 0.1f));
        lights.Set(index: 1, light: Light(direction: Vector3.UnitX, kind: SdfLightKind.Directional, weight: 0.2f));
        lights.Set(index: 2, light: Light(direction: Vector3.Zero, kind: SdfLightKind.Hemisphere, weight: 0.3f));
        lights.Set(index: 3, light: Light(direction: Vector3.Zero, kind: SdfLightKind.Rim, weight: 0.4f));
        lights.Set(index: 4, light: Light(direction: new Vector3(x: 0f, y: 0f, z: 2f), kind: SdfLightKind.Directional, weight: 0.9f));
        lights.ShadowSlots.Configure(fadeCapacity: 0, slots: 1);
        lights.ShadowSlots.SetSlot(light: 0, slot: 0);

        var records = Packed(lights: lights);

        Assert.Equal(expected: 5, actual: lights.Count);
        Assert.Equal(expected: 0, actual: lights.ShadowSlots[0]);
        // A directional packs its direction normalized.
        Assert.Equal(expected: Vector3.UnitZ, actual: records[4].Direction);
        Assert.Equal(expected: 0.9f, actual: records[4].Weight);
        Assert.Equal(expected: SdfLightKind.Directional, actual: records[4].Kind);
    }
    [Fact]
    public void OccluderPositionAndAnchorPackWithoutDirectionNormalization() {
        var lights = new SdfLights { Count = 1 };

        lights.Set(
            index: 0,
            light: new(
                SdfLightKind.Occluder,
                new(
                    x: 12f,
                    y: 3f,
                    z: -4f
                ),
                Vector3.Zero,
                0.6f,
                2.5f,
                false,
                7
            )
        );
        var record = Packed(lights: lights)[0];

        Assert.Equal(expected: new Vector3(x: 12f, y: 3f, z: -4f), actual: record.Direction);
        Assert.Equal(actual: record.Weight, expected: 0.6f);
        Assert.Equal(actual: record.Kind, expected: SdfLightKind.Occluder);
        Assert.Equal(actual: record.Param, expected: 2.5f);
        Assert.Equal(actual: record.DynamicSlot, expected: 7);
    }
    // A point or occluder light packs its slot for the kernels to test against SDF_TRANSFORM_SLOT_NONE, so it is the
    // sentinel or a slot a program can name, and nothing else; every other kind packs no slot, so its value is ignored.
    [Fact]
    public void ALightPacksTheStaticSentinelOrASlotAndNothingElse() {
        var lights = new SdfLights { Count = 1 };

        foreach (var kind in ((SdfLightKind[])[SdfLightKind.Point, SdfLightKind.Occluder])) {
            foreach (var slot in ((int[])[SdfProgram.NoDynamicTransformSlot, 0, SdfProgram.MaxDynamicTransformSlot])) {
                lights.Set(index: 0, light: Light(kind: kind, slot: slot));
                Assert.Equal(expected: slot, actual: lights[0].DynamicSlot);
            }
            foreach (var slot in ((int[])[(SdfProgram.NoDynamicTransformSlot - 1), (SdfProgram.MaxDynamicTransformSlot + 1)])) {
                Assert.Throws<ArgumentOutOfRangeException>(testCode: () => lights.Set(index: 0, light: Light(kind: kind, slot: slot)));
            }
        }
        lights.Set(index: 0, light: (Light(kind: SdfLightKind.Directional, slot: 0) with { DynamicSlot = (SdfProgram.NoDynamicTransformSlot - 1) }));
        Assert.Equal(expected: 0, actual: lights[0].DynamicSlot);

        static SdfLight Light(SdfLightKind kind, int slot) => new(
            Color: Vector3.One,
            Direction: Vector3.UnitY,
            DynamicSlot: slot,
            Kind: kind,
            Param: 1f,
            Shadows: false,
            Weight: 1f
        );
    }
    // The sky block carries what the sky draws by, baked from the lights so the sky pass reads no light: the disc's
    // direction is its light's packed direction, and the clouds are lit by stable slot zero, or the pinned sun and
    // white when that slot is vacant.
    [Fact]
    public void TheSkyBakesItsDiscAndCloudLightFromTheLights() {
        var lights = new SdfLights { Count = 2 };

        lights.Set(index: 0, light: Light(direction: new Vector3(x: 0f, y: 3f, z: 0f), kind: SdfLightKind.Directional, weight: 1f));
        lights.Set(index: 1, light: (Light(direction: new Vector3(x: 2f, y: 0f, z: 0f), kind: SdfLightKind.Directional, shadows: true, weight: 1f) with { Color = new Vector3(x: 1f, y: 0.5f, z: 0.25f) }));
        lights.ShadowSlots.Configure(fadeCapacity: 0, slots: 1);
        lights.ShadowSlots.SetSlot(light: 1, slot: 0);

        var sky = new SdfSky { SunDiscRadians = 0.05f };

        sky.Block.DiscLight = 0;

        var block = Sky(lights: lights, sky: sky);

        Assert.Equal(expected: Vector3.UnitY, actual: block.DiscDirection);
        Assert.Equal(expected: ((float)(Math.Log(d: 0.5d) / Math.Log(d: Math.Cos(d: 0.05d)))), actual: block.DiscExponent);
        Assert.Equal(expected: Vector3.UnitX, actual: block.CloudLightDirection);
        Assert.Equal(expected: new Vector3(x: 1f, y: 0.5f, z: 0.25f), actual: block.CloudLightColor);

        var unlit = Sky(sky: new SdfSky(), lights: new SdfLights());

        Assert.Equal(actual: unlit.DiscLight, expected: -1);
        Assert.Equal(expected: SdfLights.DefaultSunDirection, actual: unlit.CloudLightDirection);
        Assert.Equal(expected: Vector3.One, actual: unlit.CloudLightColor);
    }
}
