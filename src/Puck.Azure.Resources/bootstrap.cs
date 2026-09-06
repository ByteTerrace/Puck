#!/usr/bin/env dotnet

/* PowerShell
    dotnet ./bootstrap.cs `
        --application-name '<application-name>' `
        --location '<location>' `
        --managed-identity-name '<managed-identity-name>' `
        --organization-name '<organization-name>' `
        --project-name '<project-name>' `
        --resource-group-name '<resource-group-name>' `
        --service-connection-name '<service-connection-name>' `
        --subscription-id '<subscription-id>' `
        --template-spec-version '<template-spec-version>' `
        --tenant-id '<tenant-id>';
*/

#:sdk Microsoft.NET.Sdk

#:package Azure.Deployments.Expression@1.641.0
#:package Azure.Identity@1.20.0
#:package Azure.ResourceManager.Authorization@1.1.6
#:package Azure.ResourceManager.ManagedServiceIdentities@1.4.0
#:package Azure.ResourceManager.Resources@1.9.0
#:package Microsoft.Graph@5.103.0
#:package Microsoft.TeamFoundationServer.Client@20.256.2
#:package Microsoft.VisualStudio.Services.Client@20.256.2
#:package Microsoft.VisualStudio.Services.ServiceEndpoints.WebApi@20.256.2
#:package System.CommandLine@2.0.5
#:package System.Configuration.ConfigurationManager@10.0.5
#:package System.Drawing.Common@10.0.5
#:package System.Security.Cryptography.Xml@10.0.5

using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Authorization.Models;
using Azure.ResourceManager.ManagedServiceIdentities;
using Azure.ResourceManager.Resources;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.Operations;
using Microsoft.VisualStudio.Services.ServiceEndpoints.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using System.CommandLine;
using System.Security.Cryptography;
using System.Text;

using Operation = Microsoft.VisualStudio.Services.Operations.Operation;

const string AzureDevOpsAppId = "499b84ac-1321-427f-aa17-267ca6975798";
const string DevOpsInfrastructureAppId = "31687f79-5e43-4c1e-8c63-d9f4bff5cf8b";
const string GraphServiceAppId = "00000003-0000-0000-c000-000000000000";

AppContext.SetSwitch(
    isEnabled: true,
    switchName: "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault"
);

var applicationNameOption = new Option<string>(name: "--application-name") { Required = true };
var locationOption = new Option<string>(name: "--location") { Required = true };
var managedIdentityNameOption = new Option<string>(name: "--managed-identity-name") { Required = true };
var organizationNameOption = new Option<string>(name: "--organization-name") { Required = true };
var projectNameOption = new Option<string>(name: "--project-name") { Required = true };
var resourceGroupNameOption = new Option<string>(name: "--resource-group-name") { Required = true };
var serviceConnectionNameOption = new Option<string>(name: "--service-connection-name") { Required = true };
var subscriptionIdOption = new Option<string>(name: "--subscription-id") { Required = true };
var templateSpecVersionOption = new Option<string>(name: "--template-spec-version") { Required = true };
var tenantIdOption = new Option<string>(name: "--tenant-id") { Required = true };

var rootCommand = new RootCommand()
{
    Options = {
        applicationNameOption,
        locationOption,
        managedIdentityNameOption,
        organizationNameOption,
        projectNameOption,
        resourceGroupNameOption,
        serviceConnectionNameOption,
        subscriptionIdOption,
        templateSpecVersionOption,
        tenantIdOption,
    },
};
var parseResult = rootCommand.Parse(args: args);

if (0 < parseResult.Errors.Count)
{
    foreach (var parseError in parseResult.Errors)
    {
        Console.Error.WriteLine(value: parseError.Message);
    }

    throw new ArgumentException(message: "Failed to parse command line arguments.");
}

using var cancellationTokenSource = new CancellationTokenSource();

