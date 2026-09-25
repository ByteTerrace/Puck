using System.Reflection;
using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuCreationFaults"/> over <see cref="FakeGpuDevice"/>: an armed fault fires exactly once, at the nth
/// creation of its kind counted from the moment it was armed, names its kind and number, and never reaches the device;
/// every other creation and every non-creating member is forwarded untouched; and every member of every wrapped
/// interface is either a creation of one declared kind or a pass-through.
/// </summary>
public sealed class GpuCreationFaultsLawTests {
    private static readonly Type[] WrappedInterfaces = [
        typeof(IGpuBindings),
        typeof(IGpuBufferFactory),
        typeof(IGpuCommandPoolFactory),
        typeof(IGpuImageFactory),
        typeof(IGpuPipelineFactory),
        typeof(IGpuRenderPassFactory),
        typeof(IGpuShaderModuleFactory),
    ];

    // Every creating member of the wrapped interfaces, keyed Interface.Member, with the kind it counts as and a call
    // through the wrapped services. The fake reads no description or module.
    private static Dictionary<string, (GpuCreationKind Kind, Action<GpuDeviceServices> Create)> Creations() => new(comparer: StringComparer.Ordinal) {
        ["IGpuBindings.CreatePool"] = (GpuCreationKind.BindingsPool, static services => _ = services.Bindings.CreatePool(sizes: default)),
        ["IGpuBufferFactory.CreateDeviceLocal"] = (GpuCreationKind.Buffer, static services => _ = services.BufferFactory.CreateDeviceLocal(sizeBytes: 16, usage: GpuBufferUsage.Storage)),
        ["IGpuBufferFactory.CreateHostVisible"] = (GpuCreationKind.Buffer, static services => _ = services.BufferFactory.CreateHostVisible(sizeBytes: 16, usage: GpuBufferUsage.Storage)),
        ["IGpuBufferFactory.CreateHostVisible(data)"] = (GpuCreationKind.Buffer, static services => _ = services.BufferFactory.CreateHostVisible(data: new byte[16], usage: GpuBufferUsage.Vertex)),
        ["IGpuCommandPoolFactory.Create"] = (GpuCreationKind.CommandPool, static services => _ = services.CommandPoolFactory.Create()),
        ["IGpuImageFactory.Create"] = (GpuCreationKind.Image, static services => _ = services.ImageFactory.Create(format: GpuPixelFormat.R8G8B8A8Unorm, height: 4, usage: GpuImageUsage.Storage, width: 4)),
        ["IGpuPipelineFactory.Create(compute)"] = (GpuCreationKind.Pipeline, static services => _ = services.PipelineFactory.Create(computeShaderModule: null!, description: null!)),
        ["IGpuPipelineFactory.Create(graphics)"] = (GpuCreationKind.Pipeline, static services => _ = services.PipelineFactory.Create(description: null!, fragmentShaderModule: null!, renderPass: null!, vertexShaderModule: null!)),
        ["IGpuRenderPassFactory.Create"] = (GpuCreationKind.RenderPass, static services => _ = services.RenderPassFactory.Create(description: null!)),
        ["IGpuRenderPassFactory.CreateFramebuffer"] = (GpuCreationKind.Framebuffer, static services => _ = services.RenderPassFactory.CreateFramebuffer(colors: [], depth: null, renderPass: null!)),
        ["IGpuShaderModuleFactory.Create"] = (GpuCreationKind.ShaderModule, static services => _ = services.ShaderModuleFactory.Create(bytecode: new byte[4], stage: GpuShaderStage.Compute)),
    };

    // The members of the wrapped interfaces that create nothing counted, and are forwarded without asking the faults.
    private static readonly string[] PassThroughs = [
        "IGpuBindings.AllocateSet",
        "IGpuBindings.CreateSampler",
        "IGpuBindings.DestroyPool",
        "IGpuBindings.DestroySampler",
        "IGpuBindings.WriteBuffer",
        "IGpuBindings.WriteCombinedImageSampler",
        "IGpuBindings.WriteStorageImage",
    ];

    public static TheoryData<string, int> ArmedCreations() {
        var data = new TheoryData<string, int>();

        foreach (var key in Creations().Keys) {
            for (var nth = 1; (nth <= 3); nth++) {
                data.Add(
                    p1: key,
                    p2: nth
                );
            }
        }

        return data;
    }

    // Calls one creation, and returns whether it reached the fake, which counts every call.
    private static bool Reaches(FakeGpuDevice gpu, GpuDeviceServices services, Action<GpuDeviceServices> create) {
        var before = gpu.Calls.Values.Sum();

        create(obj: services);

        return (gpu.Calls.Values.Sum() > before);
    }

