using Microsoft.Extensions.Options;

namespace Puck.Platform;

public sealed class NativeWindowOptionsValidator : IValidateOptions<NativeWindowOptions> {
    private readonly INativeWindowPlatformSupport m_platformSupport;

    public NativeWindowOptionsValidator(INativeWindowPlatformSupport platformSupport) {
        ArgumentNullException.ThrowIfNull(platformSupport);

        m_platformSupport = platformSupport;
    }
    public NativeWindowOptionsValidator()
        : this(new NativeWindowPlatformSupport(nativeDisplayEnvironment: new NativeDisplayEnvironment())) {
    }

    public ValidateOptionsResult Validate(string? name, NativeWindowOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>(capacity: 5);

        if (string.IsNullOrWhiteSpace(value: options.Title)) {
            failures.Add(item: $"{nameof(NativeWindowOptions.Title)} must be provided.");
        }

        if (options.Width == 0) {
            failures.Add(item: $"{nameof(NativeWindowOptions.Width)} must be greater than zero.");
        } else if (options.Width > int.MaxValue) {
            failures.Add(item: $"{nameof(NativeWindowOptions.Width)} must be less than or equal to {int.MaxValue}.");
        }

        if (options.Height == 0) {
            failures.Add(item: $"{nameof(NativeWindowOptions.Height)} must be greater than zero.");
        } else if (options.Height > int.MaxValue) {
            failures.Add(item: $"{nameof(NativeWindowOptions.Height)} must be less than or equal to {int.MaxValue}.");
        }

        if (
            (options.Mode == NativeWindowMode.PlatformWindow) &&
            !m_platformSupport.SupportsWindowFor(requested: options.DisplayKind)
        ) {
            failures.Add(item: $"{nameof(NativeWindowOptions.Mode)} value '{NativeWindowMode.PlatformWindow}' is not supported for display kind '{m_platformSupport.ResolveDisplayKind(requested: options.DisplayKind)}'. Supported platform window kinds are Win32, Wayland, Xcb, and Vi.");
        }

        return ((failures.Count == 0)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures));
    }
}
