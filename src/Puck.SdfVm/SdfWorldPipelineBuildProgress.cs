using System.Globalization;

namespace Puck.SdfVm;

/// <summary>
/// How far one <see cref="SdfWorldPipelines.Build"/> has come: the pipelines it creates and how many of them the driver
/// has returned. The build writes it from its creators on the thread pool; any thread reads it, so a holder waiting on
/// the set can say what it waits for.
/// </summary>
public sealed class SdfWorldPipelineBuildProgress {
    private int m_created;
    private int m_total = -1;

    /// <summary>Gets the number of pipelines the build has created so far.</summary>
    public int Created => Volatile.Read(location: ref m_created);
    /// <summary>Gets the number of pipelines the build creates, or -1 before it has started.</summary>
    public int Total => Volatile.Read(location: ref m_total);

    /// <summary>Describes the build as a clause naming its progress: <c>building (3 of 14 pipelines created)</c>, or
    /// <c>queued</c> before it has started.</summary>
    /// <returns>The clause.</returns>
    public string Describe() =>
        ((Total is var total and >= 0)
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"building ({Created} of {total} pipelines created)"
            )
            : "queued"
        );

    internal void Advance() =>
        _ = Interlocked.Increment(location: ref m_created);
    internal void Begin(int total) {
        Volatile.Write(
            location: ref m_created,
            value: 0
        );
        Volatile.Write(
            location: ref m_total,
            value: total
        );
    }
}
