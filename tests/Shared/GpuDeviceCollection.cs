[assembly: Xunit.CollectionBehavior(collectionFactoryType: typeof(Puck.Testing.GpuDeviceCollectionFactory))]

namespace Puck.Testing;

/// <summary>The test collections of every test assembly: one collection per class, as xUnit's default makes them, except
/// that every class carrying <c>[Trait("Category", "Gpu")]</c>, on itself or a base type, joins one collection,
/// <see cref="CollectionName"/>, that disables parallelization. xUnit runs that collection after every parallel collection
/// has finished, one class and one law at a time, so a device law never shares the GPU, the driver's shader compiler or
/// the processors with another law, and a law that turns the Direct3D 12 debug layer on, which removes every device the
/// process holds, runs with no other device alive. A plain run of a suite therefore needs no option to keep its device
/// laws apart, and the trait GPU001 holds every device-opening class to is the one thing that places a class here. A
/// class that names a collection of its own keeps it; such a collection must disable parallelization too.
/// <para>The type is linked into every test project as source (see <c>Directory.Build.targets</c>), with the
/// assembly-level <see cref="Xunit.CollectionBehaviorAttribute"/> that installs it.</para></summary>
/// <param name="testAssembly">The test assembly whose classes the factory places.</param>
internal sealed class GpuDeviceCollectionFactory(Xunit.v3.IXunitTestAssembly testAssembly) : Xunit.v3.CollectionPerClassTestCollectionFactory(testAssembly: testAssembly) {
    /// <summary>The display name of the collection every GPU class joins.</summary>
    public const string CollectionName = "gpu device laws";

    private readonly Lazy<Xunit.v3.XunitTestCollection> m_devices = new(valueFactory: () => new Xunit.v3.XunitTestCollection(
        collectionDefinition: null,
        disableParallelization: true,
        displayName: CollectionName,
        testAssembly: testAssembly,
        uniqueID: null
    ));

    /// <summary>Gets whether a test class carries the <c>Gpu</c> trait, on itself or a base type, as xUnit gives a class
    /// its traits.</summary>
    /// <param name="testClass">The test class.</param>
    /// <returns>Whether the class is a GPU class.</returns>
    public static bool IsGpu(Type testClass) {
        for (var current = testClass; (current is not null); current = current.BaseType) {
            foreach (var attribute in current.GetCustomAttributesData()) {
                if (
                    (attribute.AttributeType == typeof(Xunit.TraitAttribute)) &&
                    (attribute.ConstructorArguments.Count == 2) &&
                    ((attribute.ConstructorArguments[0].Value as string) == "Category") &&
                    ((attribute.ConstructorArguments[1].Value as string) == "Gpu")
                ) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <inheritdoc/>
    protected override Xunit.v3.IXunitTestCollection GetDefaultTestCollection(Type testClass) => (IsGpu(testClass: testClass)
        ? m_devices.Value
        : base.GetDefaultTestCollection(testClass: testClass));
}
