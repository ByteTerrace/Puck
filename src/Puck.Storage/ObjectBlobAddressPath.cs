namespace Puck.Storage;

internal static class ObjectBlobAddressPath {
    public static string[] GetKeySegments(ObjectBlobAddress address) {
        if (string.IsNullOrWhiteSpace(value: address.Key)) {
            throw new ArgumentException(
                message: "The storage key must not be empty.",
                paramName: nameof(address)
            );
        }

        return GetSegments(
            description: "storage key",
            path: address.Key
        );
    }
    public static string GetNormalizedKey(ObjectBlobAddress address) {
        return string.Join(
            '/',
            GetKeySegments(address: address)
        );
    }
    public static string GetRoot(Guid objectId) {
        if (objectId == Guid.Empty) {
            throw new ArgumentException(
                message: "The object id must not be empty.",
                paramName: nameof(objectId)
            );
        }

        return objectId.ToString();
    }
    /// <summary>Normalizes a list operation's key prefix: empty means "every key under the object"; a non-empty value
    /// is validated exactly like a key (relative, no dot segments) and rendered back with a trailing slash so it can
    /// only match whole path segments — a prefix "worlds" must not also match a sibling key "worlds2/x.json".</summary>
    public static string GetNormalizedPrefix(string keyPrefix) {
        ArgumentNullException.ThrowIfNull(argument: keyPrefix);

        if (keyPrefix.Length == 0) {
            return string.Empty;
        }

        var segments = GetSegments(
            description: "list prefix",
            path: keyPrefix
        );

        return $"{string.Join('/', segments)}/";
    }

    private static string[] GetSegments(string path, string description) {
        if (
            Path.IsPathRooted(path: path) ||
            path.StartsWith(value: '/') ||
            path.StartsWith(value: '\\')
        ) {
            throw new ArgumentException(
                message: $"The {description} must be a relative path.",
                paramName: nameof(path)
            );
        }

        var segments = path.Split(
            options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
            separator: ['/', '\\']
        );

        if (segments.Length == 0) {
            throw new ArgumentException(
                message: $"The {description} must contain at least one segment.",
                paramName: nameof(path)
            );
        }

        foreach (var segment in segments) {
            if (segment is "." or "..") {
                throw new ArgumentException(
                    message: $"The {description} must not contain current-directory or parent-directory segments.",
                    paramName: nameof(path)
                );
            }
        }

        return segments;
    }
}
