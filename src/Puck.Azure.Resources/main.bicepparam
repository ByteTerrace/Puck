/*
    Notes:
        - https://github.com/Azure/acr/blob/main/docs/custom-domain/README.md
*/

using './main.bicep'
import {
  byteTerraceAccessManagementGroups
  byteTerraceAccessManagementRoleAssignments
} from './accessManagementDefaults.bicep'

var apexDomainName = readEnvironmentVariable('BICEPPARAM_APEX_DOMAIN_NAME', 'byteterrace.com')
var partitionCount = int(readEnvironmentVariable('BICEPPARAM_PARTITION_COUNT', '1'))
var prefix = readEnvironmentVariable('BICEPPARAM_PREFIX', 'bytrc')

param deployOwnerRoleAssignments = bool(readEnvironmentVariable('BICEPPARAM_DEPLOY_OWNER_ROLE_ASSIGNMENTS', 'true'))
param enableCustomerManagedKey = false
param enableZoneRedundancy = false
param ephemeral = false
param forcePrivateNetworking = false
param gitHubApplicationPrivateKey = null
// Empty objectId = "the deploying principal" (pipeline default). For human-run deployments set
// BICEPPARAM_OWNER_OBJECT_ID to your object id and BICEPPARAM_OWNER_PRINCIPAL_TYPE=User.
param owner = {
  objectId: readEnvironmentVariable('BICEPPARAM_OWNER_OBJECT_ID', '')
  principalType: readEnvironmentVariable('BICEPPARAM_OWNER_PRINCIPAL_TYPE', 'ServicePrincipal')
}
param partitioning = {
  count: partitionCount
  prefix: prefix
}
param resources = {
  accessManagement: {
    groups: byteTerraceAccessManagementGroups()
    roleAssignments: byteTerraceAccessManagementRoleAssignments(prefix, partitionCount)
  }
  actors: {
    name: '${prefix}cap001'
    privateNetworking: false
    userAssignedIdentity: {
      name: '${prefix}idp007'
    }
  }
  api: {
    functionApplication: {
      applicationInsights: {
        name: '${prefix}appip000'
      }
      identityProviders: {
        azureActiveDirectory: {
          authorizationPolicy: {
            allowedApplications: [
              '04b07795-8ddb-461a-bbee-02f9e1bf7b46' // Azure CLI
              'aebc6443-996d-45c2-90f0-388ff96faa56' // Visual Studio Code
            ]
          }
        }
      }
      name: '${prefix}funcp000'
      servicePlan: {
        name: '${prefix}aspp000'
      }
      userAssignedIdentity: {
        name: '${prefix}idp002'
      }
    }
    storage: {
      private: {
        name: '${prefix}stp000'
      }
      public: {
        eventGrid: {
          functionName: 'BlobCloudEvent'
          name: 'bytrcegstp000'
          subscriptionName: 'bytrcevgsp000'
        }
        name: '${prefix}stp001'
      }
    }
  }
  applicationRegistration: {
    // Front Door origin authentication can only request tokens via the api://<appId> form,
    // which Entra will not resolve unless it is a registered identifier URI.
    additionalIdentifierUris: ['api://e6a7ab9f-19af-4eb0-b23f-a5bde0f90eb7']
    identifierUri: 'https://api.${apexDomainName}'
    name: 'ByteTerrace'
    preAuthorizedApplications: [
      {
        appId: '04b07795-8ddb-461a-bbee-02f9e1bf7b46' // Azure CLI
        delegatedPermissions: ['user_impersonation']
      }
      {
        appId: 'aebc6443-996d-45c2-90f0-388ff96faa56' // Visual Studio Code
        delegatedPermissions: ['user_impersonation']
      }
    ]
    requiredResourceAccess: [
      {
        resourceAppId: '499b84ac-1321-427f-aa17-267ca6975798' // Azure DevOps
        resourceAccess: [
          {
            id: 'ee69721e-6c3a-468f-a9ec-302d16a4c599' // https://app.vssps.visualstudio.com/user_impersonation
            type: 'Scope'
          }
        ]
      }
      {
        resourceAppId: '797f4846-ba00-4fd7-ba43-dac1f8f63013' // Azure Service Management
        resourceAccess: [
          {
            id: '41094075-9dad-400e-a0bd-54e686782033' // https://management.azure.com/user_impersonation
            type: 'Scope'
          }
        ]
      }
      {
        resourceAppId: 'e406a681-f3d4-42a8-90b6-c2b029497af1' // Azure Storage
        resourceAccess: [
          {
            id: '03e0da56-190b-40ad-a80c-ea378c433f7f' // https://storage.azure.com/user_impersonation
            type: 'Scope'
          }
        ]
      }
      {
        resourceAppId: '00000003-0000-0000-c000-000000000000' // Microsoft Graph
        resourceAccess: [
          {
            id: '64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0' // https://graph.microsoft.com/email
            type: 'Scope'
          }
          {
            id: '7427e0e9-2fba-42fe-b0c0-848c9e6a8182' // https://graph.microsoft.com/offline_access
            type: 'Scope'
          }
          {
            id: '37f7f235-527c-4136-accd-4a02d197296e' // https://graph.microsoft.com/openid
            type: 'Scope'
          }
          {
            id: '14dad69e-099b-42c9-810b-d002981feec1' // https://graph.microsoft.com/profile
            type: 'Scope'
          }
          {
            id: 'e1fe6dd8-ba31-4d61-89e7-88639da4683d' // https://graph.microsoft.com/User.Read
            type: 'Scope'
          }
        ]
      }
    ]
    spa: {
      redirectUris: [
        'https://puck.${apexDomainName}'
        'https://docs.${apexDomainName}'
        'https://bytrc.com'
        'https://${apexDomainName}'
        'https://portal.${apexDomainName}'
        'https://www.${apexDomainName}'
      ]
    }
    web: {
      homePageUrl: 'https://${apexDomainName}'
      implicitGrantSettings: {
        enableAccessTokenIssuance: false
        enableIdTokenIssuance: false
      }
      logoutUrl: null
      redirectUris: []
    }
  }
  configurationStore: {
    name: '${prefix}appcsp000'
    sku: 'Standard'
  }
  containerEnvironment: {
    applicationInsights: {
      name: '${prefix}appip002'
    }
    name: '${prefix}caep000'
  }
  containerRegistry: {
    name: '${prefix}crp000'
    sku: ((enableCustomerManagedKey || forcePrivateNetworking) ? 'Premium' : 'Standard')
  }
  devCenter: {
    name: '${prefix}adcp000'
    project: {
      name: '${prefix}adcpp000'
    }
  }
  devOps: {
    agentPool: {
      agentProfile: {
        kind: 'Stateless'
        resourcePredictionsProfile: null
      }
      concurrency: 2
      images: [
        {
          ephemeralType: 'Automatic'
          wellKnownImageName: 'ubuntu-24.04'
        }
        {
          ephemeralType: 'Automatic'
          wellKnownImageName: 'windows-2025'
        }
      ]
      name: '${prefix}mdpp000'
      vmSkuName: 'Standard_D2ads_v5'
    }
    organizationName: 'byteterrace'
    projectName: 'Koholint'
  }
  diskEncryptionSet: {
    name: '${prefix}desp000'
  }
  frontDoor: {
    applicationInsights: {
      name: '${prefix}appip001'
    }
    name: '${prefix}fdp000'
    portal: {
      contentSecurityPolicy: {
        'base-uri': ['\'self\'']
        'block-all-mixed-content': []
        'child-src': ['\'none\'']
        'connect-src': ['https:']
        'default-src': ['\'none\'']
        'font-src': ['\'self\'']
        'form-action': ['\'self\'']
        'frame-ancestors': ['\'self\'']
        'frame-src': ['\'self\'', 'https://login.microsoftonline.com']
        'img-src': ['\'self\'', 'data:']
        'manifest-src': ['\'self\'']
        'media-src': ['\'none\'']
        'object-src': ['\'none\'']
        'report-to': ['csp-reports']
        'script-src-attr': ['\'none\'']
        'script-src': ['\'self\'', 'https://aadcdn.msftauth.net']
        'style-src': ['\'self\'', '\'unsafe-inline\'']
        'upgrade-insecure-requests': []
        'worker-src': ['\'self\'']
      }
      reportingEndpoints: {
        'csp-reports': 'https://api.${apexDomainName}/csp-report'
      }
    }
    routes: [
      {
        customDomains: [
          apexDomainName
          'api.${apexDomainName}'
          'portal.${apexDomainName}'
          'puck.${apexDomainName}'
          'docs.${apexDomainName}'
          'www.${apexDomainName}'
          'bytrc.com'
        ]
        name: 'api'
        originGroupName: 'api'
        originPath: '/api'
        ruleSets: ['api']
        patternsToMatch: ['/api/*']
        securityPolicies: ['rate-limit']
      }
      {
        customDomains: [
          apexDomainName
          'blob.${apexDomainName}'
          'portal.${apexDomainName}'
          'puck.${apexDomainName}'
          'docs.${apexDomainName}'
          'www.${apexDomainName}'
          'bytrc.com'
        ]
        name: 'blob-private'
        originGroupName: 'blob-private'
        originPath: '/'
        patternsToMatch: ['/private/*']
        ruleSets: ['blob']
        securityPolicies: ['rate-limit']
      }
      {
        cacheConfiguration: {
          compressionSettings: {
            isCompressionEnabled: true
          }
          queryParameters: 'cache-id'
          queryStringCachingBehavior: 'IncludeSpecifiedQueryStrings'
        }
        customDomains: [
          apexDomainName
          'blob.${apexDomainName}'
          'portal.${apexDomainName}'
          'puck.${apexDomainName}'
          'docs.${apexDomainName}'
          'www.${apexDomainName}'
          'bytrc.com'
        ]
        name: 'blob-public'
        originGroupName: 'blob-public'
        originPath: '/'
        patternsToMatch: [
          '/official/*'
          '/public/*'
        ]
        ruleSets: ['blob']
        securityPolicies: ['rate-limit']
      }
      {
        customDomains: [
          apexDomainName
          'portal.${apexDomainName}'
          'puck.${apexDomainName}'
          'docs.${apexDomainName}'
          'www.${apexDomainName}'
          'bytrc.com'
        ]
        name: 'configuration'
        originGroupName: 'configuration'
        originPath: '/'
        patternsToMatch: [
          '/configuration'
          '/configuration/*'
        ]
        ruleSets: ['configuration']
        securityPolicies: ['rate-limit']
      }
      {
        cacheConfiguration: {
          queryParameters: 'cache-id'
          queryStringCachingBehavior: 'IncludeSpecifiedQueryStrings'
        }
        customDomains: [
          apexDomainName
          'portal.${apexDomainName}'
          'puck.${apexDomainName}'
          'docs.${apexDomainName}'
          'www.${apexDomainName}'
          'bytrc.com'
        ]
        enableForCors: true
        name: 'portal'
        originGroupName: 'blob-public'
        originPath: null
        ruleSets: ['portal']
        securityPolicies: ['rate-limit']
      }
      {
        customDomains: [
          apexDomainName
          'portal.${apexDomainName}'
          'vsx.${apexDomainName}'
          'www.${apexDomainName}'
          'bytrc.com'
        ]
        name: 'vsmarketplace'
        originGroupName: 'vsmarketplace'
        originPath: '/'
        patternsToMatch: [
          '/vsmarketplace'
          '/vsmarketplace/*'
        ]
        ruleSets: ['vsmarketplace']
        securityPolicies: ['rate-limit']
      }
    ]
    sku: 'standard'
    userAssignedIdentity: {
      name: '${prefix}idp003'
    }
    webApplicationFirewallPolicy: {
      name: '${prefix}fdfpp000'
    }
  }
  gitHub: {
    networkSettings: {
      businessId: '572101'
      name: '${prefix}ghns000'
    }
  }
  keyVault: {
    name: '${prefix}kvp000'
    sku: 'premium'
  }
  kubernetesService: {
    deploy: false
    name: '${prefix}aksp000'
  }
  logAnalyticsWorkspace: {
    name: '${prefix}logp000'
  }
  monitorPrivateLinkScope: {
    name: '${prefix}mplsp000'
  }
  natGateway: {
    name: '${prefix}ngp000'
    publicIpPrefix: {
      name: '${prefix}ipprep000'
    }
  }
  postgresFlexibleServer: {
    administrators: [
      {
        objectId: '<PLACEHOLDER>'
        principalType: 'ServicePrincipal'
        principalName: '<PLACEHOLDER>'
      }
    ]
    deploy: false
    name: '${prefix}psqlp000'
    sku: 'Standard_B1ms'
    storageSizeGB: 64
    version: '18'
  }
  redisCache: {
    capacity: 2
    evictionPolicy: 'VolatileLRU'
    name: '${prefix}amrp000'
    sku: 'Balanced_B0'
  }
  userAssignedIdentityApplicationRegistration: {
    name: '${prefix}idp000'
  }
  userAssignedIdentityCustomerManagedEncryption: {
    name: '${prefix}idp001'
  }
  userAssignedIdentityKubernetesControlPlane: {
    name: '${prefix}idp004'
  }
  userAssignedIdentityKubernetesKubelet: {
    name: '${prefix}idp005'
  }
  // bytrcidpzzz: the shared CI identity, federated to GitHub's Puck environment.
  userAssignedIdentityPublishing: {
    name: '${prefix}idpzzz'
  }
  virtualNetwork: {
    addressPrefixes: ['10.64.0.0/20']
    name: '${prefix}vnetp000'
    subnets: {
      // Existing reserved subnet; retain it when reconciling the production VNet.
      reserved007: {
        addressPrefixes: ['10.64.2.128/26']
        defaultOutboundAccess: false
        delegation: 'Microsoft.App/environments'
        name: '${prefix}snetp007'
        natGatewayResourceId: '${prefix}ngp000'
        privateEndpointNetworkPolicies: 'Disabled'
        privateLinkServiceNetworkPolicies: 'Enabled'
      }
      containerEnvironment: {
        addressPrefixes: ['10.64.1.128/26']
        defaultOutboundAccess: false
        delegation: 'Microsoft.App/environments'
        name: '${prefix}snetp003'
        natGatewayResourceId: '${prefix}ngp000'
        privateEndpointNetworkPolicies: 'Disabled'
        privateLinkServiceNetworkPolicies: 'Disabled'
        serviceEndpoints: (forcePrivateNetworking
          ? []
          : [
              'Microsoft.KeyVault'
              'Microsoft.Storage'
            ])
      }
      devOpsAgentPool: {
        addressPrefixes: ['10.64.1.64/26']
        defaultOutboundAccess: false
        delegation: 'Microsoft.DevOpsInfrastructure/pools'
        name: '${prefix}snetp002'
        natGatewayResourceId: '${prefix}ngp000'
        privateEndpointNetworkPolicies: 'Disabled'
        privateLinkServiceNetworkPolicies: 'Disabled'
        serviceEndpoints: ['Microsoft.Web']
      }
      flexConsumptionApplicationServicePlan: {
        addressPrefixes: ['10.64.1.0/26']
        defaultOutboundAccess: false
        delegation: 'Microsoft.App/environments'
        name: '${prefix}snetp001'
        natGatewayResourceId: '${prefix}ngp000'
        privateEndpointNetworkPolicies: 'Disabled'
        privateLinkServiceNetworkPolicies: 'Disabled'
        serviceEndpoints: (forcePrivateNetworking
          ? []
          : [
              'Microsoft.KeyVault'
              'Microsoft.Storage'
            ])
      }
      gitHubNetwork: {
        addressPrefixes: ['10.64.1.192/26']
        defaultOutboundAccess: false
        delegation: 'GitHub.Network/networkSettings'
        name: '${prefix}snetp004'
        privateEndpointNetworkPolicies: 'Disabled'
        privateLinkServiceNetworkPolicies: 'Disabled'
      }
      kubernetesMachinePool: {
        addressPrefixes: ['10.64.2.0/28']
        defaultOutboundAccess: false
        name: '${prefix}snetp006'
        natGatewayResourceId: '${prefix}ngp000'
        privateEndpointNetworkPolicies: 'Disabled'
        privateLinkServiceNetworkPolicies: 'Disabled'
        serviceEndpoints: (forcePrivateNetworking ? [] : ['Microsoft.KeyVault'])
      }
      kubernetesServiceApi: {
        addressPrefixes: ['10.64.2.64/26']
        defaultOutboundAccess: false
        delegation: 'Microsoft.ContainerService/managedClusters'
        name: '${prefix}snetp005'
        privateEndpointNetworkPolicies: 'Disabled'
        privateLinkServiceNetworkPolicies: 'Disabled'
      }
      privateEndpoints: {
        addressPrefixes: ['10.64.0.0/24']
        defaultOutboundAccess: false
        delegation: null
        name: '${prefix}snetp000'
        privateEndpointNetworkPolicies: 'Enabled'
        privateLinkServiceNetworkPolicies: 'Enabled'
      }
    }
  }
  vsMarketplace: {
    name: '${prefix}cap000'
    userAssignedIdentity: {
      name: '${prefix}idp006'
    }
  }
}
