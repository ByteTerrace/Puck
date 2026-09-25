using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuObjectNaming"/> and <see cref="GpuObjectName"/>: with naming on, every creating member hands
/// its object's name to the device at creation, through <see cref="GpuCreationFaults"/> and <see cref="GpuWorkCounting"/>
/// alike; with naming off, the device is never called and a named creation formats and allocates nothing; and a name's
/// text is a pure function of its parts.
/// </summary>
public sealed class GpuObjectNamingLawTests {
    private const string Owner = "law";

    // Every creating member that takes a name, keyed by member, with the kind the fake names its object as.
    private static Dictionary<string, (GpuObjectKind Kind, Action<GpuDeviceServices, GpuObjectName> Create)> Creations() => new(comparer: StringComparer.Ordinal) {
        ["IGpuBindings.AllocateSet"] = (GpuObjectKind.DescriptorSet, static (services, name) => _ = services.Bindings.AllocateSet(descriptorSetLayoutHandle: 1, name: name, poolHandle: 1)),
        ["IGpuBindings.CreatePool"] = (GpuObjectKind.DescriptorPool, static (services, name) => _ = services.Bindings.CreatePool(name: name, sizes: default)),
        ["IGpuBufferFactory.CreateDeviceLocal"] = (GpuObjectKind.Buffer, static (services, name) => _ = services.BufferFactory.CreateDeviceLocal(name: name, sizeBytes: 16, usage: GpuBufferUsage.Storage)),
        ["IGpuBufferFactory.CreateHostVisible"] = (GpuObjectKind.Buffer, static (services, name) => _ = services.BufferFactory.CreateHostVisible(name: name, sizeBytes: 16, usage: GpuBufferUsage.Storage)),
        ["IGpuBufferFactory.CreateHostVisible(data)"] = (GpuObjectKind.Buffer, static (services, name) => _ = services.BufferFactory.CreateHostVisible(data: new byte[16], name: name, usage: GpuBufferUsage.Vertex)),
        ["IGpuCommandPoolFactory.Create"] = (GpuObjectKind.CommandPool, static (services, name) => _ = services.CommandPoolFactory.Create(name: name)),
        ["IGpuImageFactory.Create"] = (GpuObjectKind.Image, static (services, name) => _ = services.ImageFactory.Create(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, name: name, usage: GpuImageUsage.Storage, width: 4)),
        ["IGpuPipelineFactory.Create(compute)"] = (GpuObjectKind.Pipeline, static (services, name) => _ = services.PipelineFactory.Create(computeShaderModule: null!, description: null!, name: name)),
        ["IGpuPipelineFactory.Create(graphics)"] = (GpuObjectKind.Pipeline, static (services, name) => _ = services.PipelineFactory.Create(description: null!, fragmentShaderModule: null!, name: name, renderPass: null!, vertexShaderModule: null!)),
        ["IGpuRenderPassFactory.Create"] = (GpuObjectKind.RenderPass, static (services, name) => _ = services.RenderPassFactory.Create(description: null!, name: name)),
    };

    public static TheoryData<string> CreationKeys() {
        var data = new TheoryData<string>();

        foreach (var key in Creations().Keys) {
            data.Add(row: key);
        }

        return data;
    }

    // The services a device hands out, wrapped as a node sees them: faults at creation, counting above.
    private static GpuDeviceServices Decorated(FakeGpuDevice gpu) =>
        GpuWorkCounting.Wrap(
            ledger: new GpuWorkLedger(
                framesInFlight: 2,
                name: "gpu.test"
            ),
            services: GpuCreationFaults.Wrap(
                faults: new GpuCreationFaults(),
                services: gpu.Services
            )
        );

