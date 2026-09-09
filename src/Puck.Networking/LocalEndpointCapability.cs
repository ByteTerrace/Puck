using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Networking;

/// <summary>An OS-user-owned discovery capability with mutual challenge authentication over a local stream.</summary>
/// <remarks>The secret stays private to the descriptor and proof computation. This establishes the OS user,
/// including that user's other processes and elevation levels; callers must bind and connect only to loopback.</remarks>
public sealed class LocalEndpointCapability {
    private const int Revision = 1;

    private readonly LocalEndpointDescriptor m_descriptor;

    /// <summary>Creates a fresh host incarnation and 256-bit capability secret for a loopback listener.</summary>
    /// <param name="port">The bound TCP port, from 1 through 65535.</param>
    public LocalEndpointCapability(int port) {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        m_descriptor = new(Revision, Guid.NewGuid().ToString(format: "N"), port, Convert.ToHexString(inArray: RandomNumberGenerator.GetBytes(count: 32)));
    }

    private LocalEndpointCapability(LocalEndpointDescriptor descriptor) { m_descriptor = descriptor; }

    /// <summary>The loopback TCP port advertised by this capability.</summary>
    public int Port => m_descriptor.Port;

    /// <summary>Creates a user-private descriptor; Windows additionally holds it open against modification or replacement.</summary>
    /// <param name="path">A new file path. Only this path, never the file contents, may be logged or passed in arguments.</param>
    /// <returns>The handle the host must retain until the listener closes; the host then deletes the file.</returns>
    /// <exception cref="PlatformNotSupportedException">The platform is neither Windows nor Linux x64.</exception>
    public FileStream WriteDescriptor(string path) {
        var descriptor = m_descriptor;
        var file = (OperatingSystem.IsWindows()
            ? new FileInfo(fileName: path).Create(FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write, FileShare.Read, 4096, FileOptions.None, LocalUserAccess.Create<FileSecurity>())
            : (OperatingSystem.IsLinux() ? LocalUserAccess.OpenLinuxFile(path, create: true)
            : throw new PlatformNotSupportedException(message: "Local control supports Windows and Linux x64.")));

        try {
            file.Write(buffer: JsonSerializer.SerializeToUtf8Bytes(descriptor, LocalEndpointJson.Default.LocalEndpointDescriptor));
            file.Flush(flushToDisk: true);
            return file;
        } catch { file.Dispose(); throw; }
    }
    /// <summary>Opens and validates a descriptor's owner, DACL, bounded contents and revision using the opened handle.</summary>
    /// <param name="path">The host's descriptor file path.</param>
    /// <returns>The capability used to connect to and authenticate the advertised local endpoint.</returns>
    /// <exception cref="PlatformNotSupportedException">The platform is neither Windows nor Linux x64.</exception>
    /// <exception cref="UnauthorizedAccessException">The descriptor belongs to or grants access to another identity.</exception>
    /// <exception cref="InvalidDataException">The descriptor is malformed or its revision is unsupported.</exception>
    public static LocalEndpointCapability ReadDescriptor(string path) {
        using var file = (OperatingSystem.IsWindows() ? new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: path, share: FileShare.ReadWrite)
            : (OperatingSystem.IsLinux() ? LocalUserAccess.OpenLinuxFile(path, create: false)
            : throw new PlatformNotSupportedException(message: "Local control supports Windows and Linux x64.")));
        // Inspect the opened handle, not a path that could be replaced between the check and read.
        if (OperatingSystem.IsWindows()) {
            using var identity = WindowsIdentity.GetCurrent();
            var security = file.GetAccessControl();

            if (!Equals(objA: security.GetOwner(targetType: typeof(SecurityIdentifier)), objB: identity.User)) { throw new UnauthorizedAccessException(message: "Attachment file belongs to another user."); }
            foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))) {
                if ((rule.AccessControlType == AccessControlType.Allow) && !Equals(objA: rule.IdentityReference, objB: identity.User)) {
                    throw new UnauthorizedAccessException(message: "Attachment file grants access to another identity.");
                }
            }
        }
        if (file.Length is <= 0 or > 4096) { throw new InvalidDataException(message: "Invalid attachment file size."); }
        var data = new byte[((int)file.Length)];

        file.ReadExactly(buffer: data);
        var descriptor = (JsonSerializer.Deserialize(data, LocalEndpointJson.Default.LocalEndpointDescriptor) ?? throw new InvalidDataException(message: "Missing attachment descriptor."));

        if ((descriptor.Revision != Revision) || (descriptor.Port is <= 0 or > 65535) || !Guid.TryParseExact(descriptor.Host, "N", out _) || !IsHex(descriptor.Secret, 64)) {
            throw new InvalidDataException(message: "Invalid attachment descriptor or unsupported revision.");
        }
        return new(descriptor);
    }
    /// <summary>Proves both peers hold this capability using role-separated HMAC challenges and a five-second deadline.</summary>
    /// <param name="stream">The connected local stream; callers close it on any failure.</param>
    /// <param name="server">True for the listener; false for the attaching client.</param>
    /// <param name="token">Cancels the handshake, invalidating the connection.</param>
    /// <returns>Completion after the peer proves possession of the secret.</returns>
    /// <exception cref="UnauthorizedAccessException">The peer fails identity or proof validation.</exception>
    public async Task AuthenticateAsync(Stream stream, bool server, CancellationToken token) {
        var descriptor = m_descriptor;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: token);

        deadline.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 5));
        token = deadline.Token;
        var nonce = Convert.ToHexString(inArray: RandomNumberGenerator.GetBytes(count: 32));

        if (server) {
            await SendAsync(hello: new(Revision, descriptor.Host, nonce, ""), stream: stream, token: token).ConfigureAwait(continueOnCapturedContext: false);
            var client = await ReceiveAsync(stream: stream, token: token).ConfigureAwait(continueOnCapturedContext: false);

            Validate(descriptor: descriptor, hello: client);
            Verify(client.Proof, Proof(descriptor, "client", nonce, client.Nonce));
            await SendAsync(hello: new(Revision, descriptor.Host, nonce, Proof(descriptor, "server", nonce, client.Nonce)), stream: stream, token: token).ConfigureAwait(continueOnCapturedContext: false);
        } else {
            var host = await ReceiveAsync(stream: stream, token: token).ConfigureAwait(continueOnCapturedContext: false);

            Validate(descriptor: descriptor, hello: host);
            await SendAsync(hello: new(Revision, descriptor.Host, nonce, Proof(descriptor, "client", host.Nonce, nonce)), stream: stream, token: token).ConfigureAwait(continueOnCapturedContext: false);
            var accepted = await ReceiveAsync(stream: stream, token: token).ConfigureAwait(continueOnCapturedContext: false);

            Validate(descriptor: descriptor, hello: accepted);
            if (accepted.Nonce != host.Nonce) { throw new UnauthorizedAccessException(message: "Host challenge changed."); }
            Verify(accepted.Proof, Proof(descriptor, "server", host.Nonce, nonce));
        }
    }

    private static bool IsHex(string? text, int length) => ((text?.Length == length) && !text.AsSpan().ContainsAnyExcept(values: "0123456789abcdefABCDEF"));
    private static void Validate(LocalEndpointHello hello, LocalEndpointDescriptor descriptor) {
        if ((hello.Revision != descriptor.Revision) || (hello.Host != descriptor.Host) || !IsHex(hello.Nonce, 64)) { throw new UnauthorizedAccessException(message: "Attachment host or revision mismatch."); }
    }
    private static string Proof(LocalEndpointDescriptor descriptor, string role, string serverNonce, string clientNonce) => Convert.ToHexString(inArray: HMACSHA256.HashData(
        key: Convert.FromHexString(s: descriptor.Secret), source: Encoding.UTF8.GetBytes(s: $"puck-control:{descriptor.Revision}:{descriptor.Host}:{role}:{serverNonce}:{clientNonce}")));
    private static void Verify(string proof, string expected) {
        if (!IsHex(length: 64, text: proof) || !CryptographicOperations.FixedTimeEquals(left: Convert.FromHexString(s: proof), right: Convert.FromHexString(s: expected))) { throw new UnauthorizedAccessException(message: "Control authentication failed."); }
    }
    private static Task SendAsync(LocalEndpointHello hello, Stream stream, CancellationToken token) =>
        WireFrame.WriteAsync(stream, 0, JsonSerializer.SerializeToUtf8Bytes(hello, LocalEndpointJson.Default.LocalEndpointHello), token);
    private static async Task<LocalEndpointHello> ReceiveAsync(Stream stream, CancellationToken token) {
        var frame = await WireFrame.ReadAsync(ct: token, maxFrameBytes: (4096 + WireFrame.PrefixBytes), stream: stream).ConfigureAwait(continueOnCapturedContext: false);

        if (frame.Failure.Refusal == WireRefusal.ConnectionClosed) { throw new EndOfStreamException(); }
        if (frame.Failure.IsRefusal || (frame.Kind != 0) || frame.Body.IsEmpty) { throw new InvalidDataException(message: "Invalid local handshake frame."); }
        return (JsonSerializer.Deserialize(frame.Body.Span, LocalEndpointJson.Default.LocalEndpointHello) ?? throw new InvalidDataException(message: "Missing handshake."));
    }
}

internal sealed record LocalEndpointDescriptor(int Revision, string Host, int Port, string Secret);
internal sealed record LocalEndpointHello(int Revision, string Host, string Nonce, string Proof);
[JsonSerializable(typeof(LocalEndpointDescriptor))]
[JsonSerializable(typeof(LocalEndpointHello))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8)]
internal sealed partial class LocalEndpointJson : JsonSerializerContext;