    [MemberData(memberName: nameof(ArmedCreations))]
    [Theory]
    public void AnArmedFaultFiresExactlyOnceAtTheNthCreationOfItsKindAndNamesIt(string key, int nth) {
        var (kind, create) = Creations()[key];
        var gpu = new FakeGpuDevice(countCalls: true);
        var faults = new GpuCreationFaults();
        var services = GpuCreationFaults.Wrap(
            faults: faults,
            services: gpu.Services
        );

        // Creations before arming do not count toward the armed one.
        Assert.True(condition: Reaches(create: create, gpu: gpu, services: services));
        faults.Arm(
            kind: kind,
            nth: nth
        );
        Assert.True(condition: faults.TryGetArmed(
            kind: kind,
            remaining: out var remaining
        ));
        Assert.Equal(
            actual: remaining,
            expected: nth
        );

        for (var creation = 1; (creation < nth); creation++) {
            Assert.True(condition: Reaches(create: create, gpu: gpu, services: services));
        }

        // Every other kind's creations pass while this one is armed.
        foreach (var (otherKey, (otherKind, otherCreate)) in Creations()) {
            if (otherKind != kind) {
                Assert.True(
                    condition: Reaches(create: otherCreate, gpu: gpu, services: services),
                    userMessage: $"{otherKey} was stopped by a fault armed for {GpuCreationFaults.NameOf(kind: kind)}."
                );
            }
        }

        var calls = gpu.Calls.Values.Sum();
        var fault = Assert.Throws<GpuCreationFaultException>(testCode: () => create(obj: services));

        Assert.Equal(
            actual: gpu.Calls.Values.Sum(),
            expected: calls
        );
        Assert.Equal(
            actual: fault.Kind,
            expected: kind
        );
        Assert.Equal(
            actual: fault.Creation,
            expected: (nth + 1L)
        );
        Assert.StartsWith(
            actualString: fault.Message,
            expectedStartString: $"[{GpuCreationFaults.RefusalCode}] The {GpuCreationFaults.NameOf(kind: kind)} creation {(nth + 1)} "
        );
        Assert.False(condition: faults.TryGetArmed(
            kind: kind,
            remaining: out _
        ));

        // It fired once: the next creation of the kind reaches the device.
        Assert.True(condition: Reaches(create: create, gpu: gpu, services: services));
        Assert.Equal(
            actual: faults.SeenOf(kind: kind),
            expected: (nth + 2L)
        );
    }
    [Fact]
    public void ArmingAgainReplacesTheKindsFaultCountedFromNow() {
        var gpu = new FakeGpuDevice(countCalls: true);
        var faults = new GpuCreationFaults();
        var services = GpuCreationFaults.Wrap(
            faults: faults,
            services: gpu.Services
        );
        var create = Creations()["IGpuImageFactory.Create"].Create;

        faults.Arm(
            kind: GpuCreationKind.Image,
            nth: 3
        );
        Assert.True(condition: Reaches(create: create, gpu: gpu, services: services));
        faults.Arm(kind: GpuCreationKind.Image);
        Assert.True(condition: faults.TryGetArmed(
            kind: GpuCreationKind.Image,
            remaining: out var remaining
        ));
        Assert.Equal(
            actual: remaining,
            expected: 1L
        );
        Assert.Equal(
            actual: Assert.Throws<GpuCreationFaultException>(testCode: () => create(obj: services)).Creation,
            expected: 2L
        );

        // The replaced third creation is not armed any more.
        Assert.True(condition: Reaches(create: create, gpu: gpu, services: services));
        Assert.True(condition: Reaches(create: create, gpu: gpu, services: services));
    }
    [Fact]
    public void DisarmingClearsEveryFaultAndEveryCount() {
        var gpu = new FakeGpuDevice(countCalls: true);
        var faults = new GpuCreationFaults();
        var services = GpuCreationFaults.Wrap(
            faults: faults,
            services: gpu.Services
        );

        // Armed past every kind's creations below (a buffer has three creating members), so each kind is counted and
        // still armed when the faults are disarmed.
        foreach (var kind in GpuCreationFaults.Kinds) {
            faults.Arm(
                kind: kind,
                nth: 4
            );
        }

        foreach (var (key, (_, create)) in Creations()) {
            Assert.True(
                condition: Reaches(create: create, gpu: gpu, services: services),
                userMessage: $"{key} faulted before its fourth creation."
            );
        }

        faults.Disarm();

        foreach (var kind in GpuCreationFaults.Kinds) {
            Assert.False(condition: faults.TryGetArmed(
                kind: kind,
                remaining: out _
            ));
            Assert.Equal(
                actual: faults.SeenOf(kind: kind),
                expected: 0L
            );
        }

        foreach (var (key, (_, create)) in Creations()) {
            Assert.True(
                condition: Reaches(create: create, gpu: gpu, services: services),
                userMessage: $"{key} still faulted after disarming."
            );
        }
    }
    [Fact]
    public void ArmingRefusesAnUndeclaredKindAndANonPositiveCount() {
        var faults = new GpuCreationFaults();

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => faults.Arm(kind: GpuCreationKind.Image, nth: 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => faults.Arm(kind: ((GpuCreationKind)GpuCreationFaults.Kinds.Length), nth: 1));
    }
    [Fact]
    public void EveryKindHasOneSpellingThatReadsBack() {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var kind in GpuCreationFaults.Kinds) {
            var name = GpuCreationFaults.NameOf(kind: kind);

            Assert.True(condition: names.Add(item: name));
            Assert.True(condition: GpuCreationFaults.TryParseKind(
                kind: out var parsed,
                name: name
            ));
            Assert.Equal(
                actual: parsed,
                expected: kind
            );
        }

