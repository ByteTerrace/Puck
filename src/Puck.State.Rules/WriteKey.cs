namespace Puck.State.Rules;

/// <summary>Which case resolving a state-writing effect's key answered. The zero value is <see cref="Faulted"/>, so a
/// defaulted answer refuses the firing as a fault rather than writing.</summary>
public enum WriteKeyCase : byte {
    /// <summary>Computing the key faulted (overflow, a divide by zero, or a value with no number): the firing is
    /// faulted.</summary>
    Faulted = 0,
    /// <summary>The key resolved but could not be admitted (a runtime key the table refused to intern): the firing is
    /// refused.</summary>
    Refused,
    /// <summary>The key resolved; it is the invalid key when the address names no cell.</summary>
    Resolved,
}
/// <summary>What resolving the key a state-writing effect addresses answered: the key, or why the write may not go on,
/// as cases of one value rather than a reason to compare.</summary>
/// <remarks>A dynamic key family that must create a runtime address resolves it here; read resolution never changes the
/// key table.</remarks>
public readonly struct WriteKey {
    private readonly WriteKeyCase m_case;
    private readonly CellKey m_key;
    private readonly string? m_reason;

    private WriteKey(WriteKeyCase writeCase, CellKey key, string? reason) {
        m_case = writeCase;
        m_key = key;
        m_reason = reason;
    }

    /// <summary>Gets which case resolving the key answered.</summary>
    public WriteKeyCase Case => m_case;
    /// <summary>Gets why the write may not go on, or empty when <see cref="Case"/> is
    /// <see cref="WriteKeyCase.Resolved"/>.</summary>
    public string Reason => (m_reason ?? ((m_case == WriteKeyCase.Resolved)
        ? string.Empty
        : RuleReads.KeyFaultReason));

    /// <summary>Creates the answer for a key that resolved.</summary>
    /// <param name="key">The key, or the invalid default when the address names no cell.</param>
    /// <returns>The answer.</returns>
    public static WriteKey Resolved(CellKey key) => new(
        key: key,
        reason: null,
        writeCase: WriteKeyCase.Resolved
    );
    /// <summary>Creates the answer for a key whose computation faulted.</summary>
    /// <returns>The answer.</returns>
    public static WriteKey Faulted() => new(
        key: default,
        reason: RuleReads.KeyFaultReason,
        writeCase: WriteKeyCase.Faulted
    );
    /// <summary>Creates the answer for a key that resolved and could not be admitted.</summary>
    /// <param name="reason">Why the key was refused.</param>
    /// <returns>The answer.</returns>
    public static WriteKey Refused(string reason) => new(
        key: default,
        reason: reason,
        writeCase: WriteKeyCase.Refused
    );
    /// <summary>Gives the key only when it resolved, so no caller reads a key from an answer that has none.</summary>
    /// <param name="key">The key, or the invalid default.</param>
    /// <returns><see langword="true"/> when <see cref="Case"/> is <see cref="WriteKeyCase.Resolved"/>.</returns>
    public bool TryResolved(out CellKey key) {
        key = ((m_case == WriteKeyCase.Resolved)
            ? m_key
            : default);

        return (m_case == WriteKeyCase.Resolved);
    }
}
