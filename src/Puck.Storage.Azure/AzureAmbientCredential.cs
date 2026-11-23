using Azure.Core;
using Azure.Identity;

namespace Puck.Storage.Azure;

/// <summary>
/// The ambient Azure credential Puck's Azure provider paths sign in with, without an app registration: a person's or
/// a runner's own sign-in (environment, workload identity, Visual Studio, the Azure CLI, Azure PowerShell, the Azure
/// Developer CLI) first, then the machine's managed identity, which the Azure SDK selects as it does for
/// <see cref="DefaultAzureCredential"/> (user-assigned when the host names one).
/// <para>The managed identity comes last because asking a machine without one can fail outright rather than report
/// itself unavailable, which ends a chain before any later credential is asked, so a person's own sign-in on such a
/// machine would never be reached. A hosted server has no person's sign-in: every earlier credential reports itself
/// unavailable, which lets the chain go on, and its managed identity answers.</para>
/// </summary>
public static class AzureAmbientCredential {
    /// <summary>Creates the ambient credential. Each instance caches the tokens it acquires, so a caller that wants a
    /// fresh answer creates a fresh one.</summary>
    /// <returns>The credential.</returns>
    public static TokenCredential Create() => new ChainedTokenCredential(
        new DefaultAzureCredential(options: new DefaultAzureCredentialOptions { ExcludeManagedIdentityCredential = true }),
        new DefaultAzureCredential(options: new DefaultAzureCredentialOptions {
            ExcludeAzureCliCredential = true,
            ExcludeAzureDeveloperCliCredential = true,
            ExcludeAzurePowerShellCredential = true,
            ExcludeEnvironmentCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeVisualStudioCredential = true,
            ExcludeWorkloadIdentityCredential = true,
        })
    );
}
