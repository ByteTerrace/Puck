namespace Puck.Abstractions.Jobs;

/// <summary>A fork's partitions run through a delegate: for a body whose state a type of its own would only restate,
/// such as a lambda over the caller's locals.</summary>
/// <param name="body">The work of one partition, given its index.</param>
public sealed class DelegatedPartitions(Action<int> body) : IJobPartitions {
    /// <inheritdoc/>
    public void Run(int partition) => body(obj: partition);
}
