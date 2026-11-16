namespace Puck.State;

/// <summary>An answer computed from one section: a static function of the section's value and nothing else.</summary>
/// <typeparam name="TSection">The section type.</typeparam>
/// <typeparam name="TResult">The answer's type.</typeparam>
public interface IDerivation<TSection, out TResult> {
    /// <summary>Computes the answer.</summary>
    /// <param name="section">The section.</param>
    /// <returns>The answer.</returns>
    static abstract TResult Derive(in TSection section);
}
/// <summary>An answer computed from two sections: a static function of their values and nothing else.</summary>
/// <typeparam name="TFirst">The first section type.</typeparam>
/// <typeparam name="TSecond">The second section type.</typeparam>
/// <typeparam name="TResult">The answer's type.</typeparam>
public interface IDerivation<TFirst, TSecond, out TResult> {
    /// <summary>Computes the answer.</summary>
    /// <param name="first">The first section.</param>
    /// <param name="second">The second section.</param>
    /// <returns>The answer.</returns>
    static abstract TResult Derive(in TFirst first, in TSecond second);
}
/// <summary>
/// An answer kept beside the holders it was computed from. A read takes the holders themselves and the type whose
/// static member computes the answer from their read sides; it recomputes when a holder is another one than last
/// time or its version moved, and otherwise returns what it kept. The function is static and receives only what the
/// holders hold, so the versions compared are the versions of exactly the values read: there is no second list of
/// inputs to keep in step.
/// </summary>
/// <typeparam name="TResult">The answer's type.</typeparam>
/// <remarks>The guarantee stops at a derivation that reads mutable static state, which nothing here refuses.</remarks>
public sealed class Derived<TResult> {
    private VersionedHolder? m_first;
    private VersionedHolder? m_second;
    private ulong m_firstVersion;
    private ulong m_secondVersion;
    private TResult m_value = default!;

    /// <summary>Gets the number of times a read computed the answer afresh.</summary>
    public long Computes { get; private set; }

    /// <summary>Reads the answer derived from one holder's section.</summary>
    /// <typeparam name="TSection">The section type.</typeparam>
    /// <typeparam name="TDerivation">The type whose static member computes the answer.</typeparam>
    /// <param name="holder">The holder.</param>
    /// <returns>The answer for the holder's current value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="holder"/> is <see langword="null"/>.</exception>
    public TResult Read<TSection, TDerivation>(Versioned<TSection> holder)
        where TSection : unmanaged, IVersionedSection
        where TDerivation : IDerivation<TSection, TResult> {
        ArgumentNullException.ThrowIfNull(argument: holder);

        if (!ReferenceEquals(objA: m_first, objB: holder) || (m_firstVersion != holder.Version) || (m_second is not null)) {
            m_value = TDerivation.Derive(section: in holder.Read);
            m_first = holder;
            m_firstVersion = holder.Version;
            m_second = null;
            Computes++;
        }
        return m_value;
    }
    /// <summary>Reads the answer derived from two holders' sections.</summary>
    /// <typeparam name="TFirst">The first section type.</typeparam>
    /// <typeparam name="TSecond">The second section type.</typeparam>
    /// <typeparam name="TDerivation">The type whose static member computes the answer.</typeparam>
    /// <param name="first">The first holder.</param>
    /// <param name="second">The second holder.</param>
    /// <returns>The answer for the holders' current values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="first"/> or <paramref name="second"/> is <see langword="null"/>.</exception>
    public TResult Read<TFirst, TSecond, TDerivation>(Versioned<TFirst> first, Versioned<TSecond> second)
        where TFirst : unmanaged, IVersionedSection
        where TSecond : unmanaged, IVersionedSection
        where TDerivation : IDerivation<TFirst, TSecond, TResult> {
        ArgumentNullException.ThrowIfNull(argument: first);
        ArgumentNullException.ThrowIfNull(argument: second);

        if (
            !ReferenceEquals(objA: m_first, objB: first) || (m_firstVersion != first.Version) ||
            !ReferenceEquals(objA: m_second, objB: second) || (m_secondVersion != second.Version)
        ) {
            m_value = TDerivation.Derive(first: in first.Read, second: in second.Read);
            m_first = first;
            m_firstVersion = first.Version;
            m_second = second;
            m_secondVersion = second.Version;
            Computes++;
        }
        return m_value;
    }
}