var allowedRoleDefinitionNames = new[] {
    "App Configuration Data Owner",
    "App Configuration Data Reader",
    "Container Registry Repository Catalog Lister",
    "Container Registry Repository Reader",
    "Container Registry Repository Writer",
    "Key Vault Administrator",
    "Key Vault Crypto Service Encryption User",
    "Key Vault Secrets User",
    "Managed Identity Operator",
    "Monitoring Metrics Publisher",
    "Network Contributor",
    "Reader",
    "Storage Blob Data Owner",
    "Storage Blob Data Reader",
    "Storage Queue Data Contributor",
    "Storage Table Data Contributor",
};
var applicationName = parseResult.GetRequiredValue(option: applicationNameOption);
var cancellationToken = cancellationTokenSource.Token;
var customRoleDefinitions = new[] {
    new AuthorizationRoleDefinitionData () {
        Description = $"Allows an API host to access {applicationName} resources.",
        Permissions = {
            new()  {
                Actions = {
                    "Microsoft.Storage/storageAccounts/blobServices/containers/read",
                    "Microsoft.Storage/storageAccounts/blobServices/containers/write",
                    "Microsoft.Storage/storageAccounts/queueServices/queues/read",
                    "Microsoft.Storage/storageAccounts/queueServices/queues/write",
                },
                DataActions = {},
                NotActions = {},
                NotDataActions = {},
            },
        },
        RoleName = $"{applicationName} API Host",
        RoleType = "CustomRole",
    },
    new AuthorizationRoleDefinitionData () {
        Description = $"Allows user access to {applicationName} storage resources.",
        Permissions = {
            new()  {
                Actions = {
                    "Microsoft.Storage/storageAccounts/blobServices/generateUserDelegationKey/action",
                    "Microsoft.Storage/storageAccounts/fileServices/generateUserDelegationKey/action",
                    "Microsoft.Storage/storageAccounts/queueServices/generateUserDelegationKey/action",
                },
                DataActions = {
                    "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/add/action",
                    "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete",
                    "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/move/action",
                    "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read",
                    "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/runAsSuperUser/action",
                    "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags/read",
                    "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags/write",
                    "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write",
                    "Microsoft.Storage/storageAccounts/queueServices/queues/messages/delete",
                    "Microsoft.Storage/storageAccounts/queueServices/queues/messages/process/action",
                    "Microsoft.Storage/storageAccounts/queueServices/queues/messages/read",
                    "Microsoft.Storage/storageAccounts/queueServices/queues/messages/write",
                },
                NotActions = {},
                NotDataActions = {},
            },
        },
        RoleName = $"{applicationName} Storage User",
        RoleType = "CustomRole",
    },
};
var managedIdentityApplicationRoleMap = new Dictionary<string, IList<string>>()
{
    [GraphServiceAppId] = [
        "Application.ReadWrite.OwnedBy",
        "GroupMember.Read.All",
    ],
};
var location = parseResult.GetRequiredValue(option: locationOption);
var managedIdentityName = parseResult.GetRequiredValue(option: managedIdentityNameOption);
var organizationName = parseResult.GetRequiredValue(option: organizationNameOption);
var projectName = parseResult.GetRequiredValue(option: projectNameOption);
var resourceGroupName = parseResult.GetRequiredValue(option: resourceGroupNameOption);
var serviceConnectionName = parseResult.GetRequiredValue(option: serviceConnectionNameOption);
var subscriptionId = parseResult.GetRequiredValue(option: subscriptionIdOption);
var subscriptionProviderNamespaces = new[] {
    "GitHub.Network",
    "Microsoft.AlertsManagement",
    "Microsoft.App",
    "Microsoft.AppConfiguration",
    "Microsoft.Cache",
    "Microsoft.Compute",
    "Microsoft.ContainerInstance",
    "Microsoft.ContainerRegistry",
    "Microsoft.ContainerService",
    "Microsoft.Cdn",
    "Microsoft.DBforPostgreSQL",
    "Microsoft.DevCenter",
    "Microsoft.DevOpsInfrastructure",
    "Microsoft.Diagnostics",
    "Microsoft.KeyVault",
    "Microsoft.ManagedIdentity",
    "Microsoft.Network",
    "Microsoft.OperationalInsights",
    "Microsoft.Storage",
    "Microsoft.Web",
};
var templateSpecifications = new[] {
    "avm-temp/ptn/dev-ops/cicd-agents-and-runners/main.json",
    "avm-temp/ptn/network/basic-topology/main.json",
    "avm-temp/ptn/network/private-dns-zones/main.json",
    "avm-temp/ptn/network/public-dns-zones/main.json"
};
var templateSpecVersion = parseResult.GetRequiredValue(option: templateSpecVersionOption);
var tenantId = parseResult.GetRequiredValue(option: tenantIdOption).ToLowerInvariant();
var tokenCredential = new DefaultAzureCredential(options: new DefaultAzureCredentialOptions
{
    ExcludeInteractiveBrowserCredential = true,
    ExcludeManagedIdentityCredential = true,
});

