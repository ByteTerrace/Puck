import {
  deploymentType
} from 'br/public:avm/res/cognitive-services/account:0.19.0'

param applicationInsights {
  name: string
}
param cognitiveServices {
  customSubDomainName: string?
  deployments: deploymentType[]
  name: string
  networking: {
    agentSubnetResourceId: string
    aiServicesPrivateDnsZoneResourceId: string
    allowedCidrs: string[]?
    allowedSubnetResourceIds: string[]?
    cognitiveServicesPrivateDnsZoneResourceId: string
    openAiPrivateDnsZoneResourceId: string
    privateEndpointSubnetResourceId: string
  }
}
param cosmosDb {
  name: string
  networking: {
    allowedCidrs: string[]?
    allowedSubnetResourceIds: string[]?
    privateDnsZoneResourceId: string
    privateEndpointSubnetResourceId: string
  }
}
param customerManagedKey {
  keyName: string
  keyVaultResourceId: string
  userAssignedIdentityResourceId: string
}?
param enableTelemetry bool = false
param enableZoneRedundancy bool = true
param forcePrivateNetworking bool = true
param location string = resourceGroup().location
param lockKind ('CanNotDelete' | 'None' | 'ReadOnly') = 'None'
param logAnalyticsWorkspaceResourceId string

var customerManagedKeyUserAssignedResourceIds = (enableCustomerManagedKey
  ? [customerManagedKey!.userAssignedIdentityResourceId]
  : [])
var enableCustomerManagedKey = !empty(customerManagedKey)
var publicNetworkAccess = (forcePrivateNetworking ? 'Disabled' : 'Enabled')

module cognitiveServicesAccount 'br/public:avm/res/cognitive-services/account:0.19.0' = {
  params: {
    allowedFqdnList: []
    allowProjectManagement: true
    customerManagedKey: customerManagedKey
    customSubDomainName: (cognitiveServices.?customSubDomainName ?? cognitiveServices.name)
    deployments: cognitiveServices.deployments
    diagnosticSettings: []
    disableLocalAuth: true
    enableTelemetry: enableTelemetry
    kind: 'AIServices'
    location: location
    lock: {
      kind: lockKind
    }
    managedIdentities: {
      systemAssigned: true
      userAssignedResourceIds: customerManagedKeyUserAssignedResourceIds
    }
    name: cognitiveServices.name
    networkAcls: {
      bypass: 'None' // TODO: Open an issue in the official repo to respect the bypass property; it is currently ignored.
      defaultAction: 'Deny'
      ...(forcePrivateNetworking
        ? {}
        : {
            ipRules: map((cognitiveServices.networking.?allowedCidrs ?? []), value => {
              value: value
            })
            virtualNetworkRules: map((cognitiveServices.networking.?allowedSubnetResourceIds ?? []), value => {
              id: value
              ignoreMissingVnetServiceEndpoint: true
            })
          })
    }
    networkInjections: {
      scenario: 'agent'
      subnetResourceId: cognitiveServices.networking.agentSubnetResourceId
      useMicrosoftManagedNetwork: false
    }
    privateEndpoints: (forcePrivateNetworking
      ? [
          {
            enableTelemetry: enableTelemetry
            privateDnsZoneGroup: {
              privateDnsZoneGroupConfigs: [
                {
                  privateDnsZoneResourceId: cognitiveServices.networking.aiServicesPrivateDnsZoneResourceId
                }
                {
                  privateDnsZoneResourceId: cognitiveServices.networking.cognitiveServicesPrivateDnsZoneResourceId
                }
                {
                  privateDnsZoneResourceId: cognitiveServices.networking.openAiPrivateDnsZoneResourceId
                }
              ]
            }
            subnetResourceId: cognitiveServices.networking.privateEndpointSubnetResourceId
          }
        ]
      : null)
    publicNetworkAccess: publicNetworkAccess
    restrictOutboundNetworkAccess: true
    roleAssignments: []
    sku: 'S0'
  }
}
module cognitiveServicesAccount_applicationInsights 'br/public:avm/res/insights/component:0.8.0' = {
  params: {
    applicationType: 'web'
    diagnosticSettings: []
    disableIpMasking: false
    disableLocalAuth: true
    enableTelemetry: enableTelemetry
    ingestionMode: 'LogAnalytics'
    kind: 'web'
    location: location
    lock: {
      kind: lockKind
    }
    name: applicationInsights.name
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    retentionInDays: 30
    roleAssignments: []
    samplingPercentage: 100
    workspaceResourceId: logAnalyticsWorkspaceResourceId
  }
}
module cognitiveServicesAccount_cosmosDb 'br/public:avm/res/document-db/database-account:0.21.1' = {
  params: {
    capacityMode: 'Serverless'
    customerManagedKey: customerManagedKey
    defaultConsistencyLevel: 'Session'
    defaultIdentity: (enableCustomerManagedKey
      ? {
          name: 'UserAssignedIdentity'
          resourceId: first(customerManagedKeyUserAssignedResourceIds)
        }
      : null)
    diagnosticSettings: []
    disableKeyBasedMetadataWriteAccess: true
    disableLocalAuthentication: true
    enableTelemetry: enableTelemetry
    location: location
    lock: {
      kind: lockKind
    }
    managedIdentities: (enableCustomerManagedKey // TODO: Open an issue in the official repo to remove the need for this conditional.
      ? {
          systemAssigned: false
          userAssignedResourceIds: customerManagedKeyUserAssignedResourceIds
        }
      : null)
    minimumTlsVersion: 'Tls12'
    name: cosmosDb.name
    networkRestrictions: {
      ipRules: (cosmosDb.networking.?allowedCidrs ?? [])
      networkAclBypass: 'None'
      networkAclBypassResourceIds: []
      publicNetworkAccess: publicNetworkAccess
      virtualNetworkRules: map((cosmosDb.networking.?allowedSubnetResourceIds ?? []), value => {
        subnetResourceId: value
      })
    }
    privateEndpoints: (forcePrivateNetworking
      ? [
          {
            enableTelemetry: enableTelemetry
            privateDnsZoneGroup: {
              privateDnsZoneGroupConfigs: [
                {
                  privateDnsZoneResourceId: cosmosDb.networking.privateDnsZoneResourceId
                }
              ]
            }
            service: 'sql'
            subnetResourceId: cosmosDb.networking.privateEndpointSubnetResourceId
          }
        ]
      : null)
    roleAssignments: []
    sqlDatabases: []
    sqlRoleAssignments: []
    sqlRoleDefinitions: []
    zoneRedundant: enableZoneRedundancy
  }
}

