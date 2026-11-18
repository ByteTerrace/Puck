namespace Puck.State;

public sealed partial class StateArena {
    private StateReadAccess? m_readAccess;

    /// <summary>The authority's read operations, without a writable store on their public surface.</summary>
    public StateReadAccess ReadAccess => m_readAccess ??= new StateReadAccess(this, null);

    /// <summary>Starts a fresh, sticky-fault read scope using the caller's whole-row admission policy.</summary>
    /// <param name="admits">The live whole-row policy, evaluated before every acquisition.</param>
    /// <returns>A fresh read scope. The caller must refuse completion if it has failed.</returns>
    public StateReadAccess RestrictReads(Func<int, bool> admits) {
        ArgumentNullException.ThrowIfNull(admits);
        return new StateReadAccess(this, admits);
    }
}
