using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>A mutation that has passed every gate it can be refused at and has not been installed: the candidate
/// document, what was compiled and built for it, and the resources the addon and machine hosts staged.</summary>
/// <remarks>Whoever holds one installs it through <see cref="WorldDocument.InstallPrepared"/> or disposes it.
/// Disposing one that was not installed releases what was staged; a plan its host committed is left alone.</remarks>
internal sealed class WorldPreparedMutation(
    WorldMutation mutation,
    WorldDefinition candidate,
    WorldRuleCompilation? compilation,
    StateArena? arena,
    WorldSolidField? solids,
    bool solidAffecting,
    IWorldAddonPreparedPlan? addonPlan,
    IWorldMachinePreparedPlan? machinePlan,
    int[]? tickWrittenEntity,
    WorldPrincipal[]? tickWrittenPrincipal,
    bool[]? tickCollided,
    CellName? evictedKey,
    ulong tick,
    ulong engineTick
) : IDisposable {
    private bool m_addonSettled;
    private bool m_machineSettled;

    public IWorldAddonPreparedPlan? AddonPlan { get; } = addonPlan;
    public StateArena? Arena { get; } = arena;
    public WorldDefinition Candidate { get; } = candidate;
    public WorldRuleCompilation? Compilation { get; } = compilation;
    public ulong EngineTick { get; } = engineTick;
    public CellName? EvictedKey { get; } = evictedKey;
    public IWorldMachinePreparedPlan? MachinePlan { get; } = machinePlan;
    public WorldMutation Mutation { get; } = mutation;
    public bool SolidAffecting { get; } = solidAffecting;
    public WorldSolidField? Solids { get; } = solids;
    public ulong Tick { get; } = tick;
    public bool[]? TickCollided { get; } = tickCollided;
    public int[]? TickWrittenEntity { get; } = tickWrittenEntity;
    public WorldPrincipal[]? TickWrittenPrincipal { get; } = tickWrittenPrincipal;

    /// <summary>Records that the addon host committed its plan, so disposal leaves it alone.</summary>
    public void MarkAddonCommitted() => m_addonSettled = true;
    /// <summary>Records that the machine host committed its plan, so disposal leaves it alone.</summary>
    public void MarkMachineCommitted() => m_machineSettled = true;
    /// <inheritdoc/>
    public void Dispose() {
        if (!m_addonSettled) {
            m_addonSettled = true;
            AddonPlan?.Dispose();
        }
        if (!m_machineSettled) {
            m_machineSettled = true;
            MachinePlan?.Dispose();
        }
    }
}
