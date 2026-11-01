namespace Puck.Machines.Post;

/// <summary>The machine-specific operations used by the fleet benchmark.</summary>
/// <typeparam name="TSnapshot">The machine's immutable snapshot type.</typeparam>
public interface IMachineBenchmarkSubject<TSnapshot> : IDisposable where TSnapshot : class {
    /// <summary>Compares all state carried by two snapshots.</summary>
    /// <param name="a">The first snapshot.</param>
    /// <param name="b">The second snapshot.</param>
    /// <returns>Whether the snapshots are identical.</returns>
    static abstract bool SnapshotsEqual(TSnapshot a, TSnapshot b);
    /// <summary>Reads a snapshot's byte length.</summary>
    /// <param name="snapshot">The captured state.</param>
    /// <returns>The state length in bytes.</returns>
    static abstract int SnapshotSize(TSnapshot snapshot);
    /// <summary>Captures the current machine state.</summary>
    /// <returns>The immutable snapshot.</returns>
    TSnapshot Snapshot();
    /// <summary>Restores a captured machine state.</summary>
    /// <param name="snapshot">The captured state.</param>
    void Restore(TSnapshot snapshot);
    /// <summary>Creates an independently owned fork at the current instant.</summary>
    /// <returns>The fork owner, whose disposal releases the fork.</returns>
    IDisposable Fork();
    /// <summary>Resolves input collaborators before a fleet measurement, outside lifecycle measurements.</summary>
    void PrepareInput();
    /// <summary>Applies one deterministic input and advances a frame.</summary>
    /// <param name="stream">The independent input stream index.</param>
    /// <param name="frame">The zero-based frame within the measured span.</param>
    void RunFrame(int stream, int frame);
    /// <summary>Advances frames with the current input.</summary>
    /// <param name="frames">The number of frames.</param>
    void RunFrames(int frames);
}
