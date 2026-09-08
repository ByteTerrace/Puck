// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Imports
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
import { containerRepositoryCondition } from '../../../../abacConditions.bicep'

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Types
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
type tagsType = { *: string }
@export()
type worldSiloActionGroupConfigType = {
  name: string
  shortName: string
  emailReceivers: { name: string, emailAddress: string, useCommonAlertSchema: bool }[]
  tags: tagsType?
}
@export()
type worldSiloConfigType = {
  authentication: { type: string, settings: { *: string } }
  container: {
    cpu: int
    memoryInGB: int
    name: string
  }
  compute: {
    adminUsername: string
    capacity: 1
    automaticRepairs: bool
    repairGracePeriod: string
    patchMode: 'AutomaticByPlatform' | 'ImageDefault'
    patchAssessmentMode: 'AutomaticByPlatform' | 'ImageDefault'
    imageReference: { publisher: string, offer: string, sku: string, version: string }
    osDiskSizeGB: int
    osDiskStorageType: string
    sku: string
    vmNamePrefix: string
    zones: int[]
  }
  lifecycle: { azureScheduledEvents: bool, healthPort: int, pollSeconds: int, shutdownSeconds: int, progressTimeoutSeconds: int, checkpointTimeoutSeconds: int, journalTimeoutSeconds: int, journalBacklogLimit: int }
  monitoring: { alertName: string, actionGroupResourceIds: string[], evaluationFrequency: string, windowSize: string, severity: int }
  network: {
    virtualNetworkName: string
    subnetName: string
    subnetPrefix: string
    natGatewayName: string
    securityGroupName: string
    publicIpName: string
    loadBalancerName: string
  }
  dns: {
    recordName: string
    ttl: int
    zoneName: string
  }
  federationKeySecretName: string
  releaseStateSecretName: string
  name: string
  port: int
  repositoryName: string
  roleDefinitionName: string
  tags: tagsType?
  userAssignedIdentity: {
    name: string
    tags: tagsType?
  }
  worldName: string
}

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Parameters
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
param configuration worldSiloConfigType
param actionGroup worldSiloActionGroupConfigType
param tags tagsType = {}
param owner string
param publishingPrincipalId string
param registryName string
param storageAccountName string

var worldStoreRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', worldStoreRole.name)
var publishingRoleDefinitionId = az.roleDefinitions('Storage Blob Data Contributor').id
var pullRoleDefinitionId = az.roleDefinitions('Container Registry Repository Reader').id

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Resources
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
module hostingActionGroup 'br/public:avm/res/insights/action-group:0.8.0' = {
  params: {
    name: actionGroup.name
    groupShortName: actionGroup.shortName
    emailReceivers: actionGroup.emailReceivers
    tags: union(tags, actionGroup.?tags ?? {})
  }
}
resource storage 'Microsoft.Storage/storageAccounts@2025-01-01' existing = {
  name: storageAccountName
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' existing = {
  parent: storage
  name: 'default'
}
// The object-store contract names each private container after its owning principal.
resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobs
  name: owner
  properties: { publicAccess: 'None' }
}
resource worldStoreRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, configuration.roleDefinitionName)
  properties: {
    roleName: configuration.roleDefinitionName
    description: 'Read and write world definitions, checkpoints and journals in the assigned container.'
    type: 'CustomRole'
    assignableScopes: [resourceGroup().id]
    permissions: [{
      // The object-store backend calls CreateIfNotExists before writing blobs.
      actions: ['Microsoft.Storage/storageAccounts/blobServices/containers/read', 'Microsoft.Storage/storageAccounts/blobServices/containers/write']
      dataActions: ['Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read', 'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write']
    }]
  }
}
resource runtimeBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: container
  name: guid(container.id, owner, worldStoreRoleDefinitionId)
  properties: {
    principalId: owner
    principalType: 'ServicePrincipal'
    roleDefinitionId: worldStoreRoleDefinitionId
  }
}
resource publishingBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: container
  name: guid(container.id, publishingPrincipalId, publishingRoleDefinitionId)
  properties: {
    principalId: publishingPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: publishingRoleDefinitionId
  }
}
resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: registryName
}
resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, owner, pullRoleDefinitionId)
  properties: {
    principalId: owner
    principalType: 'ServicePrincipal'
    roleDefinitionId: pullRoleDefinitionId
    conditionVersion: '2.0'
    condition: containerRepositoryCondition([configuration.repositoryName], false)
  }
}

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Outputs
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
output storageEndpoint string = storage.properties.primaryEndpoints.blob
output deploymentConfiguration worldSiloConfigType = union(configuration, {
  monitoring: union(configuration.monitoring, {
    actionGroupResourceIds: union([hostingActionGroup.outputs.resourceId], configuration.monitoring.actionGroupResourceIds)
  })
})