    [MemberData(memberName: nameof(CreationKeys))]
    [Theory]
    public void NamesReachTheDeviceAtCreationThroughTheDecorators(string key) {
        var naming = new RecordingGpuObjectNaming(isEnabled: true);
        var services = Decorated(gpu: new FakeGpuDevice(naming: naming));

        var (kind, create) = Creations()[key];

        create(
            arg1: services,
            arg2: new GpuObjectName(
                index: 2,
                owner: Owner,
                part: key
            )
        );

        var applied = Assert.Single(collection: naming.Applied);

        Assert.Equal(
            actual: applied.Kind,
            expected: kind
        );
        Assert.Equal(
            actual: applied.Name,
            expected: $"{Owner}/{key}[2]"
        );
        Assert.Same(
            actual: services.Naming,
            expected: naming
        );
    }
    [MemberData(memberName: nameof(CreationKeys))]
    [Theory]
    public void NoNameReachesTheDeviceWhenNamingIsOff(string key) {
        var naming = new RecordingGpuObjectNaming(isEnabled: false);
        var services = Decorated(gpu: new FakeGpuDevice(naming: naming));

        Creations()[key].Create(
            arg1: services,
            arg2: new GpuObjectName(
                owner: Owner,
                part: key
            )
        );

        Assert.Empty(collection: naming.Applied);
    }
    [Fact]
    public void TheDefaultNameIsNeverApplied() {
        var naming = new RecordingGpuObjectNaming(isEnabled: true);
        var services = Decorated(gpu: new FakeGpuDevice(naming: naming));

        foreach (var (_, create) in Creations().Values) {
            create(
                arg1: services,
                arg2: default
            );
        }

        Assert.Empty(collection: naming.Applied);
    }
    [Fact]
    public void ANamedCreationAllocatesNothingWhenNamingIsOff() {
        var services = Decorated(gpu: new FakeGpuDevice(naming: new RecordingGpuObjectNaming(isEnabled: false)));
        var part = "descriptors";

        void Body() {
            _ = services.Bindings.AllocateSet(
                descriptorSetLayoutHandle: 1,
                name: new GpuObjectName(
                    detail: "upload",
                    index: 3,
                    owner: Owner,
                    part: part
                ),
                poolHandle: 1
            );
        }

        Assert.Equal(
            actual: AllocationWindow.Least(window: Body),
            expected: 0L
        );
    }
    [Fact]
    public void ANamedCreationFormatsItsNameWhenNamingIsOn() {
        var services = Decorated(gpu: new FakeGpuDevice(naming: new RecordingGpuObjectNaming(isEnabled: true)));
        var part = "descriptors";

        void Body() {
            _ = services.Bindings.AllocateSet(
                descriptorSetLayoutHandle: 1,
                name: new GpuObjectName(
                    detail: "upload",
                    index: 3,
                    owner: Owner,
                    part: part
                ),
                poolHandle: 1
            );
        }

        Assert.True(condition: (AllocationWindow.Measure(window: Body) > 0L));
    }
    [Fact]
    public void ANameFormatsFromItsPartsAlone() {
        Assert.Equal(
            actual: new GpuObjectName(owner: "sdf.world", part: "tiles").ToString(),
            expected: "sdf.world/tiles"
        );
        Assert.Equal(
            actual: new GpuObjectName(detail: "host", index: 1, owner: "sdf.world", part: "viewports").ToString(),
            expected: "sdf.world/viewports/host[1]"
        );
        Assert.Equal(
            actual: new GpuObjectName(owner: "main", part: "bloom").At(index: 0).ToString(),
            expected: "main/bloom[0]"
        );
        Assert.Equal(
            actual: new GpuObjectName(owner: "main", part: "draw", detail: "barriers").ToString(),
            expected: "main/draw/barriers"
        );
        Assert.Equal(
            actual: default(GpuObjectName).At(index: 4),
            expected: default
        );
        Assert.Equal(
            actual: default(GpuObjectName).ToString(),
            expected: string.Empty
        );
        _ = Assert.Throws<ArgumentException>(testCode: static () => new GpuObjectName(owner: "main", part: "draw", detail: string.Empty));
    }
    [Fact]
    public void TheOffNamingIsNeverEnabled() =>
        Assert.False(condition: GpuObjectNaming.Off.IsEnabled);
}
