using System.Security.Cryptography;
using System.Text;

namespace Puck.Commands;

/// <summary>Whether an input-device identity continues to name the same physical device after reconnect.</summary>
public enum InputDeviceIdentityPersistence : byte {
    /// <summary>The identity is valid only for routing the current connection and must not be persisted.</summary>
    Connection,

    /// <summary>The identity is derived from physical-device content and is stable across reconnects.</summary>
    Reconnect,
}
/// <summary>The identity of an input device. Prefer the content-addressed factories so a device's identity is
/// stable across reconnects; <see cref="New"/> and <see cref="FromConnectionKey"/> remain for ad-hoc or
/// transport-local identities that must not be persisted.</summary>
/// <param name="Value">The 128-bit device identity.</param>
/// <param name="Persistence">Whether the value continues to identify the same physical device after reconnect.</param>
public readonly record struct InputDeviceId(Guid Value, InputDeviceIdentityPersistence Persistence = InputDeviceIdentityPersistence.Connection) {
    /// <summary>Derives a deterministic connection-only id from a transport key such as an XInput user slot.</summary>
    /// <param name="key">The transport-local connection key.</param>
    /// <returns>An id that is deterministic for the key but explicitly ineligible for durable preferences.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public static InputDeviceId FromConnectionKey(string key) {
        ArgumentNullException.ThrowIfNull(key);

        var addressed = FromContent(content: Encoding.UTF8.GetBytes(s: key));

        return addressed with { Persistence = InputDeviceIdentityPersistence.Connection };
    }
    /// <summary>
    /// Derives a stable, content-addressed id from a device's identity bytes (for example its OS device path):
    /// the same content always yields the same id, so a controller that reconnects keeps its identity while
    /// distinct devices get distinct ids. Mirrors <c>AssetContentHash</c>'s SHA-256 content addressing, taking
    /// the leading 128 bits of the digest.
    /// </summary>
    /// <param name="content">The stable identity bytes to address.</param>
    /// <returns>The content-addressed device id.</returns>
    public static InputDeviceId FromContent(ReadOnlySpan<byte> content) {
        Span<byte> digest = stackalloc byte[32];

        _ = SHA256.HashData(
            destination: digest,
            source: content
        );

        return new InputDeviceId(
            Value: new Guid(b: digest[..16]),
            Persistence: InputDeviceIdentityPersistence.Reconnect
        );
    }
    /// <summary>Derives a content-addressed id from a textual device key (UTF-8 encoded), such as a device path.</summary>
    /// <param name="key">The stable identity string to address.</param>
    /// <returns>The content-addressed device id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public static InputDeviceId FromKey(string key) {
        ArgumentNullException.ThrowIfNull(key);

        // Hashed once per device connection, so the small allocation here is immaterial.
        return FromContent(content: Encoding.UTF8.GetBytes(s: key));
    }
    /// <summary>Creates a fresh, random device id with no stable meaning across connections.</summary>
    /// <returns>A new random device id.</returns>
    public static InputDeviceId New() {
        return new InputDeviceId(
            Value: Guid.NewGuid(),
            Persistence: InputDeviceIdentityPersistence.Connection
        );
    }
    public override string ToString() {
        return $"input/{Value:n}";
    }
}
