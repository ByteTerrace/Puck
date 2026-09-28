using Puck.SdfVm;

namespace Puck.World.Client;

public sealed partial class WorldViewGraphHost {
    /// <summary>Gets or sets the shared GPU pickers for the host's rendered SDF views. The editor and presentation
    /// tools request a normalized pixel from the same one-pixel asynchronous visibility readback.</summary>
    public SdfWorldPasses? Pickers { get; set; }

    /// <summary>Finds an active view's presentation picker. Requesting a pick never changes simulation state.</summary>
    /// <param name="instance">The rendered view instance.</param>
    /// <returns>The picker, or null before that SDF view has been registered.</returns>
    public SdfWorldPicker? FindPicker(string instance) => Pickers?.FindPicker(instance: instance);
}
