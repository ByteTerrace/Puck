namespace Puck.Abstractions.Machines;

/// <summary>Optional removable program or media content. A runtime need not have a content slot.</summary>
public interface IMachineContentSlot {
    /// <summary>Gets whether content is mounted.</summary>
    bool IsAssigned { get; }
    /// <summary>Replaces mounted content. The caller validates and prepares the image before invoking this operation.</summary>
    /// <param name="data">Prepared native content bytes.</param>
    /// <param name="savePath">Persistent save location, or null for in-memory persistence.</param>
    void LoadContent(byte[] data, string? savePath = null);
    /// <summary>Flushes and removes mounted content.</summary>
    void Eject();
    /// <summary>Persists pending save changes.</summary>
    /// <param name="force">Whether to flush even when only clock-style state changed.</param>
    void FlushSave(bool force = false);
}
