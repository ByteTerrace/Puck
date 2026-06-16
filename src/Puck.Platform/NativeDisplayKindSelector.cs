namespace Puck.Platform;

public static class NativeDisplayKindSelector {
    public static NativeDisplayKind Select(
        PlatformID platform,
        string? waylandDisplay,
        string? xdgSessionType
    ) {
        if (platform == PlatformID.Win32NT) {
            return NativeDisplayKind.Win32;
        }

        if (platform == PlatformID.Unix) {
            if (
                !string.IsNullOrWhiteSpace(value: waylandDisplay) ||
                string.Equals(
                    a: xdgSessionType,
                    b: "wayland",
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )
            ) {
                return NativeDisplayKind.Wayland;
            }

            return NativeDisplayKind.Xcb;
        }

        if (platform == PlatformID.MacOSX) {
            return NativeDisplayKind.Unsupported;
        }

        return NativeDisplayKind.Unsupported;
    }
    public static NativeDisplayKind SelectCurrent() {
        return Select(
            platform: Environment.OSVersion.Platform,
            waylandDisplay: Environment.GetEnvironmentVariable(variable: "WAYLAND_DISPLAY"),
            xdgSessionType: Environment.GetEnvironmentVariable(variable: "XDG_SESSION_TYPE")
        );
    }
}
