using System.Numerics;

using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for <see cref="PublishedMachineOutputs"/>: an output a presenter publishes is retired on device loss even when
/// its publish created the upload and then threw, and retiring forgets every output so the next device starts empty.
/// </summary>
public sealed class PublishedMachineOutputsLawTests {
    [Fact]
    public void APublishThatLosesTheDeviceOnItsFirstUploadIsStillRetired() {
        var device = new FakeGpuDevice(reportVersion: 0);
        var machine = new UploadThenLoseOutput();
        var outputs = new PublishedMachineOutputs();

        _ = Assert.Throws<DeviceLostException>(testCode: () => outputs.Publish(
            deviceContext: device,
            gpu: device,
            instance: "cabinet",
            machine: machine,
            output: "screen"
        ));
        Assert.True(condition: machine.HoldsUpload);

        outputs.Retire(resolve: (instance, output) => (((instance == "cabinet") && (output == "screen")) ? machine : null));

        Assert.False(condition: machine.HoldsUpload);
        Assert.Equal(expected: 1, actual: machine.Retirements);
        Assert.Equal(expected: 0, actual: outputs.Count);
    }
    [Fact]
    public void RetiringForgetsEveryOutputAndSkipsARemovedInstance() {
        var device = new FakeGpuDevice(reportVersion: 0);
        var kept = new UploadThenLoseOutput { LosesDevice = false };
        var removed = new UploadThenLoseOutput { LosesDevice = false };
        var outputs = new PublishedMachineOutputs();

        outputs.Publish(deviceContext: device, gpu: device, instance: "kept", machine: kept, output: "screen");
        outputs.Publish(deviceContext: device, gpu: device, instance: "removed", machine: removed, output: "screen");
        outputs.Publish(deviceContext: device, gpu: device, instance: "kept", machine: kept, output: "screen");
        Assert.Equal(expected: 2, actual: outputs.Count);

        outputs.Retire(resolve: (instance, _) => ((instance == "kept") ? kept : null));
        outputs.Retire(resolve: (_, _) => kept);

        Assert.Equal(expected: (1, false), actual: (kept.Retirements, kept.HoldsUpload));
        Assert.Equal(expected: (0, true), actual: (removed.Retirements, removed.HoldsUpload));
        Assert.Equal(expected: 0, actual: outputs.Count);
    }

    // Models QueuedMachineWorker's publish: the upload is created on the device before the submit that can lose it.
    private sealed class UploadThenLoseOutput : IMachineVideoOutput {
        public Vector3 EmittedLight => Vector3.Zero;
        public bool HoldsUpload { get; private set; }
        public bool LosesDevice { get; init; } = true;
        public nint NativeImageViewHandle => 0;
        public int Retirements { get; private set; }

        public void NotifyDeviceLost() {
            HoldsUpload = false;
            ++Retirements;
        }
        public void PublishFrame(IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
            HoldsUpload = true;

            if (LosesDevice) {
                throw new DeviceLostException(message: "the submit found the device lost");
            }
        }
    }
}
