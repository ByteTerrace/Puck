namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Gets the recorded-extension facade — the live integration epoch, its suppression latch, the
    /// external-operation dispatcher, and the contribution journal a mounted runtime submits through.</summary>
    public WorldExtensions Extensions { get; }

    /// <summary>Captures a versioned causal checkpoint for a trusted host to commit alongside an external operation.
    /// This is authority-private recovery data; never disclose it to a provider as an observation.</summary>
    /// <param name="hostRow">The host's cross-instance state for this authority.</param>
    /// <returns>A base64 checkpoint prefixed by its recovery format name.</returns>
    /// <exception cref="InvalidOperationException">The authority is replaying or cannot currently checkpoint.</exception>
    public string CaptureExternalOperationCause(WorldAuthorityHostRowCheckpoint hostRow) =>
        Extensions.CaptureExternalOperationCause(hostRow: hostRow);
    /// <summary>Explicitly opens a new live integration epoch after a host's own suppression (a recovery or an
    /// activation from a checkpoint). Old runtime instances stay revoked. The trusted composition root calls this
    /// after choosing bindings and journal namespaces appropriate to the restored world; a replay drive or a history
    /// seek reopens the epoch itself when it completes.</summary>
    /// <exception cref="InvalidOperationException">An external call from the previous epoch is still in flight, or a
    /// replay is active.</exception>
    public void StartRecordedExtensionEpoch() => Extensions.StartEpoch();
    /// <summary>Revokes live external execution and contribution capabilities before this timeline is replaced.
    /// Suppression lasts until a new live integration epoch opens: <see cref="StartRecordedExtensionEpoch"/> for a
    /// host's own suppression, the end of the drive or seek for a replay's.</summary>
    public void SuppressRecordedExtensions() => Extensions.Suppress();
}
