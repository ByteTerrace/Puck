using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Presentation;

namespace Puck.Abstractions.Tests;

public sealed class AbstractionsContractTests {
    [Fact]
    public void SurfaceFactoriesCreateExclusiveVariants() {
        var cpu = Surface.CpuPixels(new byte[8], width: 2, height: 1, SurfaceFormat.R8G8B8A8Unorm);
        var image = Surface.SameDeviceImage(imageHandle: 16, imageViewHandle: 17, width: 1, height: 1, SurfaceFormat.B8G8R8A8Unorm);
        var shared = Surface.SharedTexture(sharedHandle: 23, width: 1, height: 1, SurfaceFormat.R8G8B8A8Unorm);

        Assert.Equal(SurfaceKind.CpuPixels, cpu.Kind);
        Assert.True(condition: cpu.IsCpuPixels);
        Assert.Equal(0, cpu.ImageViewHandle);
        Assert.Equal(0, cpu.SharedHandle);
        Assert.True(condition: image.IsSameDeviceImage);
        Assert.Equal(16, image.ImageHandle);
        Assert.True(condition: shared.IsSharedHandle);
        Assert.True(condition: default(Surface).IsEmpty);
    }
    [Fact]
    public void CpuSurfaceRejectsMismatchedPackedStorage() {
        _ = Assert.Throws<ArgumentException>(testCode: () => Surface.CpuPixels(format: SurfaceFormat.R8G8B8A8Unorm, height: 1, pixels: new byte[3], width: 1));
        _ = Assert.Throws<ArgumentException>(testCode: () => Surface.CpuPixels(format: SurfaceFormat.R8G8B8A8Unorm, height: 1, pixels: new byte[5], width: 1));
    }
    [Fact]
    public void DeviceLocalFactoryMethodsDoNotReturnHostWritableType() {
        var factoryType = typeof(IGpuStorageBufferFactory);

        Assert.Equal(typeof(IGpuBuffer), factoryType.GetMethod(name: nameof(IGpuStorageBufferFactory.CreateDeviceLocal))!.ReturnType);
        Assert.Equal(typeof(IGpuBuffer), factoryType.GetMethod(name: nameof(IGpuStorageBufferFactory.CreateDeviceLocalIndirectArgs))!.ReturnType);
    }
    [Fact]
    public void PushConstantsRequireDefinedStagesAndWordAlignment() {
        _ = new GpuPushConstantBinding(data: new byte[4], offset: 0, stageFlags: GpuShaderStage.Compute);

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new GpuPushConstantBinding(data: new byte[4], offset: 2, stageFlags: GpuShaderStage.Compute));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new GpuPushConstantBinding(data: new byte[3], offset: 0, stageFlags: GpuShaderStage.Compute));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new GpuPushConstantBinding(data: new byte[4], offset: 0, stageFlags: ((GpuShaderStage)0x8000_0000u)));
    }
    [Fact]
    public void ShaderValidationWalksContainerStructure() {
        var spirV = new byte[24];

        BinaryPrimitives.WriteUInt32LittleEndian(destination: spirV, value: 0x07230203);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: spirV.AsSpan(start: 4), value: 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: spirV.AsSpan(start: 12), value: 1);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: spirV.AsSpan(start: 20), value: 0x00010000);
        ShaderBytecode.ValidateFormat(bytecode: spirV);

        var truncatedInstruction = spirV.ToArray();

        BinaryPrimitives.WriteUInt32LittleEndian(destination: truncatedInstruction.AsSpan(start: 20), value: 0x00020000);
        _ = Assert.Throws<ArgumentException>(testCode: () => ShaderBytecode.ValidateFormat(bytecode: truncatedInstruction));

        var dxbc = new byte[44];

        BinaryPrimitives.WriteUInt32LittleEndian(destination: dxbc, value: 0x43425844);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: dxbc.AsSpan(start: 24), value: ((uint)dxbc.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(destination: dxbc.AsSpan(start: 28), value: 1);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: dxbc.AsSpan(start: 32), value: 36);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: dxbc.AsSpan(start: 36), value: 0x4c495844);
        ShaderBytecode.ValidateFormat(bytecode: dxbc);

        BinaryPrimitives.WriteUInt32LittleEndian(destination: dxbc.AsSpan(start: 40), value: 1);
        _ = Assert.Throws<ArgumentException>(testCode: () => ShaderBytecode.ValidateFormat(bytecode: dxbc));
    }
    [Fact]
    public void DescriptorSizingRejectsInvalidSetsAndOverflow() {
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuDescriptorPoolSizes.ForSets([default(GpuComputeBinding)]));
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuDescriptorPoolSizes.ForSets([
            new GpuComputeBinding(0, GpuComputeBindingKind.StorageImage),
            new GpuComputeBinding(0, GpuComputeBindingKind.StorageBufferRead)
        ]));
        _ = Assert.Throws<OverflowException>(testCode: () => GpuDescriptorPoolSizes.ForSets([
            new GpuComputeBinding(Binding: 0, Count: uint.MaxValue, Kind: GpuComputeBindingKind.StorageImage),
            new GpuComputeBinding(1, GpuComputeBindingKind.StorageImage)
        ]));
    }
    [Fact]
    public void NormalizedInputAndCameraRejectNonFiniteValues() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new MachinePadState(default, new Vector2(x: float.NaN, y: 0), default, 0, 0));
        var pad = new MachinePadState(MachineButtons.South, new Vector2(x: 0.5f, y: -0.5f), default, 0.25f, 0.75f);
        var neutral = MachinePadState.Neutral;

        Assert.Equal(pad, MachinePadState.Merge(first: in pad, second: in neutral));

        _ = Assert.Throws<ArgumentException>(testCode: () => CameraSnapshot.LookAt(new Vector3(x: float.NaN, y: 0, z: 0), Vector3.Zero, 1f, 1, 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => CameraSnapshot.LookAt(Vector3.Zero, -Vector3.UnitZ, 1f, 1, 0));
    }
}
