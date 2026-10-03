using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesWorkLawTests {
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Theory]
    public void OnlyActiveHandoffsUploadWholeControlRecords(int active) {
        using var rig = new Rig();
        var slots = rig.Frame.Lights.ShadowSlots;

        slots.Configure(fadeCapacity: 2, slots: 2);
        slots.SetSlot(light: 0, slot: 0);
        slots.SetSlot(light: 1, slot: 1);
        for (var warm = 0; (warm < 3); warm++) {
            rig.Render();
        }
        var bytes = rig.Engine.TableBytes;
        SdfShadowHandoff[] controls = [new(Incoming: 2, Outgoing: 0, Slot: 0, Weight: 0.25f), new(Incoming: 3, Outgoing: 1, Slot: 1, Weight: 0.75f)];

        slots.SetHandoffs(handoffs: controls.AsSpan(length: active, start: 0));
        rig.Render();
        rig.Render();
        var sample = new GpuWorkSample();

        Assert.True(condition: rig.Engine.Work.TryReadCompleted(sample: sample));
        var pass = sample.PassLabels.IndexOf(value: "upload");
        var column = Array.IndexOf(array: GpuWork.SubmissionKinds.ToArray(), value: GpuWork.HostVisibleUploadBytes);

        Assert.True(condition: sample.TryGetPassCount(column: column, pass: pass, value: out var uploaded));
        Assert.Equal(actual: uploaded, expected: ((active == 0) ? 0 : ((active * 16) + ((GpuRegion.CopyHeaderWords + 2) * sizeof(uint)))));
        Assert.Equal(expected: bytes, actual: rig.Engine.TableBytes);
        slots.SetHandoffs(handoffs: []);
        rig.Render();
        rig.Render();
        Assert.True(condition: rig.Engine.Work.TryReadCompleted(sample: sample));
        Assert.True(condition: sample.TryGetPassCount(column: column, pass: pass, value: out uploaded));
        Assert.Equal(actual: uploaded, expected: 0);
    }
    [Fact]
    public void HandoffWeightChangesCadenceWithoutChangingTheSlotTable() {
        using var rig = new Rig();
        var slots = rig.Frame.Lights.ShadowSlots;

        slots.Configure(fadeCapacity: 1, slots: 1);
        slots.SetSlot(light: 0, slot: 0);
        slots.SetHandoffs(handoffs: [new(Incoming: 1, Outgoing: 0, Slot: 0, Weight: 0.25f)]);
        rig.Engine.Pack(frame: rig.Frame);
        rig.Engine.UpdateTablesSignature();
        var first = rig.Engine.ViewSignature(frame: rig.Frame, view: 0);

        slots.SetHandoffs(handoffs: [new(Incoming: 1, Outgoing: 0, Slot: 0, Weight: 0.75f)]);
        rig.Engine.Pack(frame: rig.Frame);
        rig.Engine.UpdateTablesSignature();
        Assert.NotEqual(expected: first, actual: rig.Engine.ViewSignature(frame: rig.Frame, view: 0));
    }
}
