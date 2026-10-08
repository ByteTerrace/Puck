using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>CPU laws for the explicit presentation slot table, independent of shader packing.</summary>
public sealed class ShadowGpuFrameLawTests {
    [Fact]
    public void SettingALightNeverSelectsOrReseatsAShadowSlot() {
        var lights = new SdfLights { Count = 2 };
        var light = new SdfLight(SdfLightKind.Directional, Vector3.UnitY, Vector3.One, 1f, 0.1f, true);

        lights.Set(index: 1, light: light);
        Assert.Equal(expected: 0, actual: lights.ShadowSlots.SlotCount);
        Assert.Equal(expected: -1, actual: lights.ShadowSlots[0]);
        lights.ShadowSlots.Configure(fadeCapacity: 0, slots: 2);
        lights.ShadowSlots.SetSlot(light: 1, slot: 1);
        lights.Set(index: 0, light: light);
        lights.Set(index: 1, light: light with { Shadows = 0u });
        Assert.Equal(expected: -1, actual: lights.ShadowSlots[0]);
        Assert.Equal(expected: 1, actual: lights.ShadowSlots[1]);
    }
    [Fact]
    public void CopyingLightsPreservesSparseSlotsPolicyAndIndependentHandoffs() {
        var source = new SdfLights();
        var target = new SdfLights();
        var control = new SdfShadowHandoff(Incoming: 3, Outgoing: 6, Slot: 3, Weight: 0.25f);

        source.ShadowSlots.Configure(fadeCapacity: 2, slots: 4);
        source.ShadowSlots.SetSlot(light: 6, slot: 3);
        source.ShadowSlots.SetHandoffs(handoffs: [control]);
        target.CopyFrom(source: source);
        source.ShadowSlots.Configure(fadeCapacity: 0, slots: 0);

        Assert.Equal(expected: 4, actual: target.ShadowSlots.SlotCount);
        Assert.Equal(expected: 2, actual: target.ShadowSlots.FadeCapacity);
        Assert.Equal(expected: 1, actual: target.ShadowSlots.FadeCount);
        Assert.Equal(expected: new[] { -1, -1, -1, 6 }, actual: Enumerable.Range(count: 4, start: 0).Select(selector: slot => target.ShadowSlots[slot]));
        Assert.Equal(expected: control, actual: Assert.Single(collection: target.ShadowSlots.Handoffs.ToArray()));
    }
    [Fact]
    public void ReconfiguringThePolicyClearsAllPriorOwnersAndControls() {
        var slots = new SdfShadowSlots();

        slots.Configure(fadeCapacity: 2, slots: 4);
        slots.SetSlot(light: 7, slot: 3);
        slots.SetHandoffs(handoffs: [new SdfShadowHandoff(Incoming: 2, Outgoing: 7, Slot: 3, Weight: 0.5f)]);
        slots.Configure(fadeCapacity: 0, slots: 0);

        Assert.Equal(expected: 0, actual: slots.SlotCount);
        Assert.Equal(expected: 0, actual: slots.FadeCapacity);
        Assert.Equal(expected: 0, actual: slots.FadeCount);
        Assert.Empty(collection: slots.Handoffs.ToArray());
        Assert.All(collection: Enumerable.Range(count: 4, start: 0), action: slot => Assert.Equal(expected: -1, actual: slots[slot]));
        slots.Configure(fadeCapacity: 1, slots: 1);
        Assert.Equal(expected: -1, actual: slots[0]);
        Assert.Equal(expected: 0, actual: slots.FadeCount);
    }
    [Fact]
    public void AFrameMapsRetainedNamesToVacanciesWithoutCompactingTheAllocatorSlots() {
        var definition = Fixtures.BuildDocument() with {
            RenderRaw = new WorldRenderDefaults(ShadowLights: 3, Lighting: new WorldRenderLighting(Lights: [Light(name: "first", weight: 3f), Light(name: "second", weight: 2f), Light(name: "third", weight: 1f)])),
        };
        var mirror = ClientFixtures.StateMirror(definition: definition);
        var selection = new WorldShadowSelection();

        selection.SetSettings(settings: WorldShadowSettings.From(render: definition.Render));
        selection.Advance(definition: definition, mirror: mirror, revision: 0);
        var preceding = definition with {
            RenderRaw = definition.Render with { Lighting = new WorldRenderLighting(Lights: [Light(name: "third", weight: 1f)]) },
        };
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());
        var frame = resolver.Resolve(definition: preceding, revision: 0, mirror: mirror, shadowSelection: selection);

        Assert.Equal(expected: 3, actual: frame.Lights.ShadowSlots.SlotCount);
        Assert.Equal(expected: -1, actual: frame.Lights.ShadowSlots[0]);
        Assert.Equal(expected: -1, actual: frame.Lights.ShadowSlots[1]);
        Assert.Equal(expected: 0, actual: frame.Lights.ShadowSlots[2]);
        Assert.Equal(expected: 1u, actual: frame.Lights[0].Shadows);
        Assert.Equal(expected: 0, actual: frame.Lights.ShadowSlots.FadeCount);

        static WorldRenderLight.Directional Light(string name, float weight) => new(Name: name, Shadow: WorldShadowMode.Auto, Weight: new BindableScalar(literal: weight));
    }
}
