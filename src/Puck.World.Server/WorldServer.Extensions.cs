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
    /// <summary>Explicitly opens a new live integration epoch after recovery or a fork. Old runtime instances stay
    /// revoked. Only the trusted composition root may call this after choosing bindings and journal namespaces
    /// appropriate to the restored world; replay never calls it automatically.</summary>
    /// <exception cref="InvalidOperationException">An external call from the previous epoch is still in flight.</exception>
    public void StartRecordedExtensionEpoch() => Extensions.StartEpoch();
    /// <summary>Revokes live external execution and contribution capabilities before a replay replaces this
    /// timeline. Suppression lasts until the host explicitly opens a new live integration epoch.</summary>
    public void SuppressRecordedExtensions() => Extensions.Suppress();
}