var azureDevOpsClient = new VssConnection(
    baseUrl: new Uri(uriString: $"https://dev.azure.com/{organizationName}"),
    credentials: new VssOAuthAccessTokenCredential(accessToken: (await tokenCredential.GetTokenAsync(requestContext: new TokenRequestContext(scopes: [$"{AzureDevOpsAppId}/.default"]))).Token)
);
var graphClient = new GraphServiceClient(tokenCredential: tokenCredential);
var operationsClient = await azureDevOpsClient.GetClientAsync<OperationsHttpClient>(cancellationToken: cancellationToken);
var processClient = await azureDevOpsClient.GetClientAsync<ProcessHttpClient>(cancellationToken: cancellationToken);
var projectClient = await azureDevOpsClient.GetClientAsync<ProjectHttpClient>(cancellationToken: cancellationToken);
var resourceManagerClient = new ArmClient(credential: tokenCredential);
var serviceEndpointClient = await azureDevOpsClient.GetClientAsync<ServiceEndpointHttpClient>(cancellationToken: cancellationToken);

Console.WriteLine(value: "Getting service principals...");

var devOpsInfrastructureServicePrincipal = (await graphClient
    .ServicePrincipalsWithAppId(appId: DevOpsInfrastructureAppId)
    .GetAsync(cancellationToken: cancellationToken))!;

Console.WriteLine(value: "Getting subscription details...");

var subscription = (await resourceManagerClient
    .GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscriptionId: subscriptionId))
    .GetAsync())
    .Value;

foreach (var providerNamespace in subscriptionProviderNamespaces)
{
    await (await subscription.GetResourceProviderAsync(resourceProviderNamespace: providerNamespace))
        .Value
        .RegisterAsync(cancellationToken: cancellationToken);
}

Console.WriteLine(value: "Ensuring that resource group exists...");

var resourceGroup = (await subscription
    .GetResourceGroups()
    .CreateOrUpdateAsync(
        cancellationToken: cancellationToken,
        data: new ResourceGroupData(location: new AzureLocation(location: location)),
        resourceGroupName: resourceGroupName,
        waitUntil: WaitUntil.Completed
    ))
    .Value;

Console.WriteLine(value: "Ensuring that managed identity exists...");

var managedIdentity = (await resourceGroup
    .GetUserAssignedIdentities()
    .CreateOrUpdateAsync(
        cancellationToken: cancellationToken,
        data: new UserAssignedIdentityData(location: resourceGroup.Data.Location),
        resourceName: managedIdentityName,
        waitUntil: WaitUntil.Completed
    ))
    .Value;

Console.WriteLine(value: "Ensuring that groups exist...");

var groupBatchRequests = new BatchRequestContentCollection(baseClient: graphClient);
var groupNameSuffixMap = new Dictionary<string, EntraGroupData>
{
    ["API Users"] = new()
    {
        Description = $"Delegates access to {applicationName} API resources.",
    },
};

