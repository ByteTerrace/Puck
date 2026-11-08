using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    // A program's identity is its content's hash, so each name reads its own input.
    private static SdfShadeDispatch ShadePlan(string name) => SdfShadeDispatch.Plan(width: 8, height: 8, lights: 0, glass: false,
        programs: [SdfShadeProgram.Compile(new(name, [], [new(SdfShadePhase.Surface, "roughness", new("input", Name: name switch {
            "first" or "released" => "clock",
            "second" or "interrupted" => "material.roughness",
            _ => "lanes.world2",
        }))]))]);
    private static SdfWorldResidency ShadeResidency(string name) =>
        new(SdfTestPipelines.Cache(), new FixedFrameSource(frame: Frame()), SdfTestPipelines.Kernels(), name, Extent, Extent);

    [Fact]
    public void AShadeSubmissionLeavesOnlyWhenItsSlotIsWaitedAndASlotWithoutARecordingSubmitsNothing() {
        using var residency = ShadeResidency(name: "shade");
        var slots = new SdfShadeSlots(slots: 2);
        var first = ShadePlan(name: "first");

        slots.Recorded(slot: 0, residency: residency, plan: first);
        slots.Submitted(slot: 0);
        Assert.Equal(first.Identities, residency.InFlightShadeIdentities);

        // The pass skipped or stood on slot 1, and on slot 0's next frame: nothing it recorded earlier is submitted again.
        slots.Submitted(slot: 1);
        slots.Completed(slot: 0);
        Assert.Empty(residency.InFlightShadeIdentities);
        slots.Submitted(slot: 0);
        Assert.Empty(residency.InFlightShadeIdentities);

        // A recording abandoned before its submission never reaches the residency.
        slots.Recorded(slot: 1, residency: residency, plan: first);
        slots.Completed(slot: 1);
        slots.Submitted(slot: 1);
        Assert.Empty(residency.InFlightShadeIdentities);
    }
    [Fact]
    public void ASwitchedRecorderRetiresEachSubmissionOnTheResidencyThatRecordedIt() {
        using var before = ShadeResidency(name: "before");
        using var after = ShadeResidency(name: "after");
        var slots = new SdfShadeSlots(slots: 2);
        var first = ShadePlan(name: "first");
        var second = ShadePlan(name: "second");

        slots.Recorded(slot: 0, residency: before, plan: first);
        slots.Submitted(slot: 0);
        slots.Recorded(slot: 1, residency: after, plan: second);
        slots.Submitted(slot: 1);
        Assert.Equal(first.Identities, before.InFlightShadeIdentities);
        Assert.Equal(second.Identities, after.InFlightShadeIdentities);

        slots.Completed(slot: 0);
        slots.Recorded(slot: 0, residency: after, plan: second);
        slots.Submitted(slot: 0);
        Assert.Empty(before.InFlightShadeIdentities);
        slots.Completed(slot: 1);
        slots.Completed(slot: 0);
        Assert.Empty(after.InFlightShadeIdentities);
    }
    [Fact]
    public void ASubmissionALossInterruptsStaysInFlightWhileAReleaseBeforeTheLossCompletes() {
        using var residency = ShadeResidency(name: "lost");
        var releases = new SdfShadeReleases();
        var completed = new SdfShadeSlots(slots: 1);
        var lost = new SdfShadeSlots(slots: 1);
        var late = new SdfShadeSlots(slots: 1);
        var released = ShadePlan(name: "released");
        var interrupted = ShadePlan(name: "interrupted");
        var teardown = ShadePlan(name: "teardown");

        completed.Recorded(slot: 0, residency: residency, plan: released);
        completed.Submitted(slot: 0);
        completed.Release(releases: releases);
        releases.BeginFrame();
        Assert.Empty(residency.InFlightShadeIdentities);

        // Released by the loss's teardown, before and after the package hears of the loss: neither is ever completed.
        lost.Recorded(slot: 0, residency: residency, plan: interrupted);
        lost.Submitted(slot: 0);
        late.Recorded(slot: 0, residency: residency, plan: teardown);
        late.Submitted(slot: 0);
        lost.Release(releases: releases);
        releases.DeviceLost();
        residency.OnDeviceLost();
        late.Release(releases: releases);
        releases.BeginFrame();
        releases.BeginFrame();
        Assert.Equal(interrupted.Identities.Concat(teardown.Identities).Order(StringComparer.Ordinal), residency.InFlightShadeIdentities);
    }
}
