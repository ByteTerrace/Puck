using System.ComponentModel;
using System.Diagnostics;
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
    private const int Revision = 2;
    private const int MaxWorldCharacters = 512;

    private readonly LocalEndpointDescriptor m_descriptor;

    /// <summary>Creates a fresh host incarnation and 256-bit capability secret for a loopback listener.</summary>
    /// <param name="port">The bound TCP port, from 1 through 65535.</param>
    /// <param name="world">What the endpoint's host is running, such as a world document path, published in the
    /// descriptor so an adapter can tell hosts apart; <see langword="null"/> publishes none. Cut to its last 512
    /// characters.</param>
    public LocalEndpointCapability(int port, string? world = null) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            port,
            1
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            port,
            65535
        );
        m_descriptor = new(
            Revision,
            Guid.NewGuid().ToString(format: "N"),
            port,
            Convert.ToHexString(inArray: RandomNumberGenerator.GetBytes(count: 32)),
            Environment.ProcessId,
            ProcessStart(),
            (((world is not null) && (world.Length > MaxWorldCharacters))
                ? world[^MaxWorldCharacters..]
                : world)
        );
    }

    private LocalEndpointCapability(LocalEndpointDescriptor descriptor) { m_descriptor = descriptor; }

    /// <summary>What this capability publishes beside its secret: the incarnation id, the host process and what it runs.</summary>
    public LocalEndpointInfo Info => new(
        Id: m_descriptor.Host,
        ProcessId: m_descriptor.ProcessId,
        StartedUtc: m_descriptor.StartedUtc,
        World: m_descriptor.World
    );
    /// <summary>The loopback TCP port advertised by this capability.</summary>
    public int Port => m_descriptor.Port;

    private static DateTimeOffset ProcessStart() {
        using var self = Process.GetCurrentProcess();

        return new DateTimeOffset(dateTime: self.StartTime.ToUniversalTime());
    }
    private static bool IsHex(string? text, int length) => ((text?.Length == length) && !text.AsSpan().ContainsAnyExcept(values: "0123456789abcdefABCDEF"));
    private static string Proof(LocalEndpointDescriptor descriptor, string role, string serverNonce, string clientNonce) => Convert.ToHexString(inArray: HMACSHA256.HashData(
        key: Convert.FromHexString(s: descriptor.Secret),
        source: Encoding.UTF8.GetBytes(s: $"puck-control:{descriptor.Revision}:{descriptor.Host}:{role}:{serverNonce}:{clientNonce}")
    ));
    private static async Task<LocalEndpointHello> ReceiveAsync(Stream stream, CancellationToken token) {
        var frame = await WireFrame.ReadAsync(
            ct: token,
            maxFrameBytes: (4096 + WireFrame.PrefixBytes),
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (frame.Failure.Refusal == WireRefusal.ConnectionClosed) { throw new EndOfStreamException(); }
        if (
            frame.Failure.IsRefusal ||
            (frame.Kind != 0) ||
            frame.Body.IsEmpty
        ) { throw new InvalidDataException(message: "Invalid local handshake frame."); }
        return (JsonSerializer.Deserialize(
            frame.Body.Span,
            LocalEndpointJson.Default.LocalEndpointHello
        ) ?? throw new InvalidDataException(message: "Missing handshake."));
    }
    private static Task SendAsync(LocalEndpointHello hello, Stream stream, CancellationToken token) =>
        WireFrame.WriteAsync(
            stream,
            0,
            JsonSerializer.SerializeToUtf8Bytes(
                hello,
                LocalEndpointJson.Default.LocalEndpointHello
            ),
            token
        );
    private static void Validate(LocalEndpointHello hello, LocalEndpointDescriptor descriptor) {
        if (
            (hello.Revision != descriptor.Revision) ||
            (hello.Host != descriptor.Host) ||
            !IsHex(
            hello.Nonce,
            64
        )
        ) { throw new UnauthorizedAccessException(message: "Attachment host or revision mismatch."); }
    }
    private static void Verify(string proof, string expected) {
        if (
            !IsHex(
            length: 64,
            text: proof
        ) ||
            !CryptographicOperations.FixedTimeEquals(
            left: Convert.FromHexString(s: proof),
            right: Convert.FromHexString(s: expected)
        )
        ) { throw new UnauthorizedAccessException(message: "Control authentication failed."); }
    }

    /// <summary>Proves both peers hold this capability using role-separated HMAC challenges.</summary>
    /// <param name="stream">The connected local stream; callers close it on any failure.</param>
    /// <param name="server">True for the listener; false for the attaching client.</param>
    /// <param name="token">The caller's handshake deadline; cancellation invalidates the connection. The handshake
    /// has no deadline of its own.</param>
    /// <returns>Completion after the peer proves possession of the secret.</returns>
    /// <exception cref="UnauthorizedAccessException">The peer fails identity or proof validation.</exception>
    public async Task AuthenticateAsync(Stream stream, bool server, CancellationToken token) {
        var descriptor = m_descriptor;
        var nonce = Convert.ToHexString(inArray: RandomNumberGenerator.GetBytes(count: 32));

        if (server) {
            await SendAsync(
                hello: new(
                    Revision,
                    descriptor.Host,
                    nonce,
                    ""
                ),
                stream: stream,
                token: token
            ).ConfigureAwait(continueOnCapturedContext: false);
            var client = await ReceiveAsync(
                stream: stream,
                token: token
            ).ConfigureAwait(continueOnCapturedContext: false);

            Validate(
                descriptor: descriptor,
                hello: client
            );
            Verify(
                client.Proof,
                Proof(
                    descriptor,
                    "client",
                    nonce,
                    client.Nonce
                )
            );
            await SendAsync(
                hello: new(
                    Revision,
                    descriptor.Host,
                    nonce,
                    Proof(
                        descriptor,
                        "server",
                        nonce,
                        client.Nonce
                    )
                ),
                stream: stream,
                token: token
            ).ConfigureAwait(continueOnCapturedContext: false);
        } else {
            var host = await ReceiveAsync(
                stream: stream,
                token: token
            ).ConfigureAwait(continueOnCapturedContext: false);

            Validate(
                descriptor: descriptor,
                hello: host
            );
            await SendAsync(
                hello: new(
                    Revision,
                    descriptor.Host,
                    nonce,
                    Proof(
                        descriptor,
                        "client",
                        host.Nonce,
                        nonce
                    )
                ),
                stream: stream,
                token: token
            ).ConfigureAwait(continueOnCapturedContext: false);
            var accepted = await ReceiveAsync(
                stream: stream,
                token: token
            ).ConfigureAwait(continueOnCapturedContext: false);

            Validate(
                descriptor: descriptor,
                hello: accepted
            );
            if (accepted.Nonce != host.Nonce) { throw new UnauthorizedAccessException(message: "Host challenge changed."); }
            Verify(
                accepted.Proof,
                Proof(
                    descriptor,
                    "server",
                    host.Nonce,
                    nonce
                )
            );
        }
    }
    /// <summary>Opens and validates a descriptor's owner, DACL, bounded contents and revision using the opened handle.</summary>
    /// <param name="path">The host's descriptor file path.</param>
    /// <returns>The capability used to connect to and authenticate the advertised local endpoint.</returns>
    /// <exception cref="PlatformNotSupportedException">The platform is neither Windows nor Linux x64.</exception>
    /// <exception cref="UnauthorizedAccessException">The descriptor belongs to or grants access to another identity.</exception>
    /// <exception cref="InvalidDataException">The descriptor is malformed or its revision is unsupported.</exception>
    public static LocalEndpointCapability ReadDescriptor(string path) {
        using var file = (OperatingSystem.IsWindows()
            ? new FileStream(
                access: FileAccess.Read,
                mode: FileMode.Open,
                path: path,
                share: FileShare.ReadWrite
            )
            : (OperatingSystem.IsLinux()
                ? LocalUserAccess.OpenLinuxFile(
                    path,
                    create: false
                )
                : throw new PlatformNotSupportedException(message: "Local control supports Windows and Linux x64.")
        ));
        // Inspect the opened handle, not a path that could be replaced between the check and read.
        if (OperatingSystem.IsWindows()) {
            using var identity = WindowsIdentity.GetCurrent();
            var security = file.GetAccessControl();

            if (!Equals(
                objA: security.GetOwner(targetType: typeof(SecurityIdentifier)),
                objB: identity.User
            )) { throw new UnauthorizedAccessException(message: "Attachment file belongs to another user."); }
            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier)
            )) {
                if (
                    (rule.AccessControlType == AccessControlType.Allow) &&
                    !Equals(
                    objA: rule.IdentityReference,
                    objB: identity.User
                )
                ) {
                    throw new UnauthorizedAccessException(message: "Attachment file grants access to another identity.");
                }
            }
        }
        if (file.Length is <= 0 or > 4096) { throw new InvalidDataException(message: "Invalid attachment file size."); }
        var data = new byte[((int)file.Length)];

        file.ReadExactly(buffer: data);
        var descriptor = (JsonSerializer.Deserialize(
            data,
            LocalEndpointJson.Default.LocalEndpointDescriptor
        ) ?? throw new InvalidDataException(message: "Missing attachment descriptor."));

        if (
            (descriptor.Revision != Revision) ||
            (descriptor.Port is <= 0 or > 65535) ||
            (descriptor.ProcessId <= 0) ||
            ((descriptor.World?.Length ?? 0) > MaxWorldCharacters) ||
            !Guid.TryParseExact(
            descriptor.Host,
            "N",
            out _
        ) ||
            !IsHex(
            descriptor.Secret,
            64
        )
        ) {
            throw new InvalidDataException(message: "Invalid attachment descriptor or unsupported revision.");
        }
        return new(descriptor);
    }
    /// <summary>Creates a user-private descriptor; Windows additionally holds it open against modification or replacement.</summary>
    /// <param name="path">A new file path. Only this path, never the file contents, may be logged or passed in arguments.</param>
    /// <returns>The handle the host must retain until the listener closes; the host then deletes the file.</returns>
    /// <exception cref="PlatformNotSupportedException">The platform is neither Windows nor Linux x64.</exception>
    public FileStream WriteDescriptor(string path) {
        var descriptor = m_descriptor;
        var file = (OperatingSystem.IsWindows()
            ? new FileInfo(fileName: path).Create(
                FileMode.CreateNew,
                FileSystemRights.Read | FileSystemRights.Write,
                FileShare.Read,
                4096,
                FileOptions.None,
                LocalUserAccess.Create<FileSecurity>()
            )
            : (OperatingSystem.IsLinux()
                ? LocalUserAccess.OpenLinuxFile(
                    path,
                    create: true
                )
                : throw new PlatformNotSupportedException(message: "Local control supports Windows and Linux x64.")
        ));

        try {
            file.Write(buffer: JsonSerializer.SerializeToUtf8Bytes(
                descriptor,
                LocalEndpointJson.Default.LocalEndpointDescriptor
            ));
            file.Flush(flushToDisk: true);
            return file;
        } catch { file.Dispose(); throw; }
    }
}

