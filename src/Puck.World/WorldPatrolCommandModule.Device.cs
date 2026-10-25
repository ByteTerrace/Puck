using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldPatrolCommandModule {
    /// <summary>Resolves the first seat's rendered view through the live picker of its instance.</summary>
    /// <param name="graphs">The rendered views' pickers, or <see langword="null"/> without a GPU presentation.</param>
    /// <param name="viewports">The seats' rendered view instances, or <see langword="null"/>.</param>
    /// <returns>The resolver the module takes, answering <see langword="null"/> without a rendered view.</returns>
    public static Func<IWorldPatrolView?> ViewOf(WorldViewGraphHost? graphs, WorldSeatViewports? viewports) => () => {
        var instance = (viewports?.Seat(0).RenderInstance ?? WorldViewGraphs.WorldInstance);

        return ((graphs?.FindPicker(instance: instance) is { } picker) ? new PickerView(instance: instance, picker: picker) : null);
    };

    private sealed class PickerView(string instance, SdfWorldPicker picker) : IWorldPatrolView {
        public string Instance => instance;
        public SdfPickResult? Result => picker.Result;

        public long Request(float x, float y) => picker.Request(x: x, y: y);
    }
}