foreach (var (groupNameSuffix, groupData) in groupNameSuffixMap)
{
    var groupDisplayName = $"{applicationName} {groupNameSuffix}";
    var groupUniqueName = AzureResourceManagerFunctions
        .Guid(values: [
            tenantId,
            groupDisplayName,
        ])
        .ToString();
    var stepId0 = await groupBatchRequests.AddBatchRequestStepAsync(
        requestInformation: graphClient
            .GroupsWithUniqueName(uniqueName: groupUniqueName)
            .ToPatchRequestInformation(
                body: new()
                {
                    AdditionalData = {
                        ["owners@odata.bind"] = new List<string> {
                            $"https://graph.microsoft.com/v1.0/directoryObjects/{managedIdentity.Data.PrincipalId}",
                        },
                    },
                    Description = groupData.Description,
                    DisplayName = groupDisplayName,
                    MailEnabled = false,
                    MailNickname = groupDisplayName
                        .Replace(
                            newChar: '-',
                            oldChar: ' '
                        )
                        .ToLowerInvariant(),
                    SecurityEnabled = true,
                    UniqueName = groupUniqueName,
                },
                requestConfiguration: requestConfiguration =>
                {
                    requestConfiguration.Headers.Add(
                        headerName: "Prefer",
                        headerValues: "create-if-missing"
                    );
                }
            )
    );
    var stepId1 = await groupBatchRequests.AddBatchRequestStepAsync(
        requestInformation: graphClient
            .GroupsWithUniqueName(uniqueName: groupUniqueName)
            .ToGetRequestInformation(
                requestConfiguration: requestConfiguration =>
                {
                    requestConfiguration.QueryParameters.Select = ["id"];
                }
            )
    );

    groupBatchRequests.BatchRequestSteps[key: stepId1].DependsOn = [stepId0];
    groupNameSuffixMap[key: groupNameSuffix].Id = stepId1;
}

var groupBatchResponse = await graphClient
    .Batch
    .PostAsync(
        batchRequestContentCollection: groupBatchRequests,
        cancellationToken: cancellationToken
    );

foreach (var responseStatus in await groupBatchResponse.GetResponsesStatusCodesAsync())
{
    if (((int)responseStatus.Value) is < 200 or > 299)
    {
        throw new HttpRequestException(
            httpRequestError: HttpRequestError.Unknown,
            message: await (await groupBatchResponse
                .GetResponseByIdAsync(requestId: responseStatus.Key))
                .Content
                .ReadAsStringAsync(cancellationToken: cancellationToken),
            statusCode: responseStatus.Value
        );
    }
}

foreach (var (groupNameSuffix, groupData) in groupNameSuffixMap)
{
    groupNameSuffixMap[key: groupNameSuffix].Id = (await (await groupBatchResponse
        .GetResponseByIdAsync(requestId: groupData.Id))
        .Content
        .ReadAsAsync<Group>(cancellationToken: cancellationToken))
        .Id!;
}

Console.WriteLine(value: "Ensuring that custom roles exist...");

foreach (var roleDefinition in customRoleDefinitions)
{
    roleDefinition.AssignableScopes.Add(item: resourceGroup.Id);

    _ = await resourceManagerClient
       .GetAuthorizationRoleDefinitions(scope: subscription.Id)
       .CreateOrUpdateAsync(
           cancellationToken: cancellationToken,
           data: roleDefinition,
           roleDefinitionId: new ResourceIdentifier(
               resourceId: AzureResourceManagerFunctions
                   .Guid(values: roleDefinition.RoleName)
                   .ToString()
           ),
           waitUntil: WaitUntil.Completed
       );
}

await Task.Delay(
    cancellationToken: cancellationToken,
    millisecondsDelay: 17000
);

Console.WriteLine(value: "Ensuring that resource group permissions exist...");