/// <summary>What an endpoint's descriptor says about the host that published it, without its secret.</summary>
/// <param name="Id">The incarnation's attachment id: 32 hexadecimal digits, new for every endpoint start.</param>
/// <param name="ProcessId">The host process.</param>
/// <param name="StartedUtc">When the host process started.</param>
/// <param name="World">What the host runs, such as its world document path, or <see langword="null"/>.</param>
public sealed record LocalEndpointInfo(string Id, int ProcessId, DateTimeOffset StartedUtc, string? World) {
    /// <summary>Whether the host process is still running: a descriptor left behind by an exited or crashed host, or
    /// whose process id another process now holds, is not. A process this one cannot inspect counts as running, since
    /// the connection then decides.</summary>
    /// <returns><see langword="false"/> only when the host process is provably gone.</returns>
    public bool IsHostRunning() {
        try {
            using var host = Process.GetProcessById(processId: ProcessId);

            return (!host.HasExited && (Math.Abs(value: (new DateTimeOffset(dateTime: host.StartTime.ToUniversalTime()) - StartedUtc).TotalSeconds) < 2));
        } catch (Exception error) when ((error is ArgumentException or InvalidOperationException or Win32Exception)) {
            return (error is Win32Exception);
        }
    }
}
internal sealed record LocalEndpointDescriptor(int Revision, string Host, int Port, string Secret, int ProcessId, DateTimeOffset StartedUtc, string? World);
internal sealed record LocalEndpointHello(int Revision, string Host, string Nonce, string Proof);
[JsonSerializable(typeof(LocalEndpointDescriptor))]
[JsonSerializable(typeof(LocalEndpointHello))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8)]
internal sealed partial class LocalEndpointJson : JsonSerializerContext;
