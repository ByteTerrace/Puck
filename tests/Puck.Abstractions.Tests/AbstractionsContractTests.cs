using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Pacing;
using Puck.Abstractions.Presentation;

namespace Puck.Abstractions.Tests;

public sealed class AbstractionsContractTests {
    [Fact]
    public void SurfaceFactoriesCreateExclusiveVariants() {
        var cpu = Surface.CpuPixels(new byte[8], width: 2, height: 1, SurfaceFormat.R8G8B8A8Unorm);
        var image = Surface.SameDeviceImage(imageViewHandle: 17, width: 1, height: 1, SurfaceFormat.B8G8R8A8Unorm);
        var shared = Surface.SharedTexture(sharedHandle: 23, width: 1, height: 1, SurfaceFormat.R8G8B8A8Unorm);

        Assert.Equal(SurfaceKind.CpuPixels, cpu.Kind);
        Assert.True(cpu.IsCpuPixels);
        Assert.Equal(0, cpu.ImageViewHandle);
        Assert.Equal(0, cpu.SharedHandle);
        Assert.True(image.IsSameDeviceImage);
        Assert.True(shared.IsSharedHandle);
        Assert.True(default(Surface).IsEmpty);
    }

    [Fact]
    public void CpuSurfaceRejectsMismatchedPackedStorage() {
        _ = Assert.Throws<ArgumentException>(() => Surface.CpuPixels(new byte[3], 1, 1, SurfaceFormat.R8G8B8A8Unorm));
        _ = Assert.Throws<ArgumentException>(() => Surface.CpuPixels(new byte[5], 1, 1, SurfaceFormat.R8G8B8A8Unorm));
    }

    [Fact]
    public void DeviceLocalFactoryMethodsDoNotReturnHostWritableType() {
        var factoryType = typeof(IGpuStorageBufferFactory);

        Assert.Equal(typeof(IGpuBuffer), factoryType.GetMethod(nameof(IGpuStorageBufferFactory.CreateDeviceLocal))!.ReturnType);
        Assert.Equal(typeof(IGpuBuffer), factoryType.GetMethod(nameof(IGpuStorageBufferFactory.CreateDeviceLocalIndirectArgs))!.ReturnType);
    }

    [Fact]
    public void PushConstantsRequireDefinedStagesAndWordAlignment() {
        _ = new GpuPushConstantBinding(0, GpuShaderStage.Compute, new byte[4]);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new GpuPushConstantBinding(2, GpuShaderStage.Compute, new byte[4]));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new GpuPushConstantBinding(0, GpuShaderStage.Compute, new byte[3]));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new GpuPushConstantBinding(0, (GpuShaderStage)0x8000_0000u, new byte[4]));
    }

    [Fact]
    public void ShaderValidationWalksContainerStructure() {
        var spirV = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(spirV, 0x07230203);
        BinaryPrimitives.WriteUInt32LittleEndian(spirV.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(spirV.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(spirV.AsSpan(20), 0x00010000);
        ShaderBytecode.ValidateFormat(spirV);

        var truncatedInstruction = spirV.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(truncatedInstruction.AsSpan(20), 0x00020000);
        _ = Assert.Throws<ArgumentException>(() => ShaderBytecode.ValidateFormat(truncatedInstruction));

        var dxbc = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(dxbc, 0x43425844);
        BinaryPrimitives.WriteUInt32LittleEndian(dxbc.AsSpan(24), (uint)dxbc.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(dxbc.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(dxbc.AsSpan(32), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(dxbc.AsSpan(36), 0x4c495844);
        ShaderBytecode.ValidateFormat(dxbc);

        BinaryPrimitives.WriteUInt32LittleEndian(dxbc.AsSpan(40), 1);
        _ = Assert.Throws<ArgumentException>(() => ShaderBytecode.ValidateFormat(dxbc));
    }

    [Fact]
    public void DescriptorSizingRejectsInvalidSetsAndOverflow() {
        _ = Assert.Throws<ArgumentException>(() => GpuDescriptorPoolSizes.ForSets([default(GpuComputeBinding)]));
        _ = Assert.Throws<ArgumentException>(() => GpuDescriptorPoolSizes.ForSets([
            new GpuComputeBinding(0, GpuComputeBindingKind.StorageImage),
            new GpuComputeBinding(0, GpuComputeBindingKind.StorageBufferRead)
        ]));
        _ = Assert.Throws<OverflowException>(() => GpuDescriptorPoolSizes.ForSets([
            new GpuComputeBinding(0, GpuComputeBindingKind.StorageImage, uint.MaxValue),
            new GpuComputeBinding(1, GpuComputeBindingKind.StorageImage)
        ]));
    }

    [Fact]
    public void ExternalClocksAcceptZeroTimestampAndUnregister() {
        var clock = new ExternalPresentClock();
        clock.Publish(arrivalTimestamp: 0, frameVersion: 7);
        Assert.True(clock.TryRead(out var timestamp, out var version));
        Assert.Equal(0, timestamp);
        Assert.Equal(7, version);

        var registry = new ExternalClockRegistry();
        var source = registry.RegisterSource("camera:0");
        source.Publish(arrivalTimestamp: 11, frameVersion: 1);
        Assert.True(registry.PacerClock.TryRead(out timestamp, out version));
        source.Dispose();
        Assert.Empty(registry.SourceIds);
        _ = Assert.Throws<ObjectDisposedException>(() => source.Publish(12, 2));
    }

    [Fact]
    public void NormalizedInputAndCameraRejectNonFiniteValues() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new MachinePadState(default, new Vector2(float.NaN, 0), default, 0, 0));
        var pad = new MachinePadState(MachineButtons.South, new Vector2(0.5f, -0.5f), default, 0.25f, 0.75f);
        var neutral = MachinePadState.Neutral;
        Assert.Equal(pad, MachinePadState.Merge(in pad, in neutral));

        _ = Assert.Throws<ArgumentException>(() => CameraSnapshot.LookAt(new Vector3(float.NaN, 0, 0), Vector3.Zero, 1f, 1, 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => CameraSnapshot.LookAt(Vector3.Zero, -Vector3.UnitZ, 1f, 1, 0));
    }
}
