namespace Puck.Commands;

// Internal lifecycle seam for a mutable binding resolver. A router subscribes so a profile swap first turns every
// held command into a deterministic cancellation; the public IInputBindings surface remains resolver-shaped.
internal interface IInputBindingsReloadSource {
    event Action Reloading;
}
