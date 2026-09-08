using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Puck.Networking;

/// <summary>Parses a peer address without resolving its DNS name ahead of a connection attempt.</summary>
public static class PeerEndpoint {
    /// <summary>Formats a numeric or DNS endpoint as a peer address with an explicit port.</summary>
    /// <param name="endpoint">The endpoint to render without resolving DNS.</param>
    /// <returns>An address accepted by <see cref="TryParse"/>.</returns>
    /// <exception cref="ArgumentException">The endpoint type or port is unsupported.</exception>
    public static string Format(EndPoint endpoint) => endpoint switch {
        IPEndPoint { Port: > 0 } numeric => numeric.ToString(),
        DnsEndPoint { Port: > 0 } dns => $"{dns.Host}:{dns.Port}",
        _ => throw new ArgumentException(message: "A numeric or DNS peer endpoint with a nonzero port is required.", paramName: nameof(endpoint)),
    };
    /// <summary>Accepts numeric endpoints and explicit DNS host/port pairs.</summary>
    /// <param name="value">The peer address, including its port.</param>
    /// <param name="endpoint">The socket or DNS endpoint on success.</param>
    /// <returns>Whether the address is valid and includes a nonzero port.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out EndPoint? endpoint) {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value: value)) { return false; }
        if (IPEndPoint.TryParse(result: out var numeric, s: value) && (numeric.Port > 0)) { endpoint = numeric; return true; }
        if (!Uri.TryCreate(result: out var uri, uriKind: UriKind.Absolute, uriString: ("quic://" + value)) ||
            (uri.HostNameType != UriHostNameType.Dns) || (uri.Port is < 1 or > 65535) ||
            (uri.UserInfo.Length != 0) || (uri.AbsolutePath != "/") || (uri.Query.Length != 0) || (uri.Fragment.Length != 0)) { return false; }
        endpoint = new DnsEndPoint(host: uri.IdnHost, port: uri.Port);
        return true;
    }
}
