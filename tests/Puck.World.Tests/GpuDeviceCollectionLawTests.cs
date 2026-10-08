using Puck.Testing;
using Xunit;
using Xunit.v3;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the collection factory every test project links (<c>tests/Shared/GpuDeviceCollection.cs</c>)
/// places every class of this suite that carries <c>[Trait("Category", "Gpu")]</c> in a collection that disables
/// parallelization, every such class without a collection of its own in the one device-law collection, and no other
/// class there. A plain run of the suite therefore holds at most one device law at a time, and none beside a CPU law.
/// </summary>
public sealed class GpuDeviceCollectionLawTests {
    [Fact]
    public void EveryGpuClassRunsAloneAndNoOtherClassJoinsTheDeviceLawCollection() {
        var assembly = typeof(GpuDeviceCollectionLawTests).Assembly;
        var factory = new GpuDeviceCollectionFactory(testAssembly: new XunitTestAssembly(
            assembly: assembly,
            assemblyName: null,
            assemblyPath: null,
            configFilePath: null,
            targetFramework: null,
            uniqueID: null,
            version: null
        ));
        var classes = assembly.GetTypes().Where(predicate: static type => (type.IsPublic && !type.IsAbstract && type.GetMethods().Any(predicate: static method =>
            method.GetCustomAttributesData().Any(predicate: static attribute => typeof(FactAttribute).IsAssignableFrom(c: attribute.AttributeType))))).ToArray();
        var gpu = classes.Where(predicate: GpuDeviceCollectionFactory.IsGpu).ToArray();
        var placed = classes.ToDictionary(keySelector: static type => type, elementSelector: type => factory.Get(testClass: type));
        var devices = placed.Values.Where(predicate: static collection => (collection.TestCollectionDisplayName == GpuDeviceCollectionFactory.CollectionName)).Distinct().ToArray();

        Assert.NotEmpty(collection: gpu);
        Assert.Contains(collection: gpu, expected: typeof(SdfFieldDeviceLawTests));
        Assert.DoesNotContain(collection: gpu, expected: typeof(GpuDeviceCollectionLawTests));
        Assert.Single(collection: devices);
        Assert.All(collection: gpu, action: type => Assert.True(
            condition: (placed[type] is XunitTestCollection { DisableParallelization: true }),
            userMessage: $"{type.Name} carries the Gpu trait but runs in the parallel collection '{placed[type].TestCollectionDisplayName}'"
        ));
        Assert.All(collection: classes.Except(second: gpu), action: type => Assert.NotSame(expected: devices[0], actual: placed[type]));
    }
}
