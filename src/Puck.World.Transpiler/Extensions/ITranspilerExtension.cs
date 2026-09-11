using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Ast;
using Puck.World.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Extensions;

/// <summary>Contract for pluggable document-family and forge transpilation extensions.</summary>
public interface ITranspilerExtension {
    /// <summary>The canonical schema version supported by this extension (e.g. puck.cartridge.v1).</summary>
    string Schema { get; }

    /// <summary>Determines whether this extension can handle the specified document schema.</summary>
    bool CanHandle(string schema);

    /// <summary>Lowers a domain-specific custom section into the target JSON object.</summary>
    bool TryLowerSection(string sectionName, BlockNode block, JsonObject target, DiagnosticBag diagnostics);

    /// <summary>Decompiles a domain-specific JSON section into formatted Puck DSL.</summary>
    bool TryDecompileSection(string sectionName, JsonNode node, StringBuilder sb, int indentLevel);
}

/// <summary>Registry of installed transpiler extensions.</summary>
public sealed class TranspilerExtensionRegistry {
    private readonly List<ITranspilerExtension> m_extensions = [];

    /// <summary>The default shared extension registry instance.</summary>
    public static TranspilerExtensionRegistry Default { get; } = new();

    /// <summary>Registers a new transpiler extension.</summary>
    public void Register(ITranspilerExtension extension) {
        ArgumentNullException.ThrowIfNull(extension);
        m_extensions.Add(extension);
    }

    /// <summary>Finds an extension that can handle the given schema.</summary>
    public ITranspilerExtension? FindHandler(string schema) =>
        m_extensions.FirstOrDefault(ext => ext.CanHandle(schema));
}
