namespace Puck.World.Client;

public sealed partial class WorldEnvironmentResolve {
    private WorldValueDomainGroup[] m_lightDomains = [];
    private WorldValueDomainGroup[] m_skyDomains = [];
    private WorldValueDomainGroup[] m_stopDomains = [];
    private WorldValueDomainGroup[] m_softboxDomains = [];

    private WorldValueDomainGroup? m_curvatureDomain;

    /// <summary>Gets this view's presentation validity transitions, active diagnostics and changed-input counts.</summary>
    public WorldValueDomainGuard Domains { get; } = new();

    /// <summary>Creates an environment resolver whose validity transitions use the presentation diagnostic stream.</summary>
    public WorldEnvironmentResolve() => Domains.Transition += WorldPresentationDiagnostic.Report;

    private void PrepareDomains(WorldDefinition definition) {
        Domains.Reset();
        static string Row(string collection, string? name, int index) => ((name is { Length: > 0 })
            ? $"{collection}[{name}]" : $"{collection}[{index}]");
        m_lightDomains = (definition.Render.Lighting?.Lights?.Select(selector: (row, index) => new WorldValueDomainGroup(
            Domains, definition, Row("render.lighting.lights", row.Name, index))).ToArray() ?? []);
        m_skyDomains = (definition.Render.Sky?.Layers?.Select(selector: (row, index) => new WorldValueDomainGroup(
            Domains, definition, Row("render.sky.layers", row.Name, index))).ToArray() ?? []);
        m_stopDomains = [];
        if (definition.Render.Sky?.Layers is { } layers) {
            for (var index = 0; (index < layers.Count); index++) {
                if (layers[index] is WorldRenderSkyLayer.Gradient { Stops: { } stops } gradient) {
                    var path = (Row("render.sky.layers", gradient.Name, index) + ".stops");

                    m_stopDomains = stops.Select(selector: (row, stop) => new WorldValueDomainGroup(
                        Domains, definition, Row(path, row.Name, stop))).ToArray();
                }
            }
        }
        m_softboxDomains = (definition.Render.Environment?.Softboxes?.Select(selector: (row, index) => new WorldValueDomainGroup(
            Domains, definition, Row("render.environment.softboxes", row.Name, index))).ToArray() ?? []);
        m_curvatureDomain = new(Domains, definition, "render.lighting.curvature");
    }
}
