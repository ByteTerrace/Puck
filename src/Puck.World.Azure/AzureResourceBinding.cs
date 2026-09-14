using Azure.Core;

namespace Puck.World.Azure;

/// <summary>A generic ARM resource mutation verb. Resource providers define which verbs their paths support.</summary>
public enum AzureResourceMethod {
    /// <summary>Invokes a resource action.</summary>
    Post,
    /// <summary>Partially updates a resource.</summary>
    Patch,
    /// <summary>Deletes a resource.</summary>
    Delete,
    /// <summary>Creates or replaces a resource.</summary>
    Put,
}
/// <summary>Host-selected resource and operation scope. No credentials or world-supplied destination URL.</summary>
/// <param name="Name">The binding name exposed to authorized callers.</param>
/// <param name="ResourceId">An unescaped, absolute ARM resource id, including its subscription or tenant scope.</param>
/// <param name="Incarnation">Host-maintained identity of this concrete resource incarnation; change it on replacement.</param>
/// <param name="Method">The generic HTTP operation.</param>
/// <param name="ApiVersion">The resource provider's explicit API version.</param>
/// <param name="Action">An optional unescaped action path appended to the resource id, such as start.</param>
/// <param name="IfMatch">An optional ETag precondition for providers that support it.</param>
/// <param name="QueryParameters">Optional host-bound provider query arguments. api-version is declared separately.</param>
public sealed record AzureResourceBinding(string Name, string ResourceId, string Incarnation,
    AzureResourceMethod Method, string ApiVersion, string? Action = null, string? IfMatch = null,
    IReadOnlyDictionary<string, string>? QueryParameters = null) {
    internal string GetQuery() {
        var query = ("?api-version=" + Uri.EscapeDataString(stringToEscape: ApiVersion));
        var names = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase) { "api-version" };

        foreach (var (name, value) in (QueryParameters ?? new Dictionary<string, string>()).OrderBy(
            pair => pair.Key,
            StringComparer.Ordinal
        )) {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(value);
            if (!names.Add(item: name)) { throw new ArgumentException(
                message: "Query parameter names must be unique and cannot override api-version.",
                paramName: nameof(QueryParameters)
            ); }
            query += ((("&" + Uri.EscapeDataString(stringToEscape: name)) + "=") + Uri.EscapeDataString(stringToEscape: value));
        }
        return query;
    }
    internal string ValidateAndGetPath() {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(Incarnation);
        ArgumentException.ThrowIfNullOrWhiteSpace(ApiVersion);
        if (!Enum.IsDefined(value: Method)) { throw new ArgumentOutOfRangeException(paramName: nameof(Method)); }
        if (
            string.IsNullOrWhiteSpace(value: ResourceId) ||
            !ResourceId.StartsWith(value: '/') ||
            ResourceId.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "//"
        )
        ) {
            throw new ArgumentException(
                message: "An absolute ARM resource id is required.",
                paramName: nameof(ResourceId)
            );
        }
        _ = new ResourceIdentifier(resourceId: ResourceId);
        var path = EncodePath(path: ResourceId[1..]);

        if (Action is not null) { path += ("/" + EncodePath(path: Action)); }
        if (
            (IfMatch is not null) &&
            (string.IsNullOrWhiteSpace(value: IfMatch) || IfMatch.Any(predicate: char.IsControl))
        ) {
            throw new ArgumentException(
                message: "IfMatch must be a nonempty header value.",
                paramName: nameof(IfMatch)
            );
        }
        return ("/" + path);
    }

    private static string EncodePath(string path) {
        var segments = path.Split('/');

        foreach (var segment in segments) {
            if (
                string.IsNullOrWhiteSpace(value: segment) ||
                (segment is "." or "..") ||
                segment.AsSpan().ContainsAny(
                value0: '\\',
                value1: '?',
                value2: '#'
            ) ||
                segment.Contains(value: '%') ||
                segment.Any(predicate: char.IsControl)
            ) {
                throw new ArgumentException(message: "ARM ids and actions must contain unescaped path segments, without traversal or query syntax.");
            }
        }
        return string.Join(
            separator: '/',
            values: segments.Select(selector: Uri.EscapeDataString)
        );
    }
}
