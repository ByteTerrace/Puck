/*
    The single source of truth for ByteTerrace access-management data, shared by
    main.bicepparam (nested under resources.accessManagement) and the standalone
    accessManagement.bicepparam.
*/

import {
  byteTerraceApiHostStorageUserCondition
  byteTerraceUserStorageUserCondition
} from './abacConditions.bicep'
import {
  groupType
  roleAssignmentType
} from './accessManagement.bicep'

@export()
func byteTerraceAccessManagementGroups() groupType[] => [
  {
    name: 'ByteTerrace API Users'
  }
]

// The per-partition storage grants (edge + silo hosts, and the user group) are replicated onto
// every user-data account — partition i is '<prefix>stp<i+1>', matching the oid partitioner's
// naming. The fabric/Key Vault/config grants are single. partitionCount = 1 reproduces the
// pre-partition set exactly.
@export()
func byteTerraceAccessManagementRoleAssignments(prefix string, partitionCount int) roleAssignmentType[] =>
  concat(
    flatten(map(range(0, partitionCount), i => [
      {
        resourcePath: '${prefix}stp${padLeft(string(i + 1), 3, '0')}'
        resourceProvider: 'Microsoft.Storage/storageAccounts'
        roleDefinitionName: 'ByteTerrace API Host'
        userAssignedIdentityId: '${prefix}idp002'
      }
      {
        condition: byteTerraceApiHostStorageUserCondition()
        resourcePath: '${prefix}stp${padLeft(string(i + 1), 3, '0')}'
        resourceProvider: 'Microsoft.Storage/storageAccounts'
        roleDefinitionName: 'ByteTerrace Storage User'
        userAssignedIdentityId: '${prefix}idp002'
      }
      {
        resourcePath: '${prefix}stp${padLeft(string(i + 1), 3, '0')}'
        resourceProvider: 'Microsoft.Storage/storageAccounts'
        roleDefinitionName: 'ByteTerrace API Host'
        userAssignedIdentityId: '${prefix}idp007'
      }
      {
        condition: byteTerraceApiHostStorageUserCondition()
        resourcePath: '${prefix}stp${padLeft(string(i + 1), 3, '0')}'
        resourceProvider: 'Microsoft.Storage/storageAccounts'
        roleDefinitionName: 'ByteTerrace Storage User'
        userAssignedIdentityId: '${prefix}idp007'
      }
      {
        condition: byteTerraceUserStorageUserCondition()
        groupName: 'ByteTerrace API Users'
        resourcePath: '${prefix}stp${padLeft(string(i + 1), 3, '0')}'
        resourceProvider: 'Microsoft.Storage/storageAccounts'
        roleDefinitionName: 'ByteTerrace Storage User'
      }
    ])),
    [
      {
        description: 'Puck.Actors silo: DataProtection key ring and Orleans grain-state blobs.'
        resourcePath: '${prefix}stp000'
        resourceProvider: 'Microsoft.Storage/storageAccounts'
        roleDefinitionName: 'Storage Blob Data Owner'
        userAssignedIdentityId: '${prefix}idp007'
      }
      {
        description: 'Puck.Actors silo: Orleans clustering membership and reminder tables.'
        resourcePath: '${prefix}stp000'
        resourceProvider: 'Microsoft.Storage/storageAccounts'
        roleDefinitionName: 'Storage Table Data Contributor'
        userAssignedIdentityId: '${prefix}idp007'
      }
      {
        description: 'Puck.Actors silo: DataProtection key wrap/unwrap.'
        resourcePath: '${prefix}kvp000'
        resourceProvider: 'Microsoft.KeyVault/vaults'
        roleDefinitionName: 'Key Vault Crypto Service Encryption User'
        userAssignedIdentityId: '${prefix}idp007'
      }
      {
        description: 'Puck.Actors silo: shared configuration store.'
        resourcePath: '${prefix}appcsp000'
        resourceProvider: 'Microsoft.AppConfiguration/configurationStores'
        roleDefinitionName: 'App Configuration Data Reader'
        userAssignedIdentityId: '${prefix}idp007'
      }
      {
        description: 'Puck.Actors silo: telemetry ingestion (local auth is disabled on the component).'
        resourcePath: '${prefix}appip002'
        resourceProvider: 'Microsoft.Insights/components'
        roleDefinitionName: 'Monitoring Metrics Publisher'
        userAssignedIdentityId: '${prefix}idp007'
      }
    ]
  )