var roleDefinitionsByName = await resourceManagerClient
    .GetAuthorizationRoleDefinitions(scope: resourceGroup.Id)
    .GetAllAsync(cancellationToken: cancellationToken)
    .ToDictionaryAsync(
        cancellationToken: cancellationToken,
        elementSelector: roleDefinition => roleDefinition,
        keySelector: roleDefinition => roleDefinition.Data.RoleName
    );
var userAccessAdministratorAllowedPrincipalIds = groupNameSuffixMap
    .Values
    .Select(selector: groupData => groupData.Id)
    .Concat(second: [
        devOpsInfrastructureServicePrincipal.Id!,
        managedIdentity.Data.PrincipalId!.Value.ToString()!,
    ])
    .ToArray();
var userAccessAdministratorAllowedRoleDefinitionIds = allowedRoleDefinitionNames
    .Concat(second: customRoleDefinitions.Select(selector: roleDefinition => roleDefinition.RoleName))
    .OrderBy(keySelector: roleDefinitionName => roleDefinitionName)
    .Select(selector: roleDefinitionName => roleDefinitionsByName[key: roleDefinitionName].Id.Name)
    .ToArray();

foreach (var roleDefinitionName in new[] { "Owner", })
{
    var isConstrainedRole = roleDefinitionName.Equals(
        comparisonType: StringComparison.InvariantCultureIgnoreCase,
        value: "Owner"
    );
    var roleDefinition = roleDefinitionsByName[key: roleDefinitionName];

    await resourceManagerClient
        .GetRoleAssignments(scope: resourceGroup.Id)
        .CreateOrUpdateAsync(
            cancellationToken: cancellationToken,
            content: new(
                principalId: managedIdentity.Data.PrincipalId!.Value,
                roleDefinitionId: roleDefinition.Id
            )
            {
                Condition = (isConstrainedRole ? $$"""
                (
                    (!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'}))
                    OR 
                    (
                        @Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {{{string.Join(", ", userAccessAdministratorAllowedRoleDefinitionIds)}}}
                        AND
                        @Request[Microsoft.Authorization/roleAssignments:PrincipalId] ForAnyOfAnyValues:GuidEquals {{{string.Join(", ", userAccessAdministratorAllowedPrincipalIds)}}}
                    )
                )
                AND
                (
                    (!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'}))
                    OR 
                    (
                        @Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {{{string.Join(", ", userAccessAdministratorAllowedRoleDefinitionIds)}}}
                        AND
                        @Resource[Microsoft.Authorization/roleAssignments:PrincipalId] ForAnyOfAnyValues:GuidEquals {{{string.Join(", ", userAccessAdministratorAllowedPrincipalIds)}}}
                    )
                )
                """ : null),
                ConditionVersion = (isConstrainedRole ? "2.0" : null),
                PrincipalType = RoleManagementPrincipalType.ServicePrincipal,
            },
            roleAssignmentName: AzureResourceManagerFunctions.Guid(values: [
                resourceGroup.Id.ToString()!,
                managedIdentity.Data.PrincipalId.ToString()!,
                roleDefinition.Id.ToString()
            ]).ToString(),
            waitUntil: WaitUntil.Completed
        );
}

Console.WriteLine(value: "Ensuring that project exists...");

var process = (await processClient
    .GetProcessesAsync(cancellationToken: cancellationToken))
    .Single(predicate: process => process.Name.Equals(
        comparisonType: StringComparison.InvariantCultureIgnoreCase,
        value: "Agile"
    ));
var project = new TeamProject()
{
    Capabilities = new()
    {
        [TeamProjectCapabilitiesConstants.ProcessTemplateCapabilityName] = new()
        {
            [TeamProjectCapabilitiesConstants.ProcessTemplateCapabilityTemplateTypeIdAttributeName] = process.Id.ToString(),
        },
        [TeamProjectCapabilitiesConstants.VersionControlCapabilityName] = new()
        {
            [TeamProjectCapabilitiesConstants.VersionControlCapabilityAttributeName] = SourceControlTypes.Git.ToString(),
        },
    },
    Description = "TODO: Write one...",
    Name = projectName,
    Visibility = ProjectVisibility.Unchanged,
};

