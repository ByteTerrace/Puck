namespace Puck.World.Client;

public sealed partial class WorldFramePresenter {
    private readonly WorldShadowSelection m_deliveredShadows = new();

    private void ObserveShadows() {
        m_deliveredShadows.SetSettings(settings: m_settings.ShadowSlots);
        m_client.DeliveredState += AdvanceShadows;
        AdvanceShadows(m_client.Definition, m_client.DefinitionRevision, m_client.StateMirror);
    }
    private void AdvanceShadows(WorldDefinition definition, int revision, WorldStateMirror mirror) =>
        m_deliveredShadows.Advance(definition: definition, mirror: mirror, revision: revision);

    /// <summary>Reads the last presented shadow selection of the queried authority.</summary>
    /// <param name="definition">The queried definition.</param>
    /// <returns>The slot report, or null when this presenter has no matching frame.</returns>
    public string? DescribeShadowSlots(WorldDefinition definition) {
        if (m_environment.DescribeShadowSlots(definition: definition) is { } boot) { return boot; }
        foreach (var scene in m_routedScenes.Values) {
            if (scene.DescribeShadowSlots(definition: definition) is { } report) { return report; }
        }
        return null;
    }
    /// <summary>Releases subscriptions held by the boot world's and routed worlds' presentations.</summary>
    public void Dispose() {
        m_client.DeliveredState -= AdvanceShadows;
        m_environment.Dispose();
        foreach (var scene in m_routedScenes.Values) { scene.Dispose(); }
    }
}
