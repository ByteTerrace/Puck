namespace Puck.World.Server;

// A FAULTED PRODUCER RUN IS THE BODY'S FAULT, NEVER THE HOST'S. A producer is an authored program the body runs; a run
// that throws (arithmetic its values cannot hold, an operation refusing its input) is refused whole: a producer program
// admits only operations that write the run's scratch (CompiledBodyMotionProgram's producer admission), so the body's
// pose, its producer state and its oscillators read as they did before the run, and the body holds still in its place.
// The next run tries again, so a fault that depends on what the body senses clears itself when the input does. The
// population narrates each transition, faulted and recovered, once on the body.producer channel, and body.where carries
// the fault while it holds. Only an exception that leaves the process itself unsound still propagates; every other one
// is a deterministic consequence of the document and its input, so a replay faults the same body on the same run.
public sealed partial class WorldBody {
    // The fault the body's most recent producer run met, or null when that run completed.
    private string? m_producerFault;

    /// <summary>Gets why this body's most recent producer run was refused (the producer, the exception's type and its
    /// message), or <see langword="null"/> when that run completed. A refused run leaves the body holding still.</summary>
    public string? ProducerFault => m_producerFault;

    private static bool IsProducerFault(Exception error) => (error is not (OutOfMemoryException or InsufficientExecutionStackException));
    private void NoteProducerFault(Exception error) {
        m_producerFault = $"producer '{Source.ProducerName}' threw {error.GetType().Name}: {error.Message}";
    }

    // The body.where suffix: the fault while one holds, nothing otherwise, so an ordinary read-back is unchanged.
    private string DescribeProducerFault() => ((m_producerFault is { } fault)
        ? $" producer=faulted({fault})"
        : string.Empty);
}
