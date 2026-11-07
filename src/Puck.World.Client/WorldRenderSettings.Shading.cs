using System.Runtime.CompilerServices;
using System.Text.Json;
using Puck.SignedDistance;

namespace Puck.World;

public sealed partial class WorldRenderSettings {
    private static readonly ConditionalWeakTable<SdfShadeDefinition, SdfShadeProgram> CompiledShades = new();
    private SdfShadeProgram? m_shadeOverride;
    private SdfProgram? m_shadeScene;
    private readonly Dictionary<string, SdfShadeProgram> m_namedShades = new(StringComparer.Ordinal);

    /// <summary>The live default override; null follows each world's document.</summary>
    public SdfShadeDefinition? ShadeOverride => m_shadeOverride?.Definition;

    /// <summary>Compiles a reloadable default once per immutable definition.</summary>
    public static SdfShadeProgram? CompileShade(SdfShadeDefinition? definition) => definition is null ? null : CompiledShades.GetValue(definition, SdfShadeProgram.Compile);

    /// <summary>Resolves the live override or a world's own default and inventories its named material programs.</summary>
    public SdfShadeProgram? ResolveShade(WorldRenderDefaults render, SdfProgram scene) {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(scene);
        if (!ReferenceEquals(m_shadeScene, scene)) {
            m_namedShades.Clear();
            foreach (var program in scene.Appearance.ShadingPrograms) { m_namedShades[program.Definition.Name] = program; }
            m_shadeScene = scene;
        }
        var authored = CompileShade(render.Shade);
        if (authored is not null) { m_namedShades[authored.Definition.Name] = authored; }
        return m_shadeOverride ?? authored;
    }

    /// <summary>Finds a named admitted program in the current scene.</summary>
    public SdfShadeProgram? NamedShade(string name) => m_namedShades.GetValueOrDefault(name);

    /// <summary>Applies an admitted lever payload; null restores the document default.</summary>
    public void SetShadeDefault(string? payload) {
        var definition = payload is null ? null : JsonSerializer.Deserialize(payload, WorldJsonContext.Default.SdfShadeDefinition);
        m_shadeOverride = CompileShade(definition);
        m_revision++;
    }
}
