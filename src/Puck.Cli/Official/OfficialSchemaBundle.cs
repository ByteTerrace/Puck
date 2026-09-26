using System.Text.Json.Nodes;

using Puck.Cli.Schema;
using Puck.World;

namespace Puck.Cli.Official;

// Builds the worldSchemaBundle exactly the way `puck schema --bundle` does (WorldSchema.Export then
// WorldSchema.Bundle, never by shelling out), and reads the same x-puck identity block that call stamps into it
// back out — so official build's build.generator/build.worldSchema agree with what `puck schema` itself reports, with
// no second resolution path to drift from it. The bundle's own x-puck.commit names the build of the generator and
// stays in the bundle; build.commit names the worlds tree instead (OfficialBuildCommand.TryReadTree).
internal static class OfficialSchemaBundle {
    public static (JsonObject Bundle, string Generator, string WorldSchemaId) Build(string repositoryRoot) {
        var split = WorldSchema.Export(postProcessPackages: SchemaCommand.PostProcessPackages());
        var bundle = WorldSchema.Bundle(split: split);
        var identity = (bundle["x-puck"] as JsonObject)!;
        var generator = (identity["generator"]?.GetValue<string>() ?? WorldSchema.SchemaId);
        var worldSchemaId = (identity["schemaVersion"]?.GetValue<string>() ?? WorldSchema.SchemaId);

        return (bundle, generator, worldSchemaId);
    }
}
