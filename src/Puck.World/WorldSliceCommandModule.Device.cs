using Puck.Commands;
using Puck.SdfVm;

namespace Puck.World;

// The slice's device work: queuing it on a live residency's bake, whose sampler only a GPU runs, and writing its PNG when
// the last lattice answers on the render thread.
public sealed partial class WorldSliceCommandModule {
    private static CommandResult ArmOn(WorldSliceRequest request, SdfWorldResidency residency) {
        var slice = new SdfFieldSlice(
            axis: request.Axis,
            bands: request.Bands,
            centre: request.Centre,
            completed: answered => Console.Error.WriteLine(value: Write(path: request.Path, slice: answered)),
            half: request.Half,
            offset: request.Offset,
            resolution: request.Resolution
        );

        return (residency.MeshPathBake.Probe(slice: slice)
            ? new CommandResult(Output: Armed(request: request))
            : CommandResult.Error(output: $"[{Verb}: a slice is still sampling — wait for its [{Verb}] line (world.wait bakes <seconds>)]"));
    }
}