Guid? projectId;
Guid projectOperationId;

try
{
    projectId = default;
    projectOperationId = (await projectClient
        .QueueCreateProject(projectToCreate: project))
        .Id;
}
catch (ProjectAlreadyExistsException)
{
    project.Capabilities = null;
    project.Name = null;
    projectId = (await projectClient
        .GetProject(id: projectName))
        .Id;
    projectOperationId = (await projectClient
        .UpdateProject(
            projectToUpdateId: projectId.Value,
            projectUpdate: project
        ))
        .Id;
}

await AzureDevOpsFunctions.WaitForOperationAsync(
    cancellationToken: cancellationToken,
    operationsClient: operationsClient,
    operationId: projectOperationId
);

if (!projectId.HasValue)
{
    projectId = (await projectClient
        .GetProject(id: projectName))
        .Id;
}

Console.WriteLine(value: "Ensuring that service connection exists...");

ServiceEndpoint serviceEndpoint;

try
{
    var description = $"Manages the Azure resource group {resourceGroup.Data.Name} in the subscription {subscription.Data.DisplayName}.";

    serviceEndpoint = await serviceEndpointClient.CreateServiceEndpointAsync(
        cancellationToken: cancellationToken,
        endpoint: new()
        {
            Authorization = new()
            {
                Parameters = {
                    ["scope"] = resourceGroup.Id!,
                    ["serviceprincipalid"] = managedIdentity.Data.ClientId.ToString()!,
                    ["tenantId"] = tenantId,
                    ["workloadIdentityFederationIssuerType"] = "EntraID",
                },
                Scheme = "WorkloadIdentityFederation",
            },
            Data = {
                ["creationMode"] = "Manual",
                ["environment"] = "AzureCloud",
                ["identityType"] = "ManagedIdentity",
                ["resourceGroupName"] = resourceGroup.Data.Name,
                ["scopeLevel"] = "ResourceGroup",
                ["subscriptionName"] = subscription.Data.DisplayName,
                ["subscriptionId"] = subscription.Data.SubscriptionId,
            },
            Description = description,
            IsShared = false,
            Name = serviceConnectionName,
            Owner = "library",
            ServiceEndpointProjectReferences = [
                new() {
                    Description = description,
                    Name = serviceConnectionName,
                    ProjectReference = new () {
                        Id = projectId.Value,
                    },
                },
            ],
            Type = "AzureRM",
            Url = new(uriString: "https://management.azure.com/"),
        }
    );
}
catch (DuplicateServiceConnectionException)
{
    serviceEndpoint = (await serviceEndpointClient
        .GetServiceEndpointsByNamesAsync(
            cancellationToken: cancellationToken,
            endpointNames: [serviceConnectionName],
            project: projectId.Value.ToString()
        ))
        .Single();
}

if (!"AzureRM".Equals(
    comparisonType: StringComparison.InvariantCultureIgnoreCase,
    value: serviceEndpoint.Type
))
{
    throw new InvalidOperationException(message: "Service connection with the specified name already exists, but it is not of the required type: AzureRM.");
}

if (!"WorkloadIdentityFederation".Equals(
    comparisonType: StringComparison.InvariantCultureIgnoreCase,
    value: serviceEndpoint.Authorization.Scheme
))
{
    throw new InvalidOperationException(message: "Service connection with the specified name already exists, but does not use the required authentication scheme: WorkloadIdentityFederation.");
}

if (!(serviceEndpoint.Data.TryGetValue(
    key: "scopeLevel",
    value: out var serviceEndpointScopeLevel
) && serviceEndpointScopeLevel.Equals(
    comparisonType: StringComparison.InvariantCultureIgnoreCase,
    value: "ResourceGroup"
)))
{
    throw new InvalidOperationException(message: $"Service connection with the specified name already exists, but does not reference the required scope level: ResourceGroup.");
}

