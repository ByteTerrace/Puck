using System.Text.Json.Nodes;

using Puck.Cli.Schema;
using Puck.World;

namespace Puck.Cli.Official;

// Builds the worldSchemaBundle exactly the way `puck schema --bundle` does (WorldSchema.Export then
// WorldSchema.Bundle, never by shelling out), and reads the same x-puck identity block that call stamps into it
// back out — so official build's build.commit/build.generator/build.worldSchema agree with what `puck schema`
// itself reports, with no second hash-resolution path to drift from it.
internal static class OfficialSchemaBundle {
    public static (JsonObject Bundle, string Commit, string Generator, string WorldSchemaId) Build(string repositoryRoot) {
        var extensions = SchemaCommand.LoadPostRenderExtensions(repositoryRoot: repositoryRoot);
        var split = WorldSchema.Export(postRenderExtensions: extensions);
        var bundle = WorldSchema.Bundle(split: split);
        var identity = (bundle["x-puck"] as JsonObject)!;
        var commit = (identity["commit"]?.GetValue<string>() ?? "unknown");
        var generator = (identity["generator"]?.GetValue<string>() ?? WorldSchema.SchemaId);
        var worldSchemaId = (identity["schemaVersion"]?.GetValue<string>() ?? WorldSchema.SchemaId);

        return (bundle, commit, generator, worldSchemaId);
    }
}
