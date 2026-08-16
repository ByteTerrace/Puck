namespace Puck.Physics.Tests.Measurements;

/// <summary>The collection every measurement fact runs under: several facts append to one shared report file, so
/// serializing just this collection keeps its sections in a stable order without disabling parallelism for the
/// rest of the assembly.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MeasurementCollection {
    /// <summary>The collection name every measurement fact class carries as its <see cref="CollectionAttribute"/>.</summary>
    public const string Name = "Physics measurement report";
}
