using Puck.World.Protocol;

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

    /// <summary>Reads the last presented shadow selection of the queried authority: the boot world's when the client's
    /// delivered version names the authority's activation, else the routed world's whose delivered version does. A
    /// client's definition is its own object, never the server's, so the authority is matched by version value.</summary>
    /// <param name="authority">The queried authority's document version (<c>WorldServer.DocumentVersion</c>).</param>
    /// <returns>The slot report, or <see langword="null"/> when this presenter has presented no frame of that
    /// authority.</returns>
    public string? DescribeShadowSlots(WorldDocumentVersion authority) {
        if (m_client.DocumentVersion.Activation == authority.Activation) { return m_environment.DescribeShadowSlots(); }
        foreach (var scene in m_routedScenes.Values) {
            if (scene.DescribeShadowSlots(authority: authority) is { } report) { return report; }
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
