namespace Puck.Commands;

/// <summary>
/// The time by which a text session's in-flight line owes its caller an answer: a control attachment's call deadline,
/// less the time its reply needs to travel back. A hold the line arms keeps the deadline it was armed under, so a verb
/// can refuse a hold that cannot release in time, and a hold still standing when the deadline passes ends with an
/// answer instead of with the caller's transport.
/// </summary>
/// <param name="clock">The clock the caller's own deadline runs on.</param>
/// <param name="budget">The time from construction the answer may take.</param>
public sealed class TextAnswerDeadline(TimeProvider clock, TimeSpan budget) {
    private readonly long m_start = clock.GetTimestamp();

    /// <summary>Gets the time left before the answer is owed; zero or negative once it has passed.</summary>
    public TimeSpan Remaining => (budget - clock.GetElapsedTime(startingTimestamp: m_start));
    /// <summary>Gets whether the answer is owed now.</summary>
    public bool HasPassed => (Remaining <= TimeSpan.Zero);
}
