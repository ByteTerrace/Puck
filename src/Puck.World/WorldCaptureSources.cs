using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The capture scheduler's view of the source instances the render graph runs: the instance each screen reads,
/// through the screen binder, and the reference of each uploaded source whose producer states the image it shows (a test
/// pattern, a QR code, a machine output), through the render probe's runtime.</summary>
/// <param name="probe">The render probe, whose root holds the runtime once the renderer is composed.</param>
/// <param name="binder">The screen binder, which names the source instance each screen reads.</param>
internal sealed class WorldCaptureSources(WorldRenderProbe probe, WorldScreenBinder binder) : IWorldCaptureSources {
    /// <inheritdoc/>
    public string? InstanceOf(int screen) => binder.ReadOf(screen: screen);
    /// <inheritdoc/>
    public IImageSourceReference? ReferenceOf(string instance) {
        if (probe.Root?.Runtime is not { } runtime) {
            return null;
        }

        var index = runtime.Instances.IndexOf(name: instance);

        if (index < 0) {
            return null;
        }

        return runtime.Source(instance: index) switch {
            WorldImageSourceUpload upload => (upload.Opening.Feed as IImageSourceReference),
            MachineVideoSourceUpload { Descriptor: not null } machine => machine,
            _ => null,
        };
    }
}
