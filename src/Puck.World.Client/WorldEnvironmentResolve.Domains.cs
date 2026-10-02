namespace Puck.World.Client;

public sealed partial class WorldEnvironmentResolve {
    private WorldValueDomainGroup[] m_lightDomains = [];
    private WorldValueDomainGroup[] m_skyDomains = [];
    private WorldValueDomainGroup[] m_softboxDomains = [];

    private WorldValueDomainGroup? m_curvatureDomain;
    private WorldValueDomainGroup? m_skyFrameDomain;

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
        m_softboxDomains = (definition.Render.Environment?.Softboxes?.Select(selector: (row, index) => new WorldValueDomainGroup(
            Domains, definition, Row("render.environment.softboxes", row.Name, index))).ToArray() ?? []);
        m_curvatureDomain = new(Domains, definition, "render.lighting.curvature");
        m_skyFrameDomain = new(Domains, definition, "render.sky.frame");
    }
}
