namespace Puck.Abstractions.Collections;

/// <summary>
/// A thread-safe set of reusable objects: a renter takes an idle one or a fresh one the factory builds, and gives it
/// back when done, so threads that each need one at once hold distinct objects and a steady load builds no more than
/// its most concurrent renters. An object's contents carry over between renters, so a renter resets what it reads.
/// </summary>
/// <typeparam name="T">The pooled type.</typeparam>
/// <param name="create">Builds an object when none is idle; called outside the pool's lock.</param>
public sealed class SharedPool<T>(Func<T> create) where T : class {
    private readonly Lock m_gate = new();
    private readonly Stack<T> m_idle = new();

    /// <summary>Takes an idle object, or builds one when none is idle.</summary>
    /// <returns>An object no other renter holds.</returns>
    public T Rent() {
        lock (m_gate) {
            if (m_idle.TryPop(result: out var idle)) { return idle; }
        }
        return create();
    }
    /// <summary>Gives an object back for a later renter.</summary>
    /// <param name="item">An object this pool handed out and the caller no longer uses.</param>
    public void Return(T item) {
        lock (m_gate) { m_idle.Push(item: item); }
    }
}
