namespace Puck.Abstractions.Machines;

/// <summary>Optional durable state capability for a machine runtime. Capture includes all accepted work before
/// its barrier. Restore requires an independently created runtime with the same content and configuration,
/// before it is admitted to simulation; callers must discard that runtime if restore fails.</summary>
public interface IMachineCheckpointRuntime {
    /// <summary>Captures owned bytes containing authoritative machine and host pacing state. Unrepresentable
    /// state, a fault, or a closed runtime throws instead of producing a partial checkpoint.</summary>
    /// <returns>An independently owned complete checkpoint image.</returns>
    byte[] CaptureCheckpoint();
    /// <summary>Restores a captured state after checking its content, configuration, and encoding identity.</summary>
    /// <param name="checkpoint">Owned checkpoint bytes; the runtime does not retain the caller's buffer.</param>
    void RestoreCheckpoint(ReadOnlyMemory<byte> checkpoint);
}