        Assert.Equal(
            actual: GpuCreationFaults.Kinds.Length,
            expected: Enum.GetValues<GpuCreationKind>().Length
        );
        Assert.False(condition: GpuCreationFaults.TryParseKind(
            kind: out _,
            name: "sampler"
        ));
        Assert.False(condition: GpuCreationFaults.TryParseKind(
            kind: out _,
            name: "Image"
        ));
    }
    [Fact]
    public void WithoutFaultsTheServicesAreReturnedUnchanged() {
        var gpu = new FakeGpuDevice(countCalls: true);

        Assert.Same(
            actual: GpuCreationFaults.Wrap(
                faults: null,
                services: gpu.Services
            ),
            expected: gpu.Services
        );
    }
    [Fact]
    public void TheRecorderSubmitterAndSurfaceTransfersPassThroughUnwrapped() {
        var gpu = new FakeGpuDevice(countCalls: true);
        var services = GpuCreationFaults.Wrap(
            faults: new GpuCreationFaults(),
            services: gpu.Services
        );

        Assert.Same(actual: services.Recorder, expected: gpu.Services.Recorder);
        Assert.Same(actual: services.QueueSubmitter, expected: gpu.Services.QueueSubmitter);
        Assert.Same(actual: services.SurfaceTransferFactory, expected: gpu.Services.SurfaceTransferFactory);
    }
    [Fact]
    public void WrappingAFaultingSetAgainIsRefused() {
        var faults = new GpuCreationFaults();
        var services = GpuCreationFaults.Wrap(
            faults: faults,
            services: new FakeGpuDevice(countCalls: true).Services
        );

        _ = Assert.Throws<ArgumentException>(testCode: () => GpuCreationFaults.Wrap(faults: faults, services: services));
    }
    [Fact]
    public void EveryPassThroughMemberForwardsWithoutCounting() {
        var gpu = new FakeGpuDevice(countCalls: true);
        var faults = new GpuCreationFaults();
        var bindings = GpuCreationFaults.Wrap(
            faults: faults,
            services: gpu.Services
        ).Bindings;

        foreach (var kind in GpuCreationFaults.Kinds) {
            faults.Arm(kind: kind);
        }

        _ = bindings.AllocateSet(descriptorSetLayoutHandle: 1, poolHandle: 2);
        _ = bindings.CreateSampler();
        bindings.DestroyPool(poolHandle: 2);
        bindings.DestroySampler(samplerHandle: 3);
        bindings.WriteBuffer(binding: 0, bufferHandle: 1, bufferSize: 4, descriptorSetHandle: 1, elementStride: 0, kind: GpuBindingKind.ReadOnlyBuffer);
        bindings.WriteCombinedImageSampler(arrayElement: 0, binding: 0, descriptorSetHandle: 1, imageViewHandle: 1, samplerHandle: 1);
        bindings.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: 1, imageViewHandle: 1);

        foreach (var key in PassThroughs) {
            Assert.Equal(
                actual: gpu.Count(key: key),
                expected: 1
            );
        }

        foreach (var kind in GpuCreationFaults.Kinds) {
            Assert.Equal(
                actual: faults.SeenOf(kind: kind),
                expected: 0L
            );
        }
    }
    [Fact]
    public void EveryMemberOfEveryWrappedInterfaceIsACreationOrAPassThrough() {
        var covered = Creations().Keys.Concat(second: PassThroughs).Select(selector: static key => key.Split(separator: '(')[0]).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var type in WrappedInterfaces) {
            foreach (var method in type.GetMethods(bindingAttr: BindingFlags.Public | BindingFlags.Instance)) {
                Assert.Contains(
                    collection: covered,
                    expected: $"{type.Name}.{method.Name}"
                );
            }
        }
    }
}
