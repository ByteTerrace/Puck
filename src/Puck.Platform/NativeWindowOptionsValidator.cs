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

        WindowOptionsValidation.AddFailures(failures: failures, title: options.Title, width: options.Width, height: options.Height);

        if (
            (options.Mode == NativeWindowMode.PlatformWindow) &&
            !m_platformSupport.SupportsWindowFor(requested: options.DisplayKind)
        ) {
            failures.Add(item: $"{nameof(NativeWindowOptions.Mode)} value '{NativeWindowMode.PlatformWindow}' is not supported for display kind '{m_platformSupport.ResolveDisplayKind(requested: options.DisplayKind)}'. Supported platform window kinds are Win32, Wayland, and Xcb.");
        }

        return ((failures.Count == 0)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures: failures));
    }
}
