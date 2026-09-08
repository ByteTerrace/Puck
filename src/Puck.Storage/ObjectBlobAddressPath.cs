namespace Puck.Storage;

internal static class ObjectBlobAddressPath {
    private static string[] GetSegments(string path, string description) {
        if (path.Length > 4096) { throw new ArgumentException($"The {description} exceeds its length ceiling.", nameof(path)); }
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
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: ['/', '\\']
        );

        if (segments.Length == 0 || segments.Length > 64) {
            throw new ArgumentException(
                message: $"The {description} must contain between one and 64 segments.",
                paramName: nameof(path)
            );
        }

        foreach (var segment in segments) {
            var device = segment.Split('.')[0];
            if (segment is "." or ".." || segment.EndsWith('.') || segment != segment.Trim() ||
                segment.Any(c => char.IsControl(c) || c is ':' or '<' or '>' or '"' or '|' or '?' or '*') ||
                segment.StartsWith(".puck-", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("CON", StringComparison.OrdinalIgnoreCase) || device.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                device.Equals("AUX", StringComparison.OrdinalIgnoreCase) || device.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (device.Length == 4 && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                    device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(device[3]))) {
                throw new ArgumentException(
                    message: $"The {description} contains an unsafe or reserved path segment.",
                    paramName: nameof(path)
                );
            }
        }

        return segments;
    }

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

        return $"{string.Join(
            '/',
            segments
        )}/";
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
}
