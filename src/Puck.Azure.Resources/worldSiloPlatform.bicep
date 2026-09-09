import { worldSiloConfigType, worldSiloActionGroupConfigType } from 'ts/bvm:ptn_platform_world-silo:0.0.5'
param configuration worldSiloConfigType
param actionGroup worldSiloActionGroupConfigType
param publishingPrincipalId string
param storageAccountName string
param registryName string
param keyVaultName string
param location string = resourceGroup().location
param tags { *: string } = {}
module identity 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  params: { name: configuration.userAssignedIdentity.name, location: location, tags: union(tags, configuration.userAssignedIdentity.?tags ?? {}) }
}
module world 'ts/bvm:ptn_platform_world-silo:0.0.5' = {
  params: { configuration: configuration, actionGroup: actionGroup, tags: tags, owner: identity.outputs.principalId, publishingPrincipalId: publishingPrincipalId, storageAccountName: storageAccountName, registryName: registryName }
}
output worldSiloOwner string = identity.outputs.principalId
output worldSiloIdentityResourceId string = identity.outputs.resourceId
output worldSiloClientId string = identity.outputs.clientId
output worldSiloStorageEndpoint string = world.outputs.storageEndpoint
output worldSiloConfiguration worldSiloConfigType = world.outputs.deploymentConfiguration
output deploymentKeyVaultName string = keyVaultName
output deploymentLocation string = location
output deploymentTags { *: string } = tags
output deploymentResourceGroupName string = resourceGroup().name
