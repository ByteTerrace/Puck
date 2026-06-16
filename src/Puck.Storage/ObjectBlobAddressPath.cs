namespace Puck.Storage;

internal static class ObjectBlobAddressPath {
    public static string[] GetKeySegments(ObjectBlobAddress address) {
        if (string.IsNullOrWhiteSpace(value: address.Key)) {
            throw new ArgumentException(
                message: "The storage key must not be empty.",
                paramName: nameof(address)
            );
        }

        if (
            Path.IsPathRooted(path: address.Key) ||
            address.Key.StartsWith(value: '/') ||
            address.Key.StartsWith(value: '\\')
        ) {
            throw new ArgumentException(
                message: "The storage key must be a relative path.",
                paramName: nameof(address)
            );
        }

        var segments = address.Key.Split(
            options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
            separator: ['/', '\\']
        );

        if (segments.Length == 0) {
            throw new ArgumentException(
                message: "The storage key must contain at least one segment.",
                paramName: nameof(address)
            );
        }

        foreach (var segment in segments) {
            if (segment is "." or "..") {
                throw new ArgumentException(
                    message: "The storage key must not contain current-directory or parent-directory segments.",
                    paramName: nameof(address)
                );
            }
        }

        return segments;
    }
    public static string GetNormalizedKey(ObjectBlobAddress address) {
        return string.Join(
            '/',
            GetKeySegments(address: address)
        );
    }
    public static string GetRoot(ObjectBlobAddress address) {
        if (address.ObjectId == Guid.Empty) {
            throw new ArgumentException(
                message: "The object id must not be empty.",
                paramName: nameof(address)
            );
        }

        return address.ObjectId.ToString();
    }
}
