// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Imports
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
import { worldSiloConfigType, worldSiloActionGroupConfigType } from 'ts/bvm:ptn_platform_world-silo:0.0.7'

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Parameters
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
param configuration worldSiloConfigType
param actionGroup worldSiloActionGroupConfigType
param publishingPrincipalId string
param storageAccountName string
param registryName string
param keyVaultName string
@description('The Application Insights component the silo\'s azure.monitor telemetry sink publishes to.')
param applicationInsightsName string
param location string = resourceGroup().location
param tags { *: string } = {}
@description('The world group\'s hosted-data policy, as main.bicep declares it: scratch is discarded by each release deployment.')
param hostedData ('durable' | 'scratch')

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Resources
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
module identity 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  params: {
    name: configuration.userAssignedIdentity.name
    location: location
    tags: union(tags, configuration.userAssignedIdentity.?tags ?? {})
  }
}
module world 'ts/bvm:ptn_platform_world-silo:0.0.7' = {
  params: {
    configuration: configuration
    actionGroup: actionGroup
    tags: tags
    owner: identity.outputs.principalId
    publishingPrincipalId: publishingPrincipalId
    storageAccountName: storageAccountName
    registryName: registryName
  }
}
resource insights 'Microsoft.Insights/components@2020-02-02' existing = { name: applicationInsightsName }
// The telemetry sink's Monitoring Metrics Publisher grant on Application Insights is not deployed: the CI principal was
// refused that role assignment on the 036c66a885 deploy (open items: the silo telemetry grant).

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Outputs
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
output worldSiloOwner string = identity.outputs.principalId
output worldSiloIdentityResourceId string = identity.outputs.resourceId
output worldSiloClientId string = identity.outputs.clientId
output worldSiloStorageEndpoint string = world.outputs.storageEndpoint
output worldSiloTelemetryConnectionString string = insights.properties.ConnectionString
output worldSiloConfiguration worldSiloConfigType = world.outputs.deploymentConfiguration
output deploymentKeyVaultName string = keyVaultName
output deploymentLocation string = location
output deploymentTags { *: string } = tags
output worldSiloHostedData string = hostedData
output deploymentResourceGroupName string = resourceGroup().name
