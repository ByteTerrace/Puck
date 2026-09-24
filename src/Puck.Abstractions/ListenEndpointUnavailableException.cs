using System.Net.Quic;
using System.Net.Sockets;

namespace Puck.Abstractions;

/// <summary>
/// Raised when this host cannot bind a listener at the endpoint a run names: the address is already in use, the
/// operating system refused it, or the transport is not offered here (QUIC without <c>libmsquic</c>). Its message reads
/// <c>&lt;transport&gt; listener &lt;endpoint&gt; unavailable: &lt;reason&gt;</c>.
/// </summary>
public sealed class ListenEndpointUnavailableException : HostResourceUnavailableException {
    /// <summary>Initializes a new instance of the <see cref="ListenEndpointUnavailableException"/> class.</summary>
    /// <param name="transport">The transport token, such as <c>quic</c> or <c>http</c>.</param>
    /// <param name="endpoint">The endpoint the listener was asked to bind, as the run named it.</param>
    /// <param name="reason">What the bind found, in one line.</param>
    /// <param name="innerException">The native bind failure, if any.</param>
    /// <exception cref="ArgumentException"><paramref name="transport"/>, <paramref name="endpoint"/>, or <paramref name="reason"/> is <see langword="null"/>, empty, or white space.</exception>
    public ListenEndpointUnavailableException(string transport, string endpoint, string reason, Exception? innerException = null)
        : base(
        innerException: innerException,
        reason: reason,
        resource: $"{transport} listener {endpoint}"
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        Endpoint = endpoint;
        Transport = transport;
    }

    /// <summary>Gets the endpoint the listener was asked to bind.</summary>
    public string Endpoint { get; }
    /// <summary>Gets the transport token.</summary>
    public string Transport { get; }

    /// <summary>
    /// Classifies a listener start's failure: a <see cref="SocketException"/>, <see cref="QuicException"/>, or
    /// <see cref="PlatformNotSupportedException"/> anywhere in its chain is an endpoint this host cannot bind, and
    /// anything else is not. The innermost such exception's message becomes the reason.
    /// </summary>
    /// <param name="failure">The exception the listener start raised.</param>
    /// <param name="transport">The transport token, such as <c>quic</c> or <c>http</c>.</param>
    /// <param name="endpoint">The endpoint the listener was asked to bind.</param>
    /// <returns>The classified failure carrying <paramref name="failure"/> as its inner exception, or
    /// <see langword="null"/> when <paramref name="failure"/> is not a bind failure.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is <see langword="null"/>.</exception>
    public static ListenEndpointUnavailableException? Classify(Exception failure, string transport, string endpoint) {
        ArgumentNullException.ThrowIfNull(failure);

        Exception? cause = null;

        for (var current = failure; (current is not null); current = current.InnerException) {
            if (current is SocketException or QuicException or PlatformNotSupportedException) {
                cause = current;
            }
        }

        return ((cause is null)
            ? null
            : new ListenEndpointUnavailableException(
                endpoint: endpoint,
                innerException: failure,
                reason: cause.Message.ReplaceLineEndings(replacementText: " "),
                transport: transport
            )
        );
    }
}
