using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Presentation;
namespace Puck.Demo;

/// <summary>
/// Resolves the demo's <c>--present-mode</c> / <c>--surface-format</c> flags to a neutral
/// <see cref="PresentationOptions"/> and registers it, so whichever backend hosts the window honors the selection.
/// Kept out of <c>Program</c> so the composition root's type coupling stays bounded.
/// </summary>
internal static class DemoPresentation {
    /// <summary>Registers the neutral presentation preferences parsed from the demo flags.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="presentMode">The raw <c>--present-mode</c> value (vsync/mailbox/immediate/adaptive).</param>
    /// <param name="surfaceFormat">The raw <c>--surface-format</c> value (r8g8b8a8/b8g8r8a8).</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddDemoPresentation(this IServiceCollection services, string presentMode, string surfaceFormat) {
        var mode = presentMode.ToLowerInvariant() switch {
            "mailbox" => PresentMode.Mailbox,
            "immediate" => PresentMode.Immediate,
            "adaptive" => PresentMode.Adaptive,
            _ => PresentMode.Vsync,
        };
        var format = surfaceFormat.ToLowerInvariant() switch {
            "b8g8r8a8" => SurfaceFormat.B8G8R8A8Unorm,
            _ => SurfaceFormat.R8G8B8A8Unorm,
        };

        return services.AddSingleton(implementationInstance: new PresentationOptions {
            PresentMode = mode,
            SurfaceFormat = format,
        });
    }
}
