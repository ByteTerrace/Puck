using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The <c>gpu.capture</c> verb: the operator's on-demand GPU capture of the next frames a GPU host produces, through the
/// capture tool the World loaded before its device (<c>--capture-tool pix|renderdoc</c>, <see cref="GpuFrameCapture"/>).
/// <c>gpu.capture &lt;path&gt; [&lt;frames&gt;]</c> arms a capture of the next frames (one when absent) to the path, made
/// absolute against the process's working directory, and answers what it armed; each written file is narrated on
/// standard error as <c>[gpu.capture] &lt;tool&gt; wrote &lt;file&gt;</c>. A bare <c>gpu.capture</c> describes the tool
/// and what is armed. The verb answers the operator alone (<see cref="CommandAudience.Operator"/>).
/// </summary>
/// <param name="capture">The host's frame capture.</param>
public sealed class GpuCaptureCommandModule(GpuFrameCapture capture) : ICommandModule {
    private const string Verb = "gpu.capture";

    private CommandResult Handle(WireArgs args) {
        if (args.Count == 0) {
            return new CommandResult(Output: capture.Describe());
        }

        var frames = 1;

        if (
            (args.Count > 2) ||
            ((args.Count == 2) && !args.TryInt(
                index: 1,
                value: out frames
            ))
        ) {
            return CommandResult.Error(output: "[gpu.capture: expected gpu.capture [<path> [<frames>]]]");
        }

        var path = Path.GetFullPath(path: args[0].ToString());

        return (capture.TryArm(
            frames: frames,
            path: path,
            refusal: out var refusal
        )
            ? new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[gpu.capture: armed {capture.Tool!.Name} frames={frames} -> {path}]"))
            : CommandResult.Error(output: $"[gpu.capture: refused: {refusal}]")
        );
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            description: $"Captures the next frames a GPU host produces with the capture tool the World loaded before its device (--capture-tool pix|renderdoc; operator only): gpu.capture <path> [<frames>] arms a capture of the next 1 to {GpuFrameCapture.MaxFrames} frames (default 1) to the path, made absolute against the working directory; PIX writes one .wpix holding them all, RenderDoc one .rdc a frame (<path>_capture.rdc, then <path>_capture_2.rdc and on). Each written file is narrated on stderr as [gpu.capture] <tool> wrote <file>. A bare gpu.capture describes the tool and what is armed. Refused with no tool loaded, or while a capture is still armed or being written. Position a capture with world.wait: a crossing's tick is the same on every run of one document and input.",
            handler: (_, args) => Handle(args: args),
            name: Verb
        );
    }
}
/// <summary>Registers the <c>gpu.capture</c> verb.</summary>
public static class GpuCaptureServiceRegistration {
    /// <summary>Registers <see cref="GpuCaptureCommandModule"/> over the composition's <see cref="GpuFrameCapture"/> as an
    /// <see cref="ICommandModule"/>.</summary>
    /// <param name="services">The service collection of a presentation shape that creates a GPU device.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddGpuCaptureCommand(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddSingleton<ICommandModule>(implementationFactory: static sp => new GpuCaptureCommandModule(capture: sp.GetRequiredService<GpuFrameCapture>()));
    }
}
