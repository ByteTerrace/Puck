using Puck.Platform.Windows.Interop;

namespace Puck.Platform.Windows;

/// <summary>
/// Resolves the big/small <c>HICON</c> pair a window wears, from <see cref="NativeWindowOptions.IconPath"/> when it
/// names a readable <c>.ico</c>, else from the host executable's <c>IDI_APPLICATION</c> group icon, else from the
/// shell's stock application icon. Every failure falls through to the next source; nothing here throws.
/// </summary>
/// <remarks>
/// Handles are cached for the life of the process and never destroyed. The pair is installed into the window class,
/// which outlives every window built from it and is never unregistered, and the <c>IDI_APPLICATION</c> fallback is a
/// shared handle user32 owns.
/// </remarks>
internal static class Win32IconLoader {
    /// <summary>KEEP IN SYNC with <c>Puck.World.csproj</c>'s <c>&lt;ApplicationIcon&gt;</c>, which stamps the icon
    /// into the apphost at this resource id. <c>LoadImageW</c> resolves <c>IMAGE_ICON</c> against
    /// <c>RT_GROUP_ICON</c>, and a .NET apphost carries exactly one group, here — never at id 1.</summary>
    private const uint IdiApplication = 32512;
    private const uint ImageIcon = 1;
    private const uint LrDefaultColor = 0x0000;
    private const uint LrLoadFromFile = 0x0010;
    private const int SmCxIcon = 11;
    private const int SmCxSmIcon = 49;
    private const int SmCyIcon = 12;
    private const int SmCySmIcon = 50;

    private static readonly Dictionary<string, WindowIcons> CachedIcons = new(capacity: 2);
    private static readonly Lock SyncLock = new();

    /// <summary>Returns the icon pair for one authored icon path, loading it on first use.</summary>
    /// <param name="iconPath">The <c>.ico</c> path to load, or <see langword="null"/> for the host executable's own
    /// icon. A relative path resolves against <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="instanceHandle">The module supplying the unauthored default — <c>GetModuleHandleW(null)</c>.</param>
    /// <returns>The pair. A member is <c>0</c> only when even the shell's stock icon failed to load.</returns>
    public static WindowIcons GetOrLoadIcons(string? iconPath, nint instanceHandle) {
        var cacheKey = (iconPath ?? string.Empty);

        lock (SyncLock) {
            if (CachedIcons.TryGetValue(key: cacheKey, value: out var cached)) {
                return cached;
            }

            // The system-wide metrics, not the per-monitor ones: the pair is a window-class property resolved once
            // per process, and the shell rescales for whichever monitor a window lands on.
            var extents = new IconExtents(
                CxBig: Fallback(metric: User32.GetSystemMetrics(index: SmCxIcon), whenUnavailable: 32),
                CxSmall: Fallback(metric: User32.GetSystemMetrics(index: SmCxSmIcon), whenUnavailable: 16),
                CyBig: Fallback(metric: User32.GetSystemMetrics(index: SmCyIcon), whenUnavailable: 32),
                CySmall: Fallback(metric: User32.GetSystemMetrics(index: SmCySmIcon), whenUnavailable: 16)
            );
            var resolved = (TryLoadFromFile(
                extents: extents,
                icons: out var authored,
                path: iconPath
            )
                ? authored
                : LoadFromModule(
                    extents: extents,
                    instanceHandle: instanceHandle
                )
            );

            CachedIcons[cacheKey] = resolved;

            return resolved;
        }
    }

    private static int Fallback(int metric, int whenUnavailable) => ((metric > 0)
        ? metric
        : whenUnavailable
    );

    /// <summary>Loads the module's own group icon, falling back to the shell's stock application icon.</summary>
    private static WindowIcons LoadFromModule(IconExtents extents, nint instanceHandle) {
        var big = ((nint)0);
        var small = ((nint)0);

        if (instanceHandle != 0) {
            big = User32.LoadImage(
                desiredHeight: extents.CyBig,
                desiredWidth: extents.CxBig,
                instanceHandle: instanceHandle,
                loadFlags: LrDefaultColor,
                name: ((nint)IdiApplication),
                type: ImageIcon
            );
            small = User32.LoadImage(
                desiredHeight: extents.CySmall,
                desiredWidth: extents.CxSmall,
                instanceHandle: instanceHandle,
                loadFlags: LrDefaultColor,
                name: ((nint)IdiApplication),
                type: ImageIcon
            );
        }

        if (big == 0) {
            big = User32.LoadIcon(iconName: ((nint)IdiApplication), instanceHandle: 0);
        }

        if (small == 0) {
            small = User32.LoadIcon(iconName: ((nint)IdiApplication), instanceHandle: 0);
        }

        return new WindowIcons(BigIcon: big, SmallIcon: small);
    }

    /// <summary>Loads both extents from one file, all-or-nothing so a half-readable file never mixes an author's
    /// large icon with the executable's small one.</summary>
    /// <returns><see langword="true"/> when both extents loaded.</returns>
    private static bool TryLoadFromFile(IconExtents extents, out WindowIcons icons, string? path) {
        icons = default;

        if (string.IsNullOrWhiteSpace(value: path)) {
            return false;
        }

        var resolvedPath = (Path.IsPathRooted(path: path)
            ? path
            : Path.Combine(path1: AppContext.BaseDirectory, path2: path)
        );

        if (!File.Exists(path: resolvedPath)) {
            return false;
        }

        var big = User32.LoadImageFromFile(
            desiredHeight: extents.CyBig,
            desiredWidth: extents.CxBig,
            instanceHandle: 0,
            loadFlags: (LrLoadFromFile | LrDefaultColor),
            name: resolvedPath,
            type: ImageIcon
        );
        var small = User32.LoadImageFromFile(
            desiredHeight: extents.CySmall,
            desiredWidth: extents.CxSmall,
            instanceHandle: 0,
            loadFlags: (LrLoadFromFile | LrDefaultColor),
            name: resolvedPath,
            type: ImageIcon
        );

        // Owned handles, unlike the shared IDI_APPLICATION ones, so a partial load frees what it got.
        if ((big == 0) || (small == 0)) {
            if (big != 0) {
                _ = User32.DestroyIcon(iconHandle: big);
            }

            if (small != 0) {
                _ = User32.DestroyIcon(iconHandle: small);
            }

            return false;
        }

        icons = new WindowIcons(BigIcon: big, SmallIcon: small);

        return true;
    }

    /// <summary>The pixel extents one pair is loaded at.</summary>
    private readonly record struct IconExtents(int CxBig, int CxSmall, int CyBig, int CySmall);
}
/// <summary>The two icons a window wears.</summary>
/// <param name="BigIcon">The large icon handle (Alt+Tab, the window's own large icon), or <c>0</c> to leave the slot
/// alone.</param>
/// <param name="SmallIcon">The small icon handle (caption bar, taskbar button), or <c>0</c> to leave the slot
/// alone.</param>
internal readonly record struct WindowIcons(nint BigIcon, nint SmallIcon);
