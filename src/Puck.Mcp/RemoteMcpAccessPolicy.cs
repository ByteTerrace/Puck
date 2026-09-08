using System.Collections.Frozen;

namespace Puck.Mcp;

/// <summary>Live explicit Operator grants. A trusted configuration owner may replace grants; removals revoke active attachments.</summary>
public sealed class RemoteMcpAccessPolicy {
    private readonly Lock m_gate = new();
    private FrozenSet<string> m_subjects = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    internal event Action? Changed;
    /// <summary>Checks the current grant, using the configured issuer's subject namespace.</summary>
    /// <param name="subject">A validated subject.</param>
    /// <returns>Whether it currently has Operator authority.</returns>
    public bool Allows(string subject) => Volatile.Read(ref m_subjects).Contains(subject);
    /// <summary>Atomically replaces grants. An empty list revokes every subject.</summary>
    /// <param name="subjects">Up to 64 nonempty subject identifiers.</param>
    public void Replace(IReadOnlyCollection<string> subjects) {
        ArgumentNullException.ThrowIfNull(subjects);
        if (subjects.Count > 64 || subjects.Any(string.IsNullOrWhiteSpace)) { throw new ArgumentException("At most 64 nonempty subjects are permitted.", nameof(subjects)); }
        var replacement = subjects.ToFrozenSet(StringComparer.Ordinal);
        lock (m_gate) {
            if (m_subjects.SetEquals(replacement)) { return; }
            Volatile.Write(ref m_subjects, replacement);
            Changed?.Invoke();
        }
    }
}
