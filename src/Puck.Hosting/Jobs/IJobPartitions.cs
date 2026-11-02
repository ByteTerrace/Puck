namespace Puck.Hosting.Jobs;

/// <summary>The work one fork of a <see cref="JobPool"/> runs: a fixed number of partitions, each a fixed range of
/// elements, whose results do not depend on which thread runs a partition or in what order.</summary>
public interface IJobPartitions {
    /// <summary>Runs one partition. Partitions of one fork run concurrently, so a partition writes only state it owns
    /// (its elements, or the positions an earlier serial pass assigned it) and reads only state no partition
    /// writes.</summary>
    /// <param name="partition">The partition's index, from zero to the fork's partition count less one.</param>
    void Run(int partition);
}
