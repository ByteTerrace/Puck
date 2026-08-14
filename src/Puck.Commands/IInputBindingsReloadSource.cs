namespace Puck.Commands;

/// <summary>Optional lifecycle seam for a mutable <see cref="IInputBindings"/>. The <see cref="InputRouter"/>
/// subscribes so a profile swap first turns affected held commands into deterministic cancellations and retires
/// cached resolutions of the old immutable binding lists.</summary>
public interface IInputBindingsReloadSource {
    /// <summary>Raised immediately before bindings are replaced.</summary>
    /// <remarks>A slot value scopes the replacement to that logical player. <see langword="null"/> means the
    /// resolver is replacing bindings for every slot it owns.</remarks>
    event Action<int?> Reloading;
}
