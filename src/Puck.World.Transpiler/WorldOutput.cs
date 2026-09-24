using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler;

/// <summary>One independently loadable document emitted by a source.</summary>
/// <param name="Name">The declared world name, or the source stem for a single document.</param>
/// <param name="Json">The canonical world definition.</param>
/// <param name="SourceMap">This world's source origins.</param>
/// <param name="TestWorlds">The tests declared by this world.</param>
/// <param name="Entry">Whether the world is its composition's declared entry (<c>entry world</c>), the one a boot of the
/// composition source starts in.</param>
public sealed record WorldOutput(string Name, JsonObject Json, SourceMap SourceMap, IReadOnlyList<WorldTestWorld> TestWorlds, bool Entry = false);
