namespace Puck.Platform;

public interface INativeWindowPlatformSupport {
    /// <summary>The display kind auto-detected from the current environment.</summary>
    NativeDisplayKind CurrentDisplayKind { get; }

    /// <summary>True when the auto-detected <see cref="CurrentDisplayKind"/> has a native
    /// platform window backend.</summary>
    bool SupportsPlatformWindow { get; }

    /// <summary>Resolves a requested display kind: returns <paramref name="requested"/>
    /// unchanged when it is anything other than <see cref="NativeDisplayKind.Auto"/>,
    /// otherwise the auto-detected <see cref="CurrentDisplayKind"/>.</summary>
    NativeDisplayKind ResolveDisplayKind(NativeDisplayKind requested);

    /// <summary>True when the resolved <paramref name="requested"/> kind has a native
    /// platform window backend.</summary>
    bool SupportsWindowFor(NativeDisplayKind requested);
}