if (!(serviceEndpoint.Data.TryGetValue(
    key: "subscriptionId",
    value: out var serviceEndpointSubscriptionId
) && subscriptionId.Equals(
    comparisonType: StringComparison.InvariantCultureIgnoreCase,
    value: serviceEndpointSubscriptionId
)))
{
    throw new InvalidOperationException(message: $"Service connection with the specified name already exists, but does not reference the required subscription id: {subscriptionId}.");
}

if (!(serviceEndpoint.Data.TryGetValue(
    key: "resourceGroupName",
    value: out var serviceEndpointResourceGroupName
) && resourceGroup.Data.Name.Equals(
    comparisonType: StringComparison.InvariantCultureIgnoreCase,
    value: serviceEndpointResourceGroupName
)))
{
    throw new InvalidOperationException(message: $"Service connection with the specified name already exists, but does not reference the required resource group name: {resourceGroup.Data.Name}.");
}

if (!(serviceEndpoint.Authorization.Parameters.TryGetValue(
    key: "tenantId",
    value: out var serviceEndpointTenantId
) && tenantId.Equals(
    comparisonType: StringComparison.InvariantCultureIgnoreCase,
    value: serviceEndpointTenantId
)))
{
    throw new InvalidOperationException(message: $"Service connection with the specified name already exists, but does not reference the required tenant id: {tenantId}.");
}

if (!(serviceEndpoint.Authorization.Parameters.TryGetValue(
    key: "serviceprincipalid",
    value: out var serviceEndpointServicePrincipalId
) && managedIdentity.Data.ClientId.ToString()!.Equals(
    comparisonType: StringComparison.InvariantCultureIgnoreCase,
    value: serviceEndpointServicePrincipalId
)))
{
    throw new InvalidOperationException(message: $"Service connection with the specified name already exists, but does not reference the required client id: {managedIdentity.Data.ClientId}.");
}

if (!serviceEndpoint.Authorization.Parameters.TryGetValue(
    key: "workloadIdentityFederationIssuer",
    value: out var serviceEndpointWorkloadIdentityFederationIssuer
) || string.IsNullOrWhiteSpace(value: serviceEndpointWorkloadIdentityFederationIssuer))
{
    throw new InvalidOperationException(message: $"Service connection with the specified name already exists, but does not reference a valid workload identity federation issuer.");
}

if (!serviceEndpoint.Authorization.Parameters.TryGetValue(
    key: "workloadIdentityFederationSubject",
    value: out var serviceEndpointWorkloadIdentityFederationSubject
) || string.IsNullOrWhiteSpace(value: serviceEndpointWorkloadIdentityFederationSubject))
{
    throw new InvalidOperationException(message: $"Service connection with the specified name already exists, but does not reference a valid workload identity federation subject.");
}

Console.WriteLine(value: "Ensuring that federated credential exists...");

await managedIdentity
    .GetFederatedIdentityCredentials()
    .CreateOrUpdateAsync(
        cancellationToken: cancellationToken,
        data: new()
        {
            Audiences = { "api://AzureADTokenExchange" },
            IssuerUri = new(uriString: serviceEndpointWorkloadIdentityFederationIssuer),
            Subject = serviceEndpointWorkloadIdentityFederationSubject,
        },
        federatedIdentityCredentialResourceName: serviceEndpoint.Id.ToString(),
        waitUntil: WaitUntil.Completed
    );

Console.WriteLine(value: "Ensuring that application permissions exist...");

