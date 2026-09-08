// The silo owns one identity and one private container; it has no platform administration rights.
import { containerRepositoryCondition } from './abacConditions.bicep'

param owner string
param storageAccountName string
param registryName string
param publishingPrincipalId string

resource storage 'Microsoft.Storage/storageAccounts@2025-01-01' existing = {
  name: storageAccountName
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' existing = {
  parent: storage
  name: 'default'
}
resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobs
  name: owner
  properties: {
    publicAccess: 'None'
  }
}
resource worldStoreRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, 'Puck World Store')
  properties: {
    roleName: 'Puck World Store'
    description: 'Read and write world definitions, checkpoints and journals in the assigned container.'
    type: 'CustomRole'
    assignableScopes: [resourceGroup().id]
    permissions: [{
      // Raw AzureBlobObjectBlobStoreBackend targets call CreateIfNotExists before writes.
      actions: ['Microsoft.Storage/storageAccounts/blobServices/containers/read', 'Microsoft.Storage/storageAccounts/blobServices/containers/write']
      dataActions: ['Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read', 'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write']
    }]
  }
}
resource runtimeBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: container
  name: guid(container.id, owner, worldStoreRole.id)
  properties: {
    principalId: owner
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', worldStoreRole.name)
  }
}
resource publishingBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: container
  name: guid(container.id, publishingPrincipalId, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  properties: {
    principalId: publishingPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}
resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: registryName
}
resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, owner, 'b93aa761-3e63-49ed-ac28-beffa264f7ac')
  properties: {
    principalId: owner
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b93aa761-3e63-49ed-ac28-beffa264f7ac')
    conditionVersion: '2.0'
    condition: containerRepositoryCondition(['world-silo'], false)
  }
}
output storageEndpoint string = storage.properties.primaryEndpoints.blob
