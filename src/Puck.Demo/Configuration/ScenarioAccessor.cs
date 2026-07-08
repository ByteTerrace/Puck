using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Puck.Demo.Configuration;

/// <summary>
/// The overworld render node's one window onto the <c>--scenario</c> review harness's creation. It resolves the bound
/// <see cref="ScenarioOptions"/> from the container and exposes the active scenario's creation as a primitive string.
/// It is an ACCESSOR, not a settings/config-bound POCO — it owns no state of its own and binds from no configuration
/// section (that is <see cref="ScenarioOptions"/>; see <see cref="DemoConfiguration"/>'s doc comment for the naming
/// rule). The node routes its scenario-creation read through here so it names ONE type (this class) instead of
/// <c>IOptions</c> + <see cref="ScenarioOptions"/> — keeping the node at its class-coupling ceiling while the config
/// plumbing lives here. (The demo's whole <c>PUCK_*</c> / <c>Demo:*</c> option surface was removed in the unification
/// arc; every former reader is now a console verb or a run-document field, leaving only this scenario-harness read.)
/// </summary>
internal static class ScenarioAccessor {
    /// <summary>The active scenario's creation to load into creator mode (name or path), or null when no
    /// <c>--scenario</c> is active. Read at boot by <c>OverworldRenderNode.ApplyCreatorStartupHooks</c> so the review
    /// turntable frames the loaded workpiece; the live in-session path to the same state is the <c>creator.load</c>
    /// console verb.</summary>
    public static string? ScenarioCreation(IServiceProvider services) {
        var scenario = (services.GetService<IOptions<ScenarioOptions>>()?.Value ?? new ScenarioOptions());

        return ((scenario.Active && !string.IsNullOrEmpty(value: scenario.Creation)) ? scenario.Creation : null);
    }
}
