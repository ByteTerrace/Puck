using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldFederationCodec {
    /// <summary>Encodes the destination's authority namespace in the authentication acknowledgement. This names
    /// the endpoint serving the connection; it is not a signature or a substitute for transport integrity.</summary>
    /// <param name="authority">The destination's nonempty authority namespace.</param>
    /// <returns>The encoded acknowledgement body.</returns>
    /// <exception cref="ArgumentException">The namespace is null or empty.</exception>
    public static byte[] EncodeAuthorityIdentity(string authority) {
        ArgumentException.ThrowIfNullOrEmpty(authority);
        var writer = new WireWriter();
        writer.WriteString(authority);
        return writer.ToArray();
    }

    /// <summary>Decodes exactly one nonempty destination namespace.</summary>
    /// <param name="body">The untrusted acknowledgement bytes.</param>
    /// <param name="authority">The decoded namespace on success.</param>
    /// <param name="failure">The named decoding refusal.</param>
    /// <returns>True only when the complete body is valid.</returns>
    public static bool TryDecodeAuthorityIdentity(ReadOnlySpan<byte> body, out string authority, out WireFailure failure) {
        var reader = new WireReader(body);
        authority = reader.ReadRequiredString("destination authority");
        return reader.TryFinish(out failure);
    }
}
