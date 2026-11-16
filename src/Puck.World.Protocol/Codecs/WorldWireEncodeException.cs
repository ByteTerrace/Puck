namespace Puck.World.Protocol;

/// <summary>A value an encoder was asked to write has no wire value: a kind or shape outside the wire tables, which
/// is a host bug rather than data. The throwing leaf writers of <see cref="WorldWireCodec"/> raise it; each codec
/// that uses them either lets it surface as the <see cref="InvalidOperationException"/> it is or maps it to its own
/// failure.</summary>
public sealed class WorldWireEncodeException : InvalidOperationException {
    /// <summary>Initializes a new instance of the <see cref="WorldWireEncodeException"/> class.</summary>
    /// <param name="message">What has no wire value.</param>
    public WorldWireEncodeException(string message)
        : base(message: message) {
    }
}