foreach (var (applicationId, roleNames) in managedIdentityApplicationRoleMap)
{
    var servicePrincipal = (await graphClient
        .ServicePrincipalsWithAppId(appId: applicationId)
        .GetAsync(cancellationToken: cancellationToken))!;

    foreach (var roleName in roleNames)
    {
        try
        {
            await graphClient
                .ServicePrincipals[managedIdentity.Data.PrincipalId!.Value.ToString()]
                .AppRoleAssignments
                .PostAsync(
                    body: new()
                    {
                        AppRoleId = servicePrincipal
                            .AppRoles!
                            .Single(predicate: appRole => appRole.Value!.Equals(
                                comparisonType: StringComparison.InvariantCultureIgnoreCase,
                                value: roleName
                            ))
                            .Id,
                        PrincipalId = managedIdentity.Data.PrincipalId!.Value,
                        ResourceId = Guid.Parse(input: servicePrincipal.Id!),
                    },
                    cancellationToken: cancellationToken
                );
        }
        catch (ODataError e) when (
            (e.ResponseStatusCode is 400) &&
            e.Message.Contains(value: "already exists")
        )
        { }
    }
}

Console.WriteLine(value: "Ensuring that template specifications exist...");

foreach (var specification in templateSpecifications)
{
    var nameStartIndex = specification.IndexOf(value: '/');
    var nameStopIndex = specification.LastIndexOf(value: '/');

    await (await resourceGroup
        .GetTemplateSpecs()
        .CreateOrUpdateAsync(
            cancellationToken: cancellationToken,
            data: new TemplateSpecData(location: resourceGroup.Data.Location)
            {
                Description = "TODO: Write one...",
            },
            templateSpecName: specification[(nameStartIndex + 1)..nameStopIndex].Replace(
                newChar: '_',
                oldChar: '/'
            ),
            waitUntil: WaitUntil.Completed
        ))
        .Value
        .GetTemplateSpecVersions()
        .CreateOrUpdateAsync(
            cancellationToken: cancellationToken,
            data: new TemplateSpecVersionData(location: resourceGroup.Data.Location)
            {
                Description = "TODO: Write one...",
                MainTemplate = await BinaryData.FromFileAsync(
                    cancellationToken: cancellationToken,
                    path: Path.Join(
                        Directory.GetCurrentDirectory(),
                        specification
                    )
                ),
            },
            templateSpecVersion: templateSpecVersion,
            waitUntil: WaitUntil.Completed
        );
}

static class AzureDevOpsFunctions
{
    public static async Task<Operation> WaitForOperationAsync(
        OperationsHttpClient operationsClient,
        Guid operationId,
        CancellationToken cancellationToken = default
    )
    {
        Operation operation;

        do
        {
            await Task.Delay(
                cancellationToken: cancellationToken,
                delay: TimeSpan.FromSeconds(value: 5)
            );

            operation = await operationsClient.GetOperation(
                cancellationToken: cancellationToken,
                id: operationId
            );
        } while (!operation.Completed);

        return operation;
    }
}
static class AzureResourceManagerFunctions
{
    private static ReadOnlySpan<byte> ArmNamespaceBytes => [
        0x11, 0xFB, 0x06, 0xFB, 0x71, 0x2D, 0x4D, 0xDD,
        0x98, 0xC7, 0xE7, 0x1B, 0xBD, 0x58, 0x88, 0x30,
    ];

    public static Guid Guid(string value)
    {
        var hashSpan = ((Span<byte>)stackalloc byte[20]);
        var scratchLength = (16 + Encoding.UTF8.GetByteCount(s: value));
        var scratchSpan = (
            (512 >= scratchLength)
            ? stackalloc byte[scratchLength]
            : new byte[scratchLength]
        );

        ArmNamespaceBytes.CopyTo(destination: scratchSpan);
        Encoding.UTF8.GetBytes(
            bytes: scratchSpan[16..],
            chars: value
        );
        SHA1.HashData(
            destination: hashSpan,
            source: scratchSpan
        );

        hashSpan[6] = ((byte)((hashSpan[6] & 0x0F) | (5 << 4)));
        hashSpan[8] = ((byte)((hashSpan[8] & 0x3F) | 0x80));

        return new Guid(
            b: hashSpan[..16],
            bigEndian: true
        );
    }
    public static Guid Guid(params string[] values) =>
        Guid(value: string.Join(separator: '-', values: values));
}

class EntraGroupData
{
    public string Description { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
}

