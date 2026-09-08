extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0'

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Imports
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
import {
  agentProfileType
  imageType
} from 'br/public:avm/res/dev-ops-infrastructure/pool:0.7.0'
import {
  diagnosticSettingFullType
} from 'br/public:avm/utl/types/avm-common-types:0.7.0'

import {
  subnetType
} from 'ts/bvm:ptn_network_basic-topology:0.0.1'

import {
  groupType
  roleAssignmentType
} from './accessManagement.bicep'
import {
  containerRepositoryCondition
  frontDoorPublicContentReadCondition
  officialContentPublisherCondition
} from './abacConditions.bicep'

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Types
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
@export()
type accessManagementConfigType = {
  groups: groupType[]?
  roleAssignments: roleAssignmentType[]?
}
@export()
type actorsConfigType = {
  ingressExternal: bool?
  name: string
  privateNetworking: bool?
  tags: tagsType?
  userAssignedIdentity: userAssignedIdentityConfigType
}
@export()
type applicationInsightsConfigType = {
  name: string
  tags: tagsType?
}
@export()
type applicationRegistrationConfigType = {
  additionalIdentifierUris: string[]?
  identifierUri: string
  name: string
  preAuthorizedApplications: {
    appId: string
    delegatedPermissions: string[]
  }[]
  requiredResourceAccess: {
    resourceAppId: string
    resourceAccess: {
      id: string
      type: 'Scope'
    }[]
  }[]?
  spa: {
    redirectUris: string[]
  }?
  web: {
    homePageUrl: string?
    implicitGrantSettings: {
      enableAccessTokenIssuance: bool
      enableIdTokenIssuance: bool
    }?
    logoutUrl: string?
    redirectUris: string[]
  }?
}
@export()
type configurationStoreConfigType = {
  name: string
  sku: ('Developer' | 'Free' | 'Premium' | 'Standard')
  tags: tagsType?
}
@export()
type containerEnvironmentConfigType = {
  applicationInsights: applicationInsightsConfigType
  name: string
  tags: tagsType?
}
@export()
type containerRegistryConfigType = {
  deploy: bool?
  name: string
  sku: ('Basic' | 'Premium' | 'Standard')
  tags: tagsType?
}
@export()
type devCenterConfigType = {
  name: string
  project: {
    name: string
    tags: tagsType?
  }
  tags: tagsType?
}
@export()
type devOpsAgentPoolConfigType = {
  agentProfile: agentProfileType
  @minValue(1)
  @maxValue(10000)
  concurrency: int
  images: imageType[]
  name: string
  tags: tagsType?
  vmSkuName: string
}
@export()
type devOpsConfigType = {
  agentPool: devOpsAgentPoolConfigType
  organizationName: string
  projectName: string
}
@export()
type diskEncryptionSetConfigType = {
  name: string
  tags: tagsType?
}
@export()
type frontDoorConfigType = {
  applicationInsights: applicationInsightsConfigType
  name: string
  portal: {
    contentSecurityPolicy: { *: string[] }
    reportingEndpoints: { *: string }
  }
  routes: {
    cacheConfiguration: resourceInput<'Microsoft.Cdn/profiles/afdEndpoints/routes@2025-04-15'>.properties.cacheConfiguration?
    customDomains: string[]
    enableForCors: bool?
    name: string
    originGroupName: ('api' | 'blob-private' | 'blob-public' | 'configuration' | 'vsmarketplace')
    originPath: string?
    patternsToMatch: resourceInput<'Microsoft.Cdn/profiles/afdEndpoints/routes@2025-04-15'>.properties.patternsToMatch?
    ruleSets: ('api' | 'blob' | 'configuration' | 'portal' | 'vsmarketplace')[]?
    securityPolicies: ('rate-limit')[]?
    supportedProtocols: resourceInput<'Microsoft.Cdn/profiles/afdEndpoints/routes@2025-04-15'>.properties.supportedProtocols?
  }[]
  sku: ('premium' | 'standard')
  tags: tagsType?
  userAssignedIdentity: userAssignedIdentityConfigType
  webApplicationFirewallPolicy: {
    name: string
    tags: tagsType?
  }
}
@export()
type functionApplicationConfigType = {
  applicationInsights: applicationInsightsConfigType
  cors: {
    allowedOrigins: string[]?
    supportCredentials: bool?
  }?
  identityProviders: {
    azureActiveDirectory: {
      authorizationPolicy: {
        allowedApplications: string[]?
        allowedPrincipals: {
          groups: string[]?
          identities: string[]?
        }?
      }?
    }?
  }?
  name: string
  servicePlan: {
    name: string
    tags: tagsType?
  }
  tags: tagsType?
  userAssignedIdentity: userAssignedIdentityConfigType
}
@export()
type gitHubConfigType = {
  networkSettings: {
    businessId: string
    name: string
  }
}
@export()
type keyVaultConfigType = {
  name: string
  sku: ('premium' | 'standard')
  tags: tagsType?
}
@export()
type kubernetesServiceConfigType = {
  deploy: bool?
  name: string
  tags: tagsType?
}
@export()
type logAnalyticsWorkspaceConfigType = {
  name: string
  tags: tagsType?
}
@export()
type monitorPrivateLinkScopeConfigType = {
  name: string
  tags: tagsType?
}
@export()
type natGatewayConfigType = {
  name: string
  publicIpPrefix: {
    name: string
    tags: tagsType?
  }
  tags: tagsType?
}
@export()
type postgresFlexibleServerConfigType = {
  deploy: bool?
  administrators: {
    objectId: string
    principalName: string
    principalType: ('Group' | 'ServicePrincipal' | 'Unknown' | 'User')
    tenantId: string?
  }[]
  name: string
  sku: string
  storageSizeGB: (32 | 64 | 128 | 256 | 512 | 1024 | 2048 | 4096 | 8192 | 16384)?
  tags: tagsType?
  version: ('16' | '17' | '18')
}
@export()
type redisCacheConfigType = {
  capacity: (2 | 3 | 4 | 6 | 8 | 9 | 10)?
  evictionPolicy: (
    | 'AllKeysLFU'
    | 'AllKeysLRU'
    | 'AllKeysRandom'
    | 'NoEviction'
    | 'VolatileLFU'
    | 'VolatileLRU'
    | 'VolatileRandom'
    | 'VolatileTTL')?
  name: string
  sku: (
    | 'Balanced_B0'
    | 'Balanced_B1'
    | 'Balanced_B3'
    | 'Balanced_B5'
    | 'Balanced_B10'
    | 'Balanced_B20'
    | 'Balanced_B50'
    | 'Balanced_B100'
    | 'Balanced_B150'
    | 'Balanced_B250'
    | 'Balanced_B350'
    | 'Balanced_B500'
    | 'Balanced_B700'
    | 'Balanced_B1000'
    | 'ComputeOptimized_X3'
    | 'ComputeOptimized_X5'
    | 'ComputeOptimized_X10'
    | 'ComputeOptimized_X20'
    | 'ComputeOptimized_X50'
    | 'ComputeOptimized_X100'
    | 'ComputeOptimized_X150'
    | 'ComputeOptimized_X250'
    | 'ComputeOptimized_X350'
    | 'ComputeOptimized_X500'
    | 'ComputeOptimized_X700'
    | 'FlashOptimized_A250'
    | 'FlashOptimized_A500'
    | 'FlashOptimized_A700'
    | 'FlashOptimized_A1000'
    | 'FlashOptimized_A1500'
    | 'FlashOptimized_A2000'
    | 'FlashOptimized_A4500'
    | 'MemoryOptimized_M10'
    | 'MemoryOptimized_M20'
    | 'MemoryOptimized_M50'
    | 'MemoryOptimized_M100'
    | 'MemoryOptimized_M150'
    | 'MemoryOptimized_M250'
    | 'MemoryOptimized_M350'
    | 'MemoryOptimized_M500'
    | 'MemoryOptimized_M700'
    | 'MemoryOptimized_M1000'
    | 'MemoryOptimized_M1500'
    | 'MemoryOptimized_M2000')
  tags: tagsType?
}
@export()
type tagsType = { *: string }
@export()
type userAssignedIdentityConfigType = {
  name: string
  tags: tagsType?
}
@export()
type virtualNetworkConfigType = {
  addressPrefixes: string[]
  diagnosticSettings: diagnosticSettingFullType[]?
  dnsServers: string[]?
  name: string
  subnets: {
    containerEnvironment: subnetType
    devOpsAgentPool: subnetType
    flexConsumptionApplicationServicePlan: subnetType
    gitHubNetwork: subnetType
    kubernetesMachinePool: subnetType
    kubernetesServiceApi: subnetType
    privateEndpoints: subnetType
    *: subnetType
  }
  tags: tagsType?
}
@export()
type vsMarketplaceConfigType = {
  deploy: bool?
  name: string
  tags: tagsType?
  userAssignedIdentity: userAssignedIdentityConfigType
}

type resourceType = {
  accessManagement: accessManagementConfigType
  actors: actorsConfigType
  api: {
    functionApplication: functionApplicationConfigType
    storage: {
      private: {
        name: string
        tags: tagsType?
      }
      public: {
        eventGrid: {
          functionName: string
          name: string
          subscriptionName: string
        }
        name: string
        tags: tagsType?
      }
    }
  }
  applicationRegistration: applicationRegistrationConfigType
  configurationStore: configurationStoreConfigType
  containerEnvironment: containerEnvironmentConfigType
  containerRegistry: containerRegistryConfigType?
  devCenter: devCenterConfigType
  devOps: devOpsConfigType
  diskEncryptionSet: diskEncryptionSetConfigType
  frontDoor: frontDoorConfigType
  gitHub: gitHubConfigType
  keyVault: keyVaultConfigType
  kubernetesService: kubernetesServiceConfigType?
  logAnalyticsWorkspace: logAnalyticsWorkspaceConfigType
  monitorPrivateLinkScope: monitorPrivateLinkScopeConfigType
  natGateway: natGatewayConfigType
  postgresFlexibleServer: postgresFlexibleServerConfigType?
  redisCache: redisCacheConfigType
  userAssignedIdentityApplicationRegistration: userAssignedIdentityConfigType
  userAssignedIdentityCustomerManagedEncryption: userAssignedIdentityConfigType
  userAssignedIdentityKubernetesControlPlane: userAssignedIdentityConfigType
  userAssignedIdentityKubernetesKubelet: userAssignedIdentityConfigType
  userAssignedIdentityPublishing: userAssignedIdentityConfigType
  virtualNetwork: virtualNetworkConfigType
  vsMarketplace: vsMarketplaceConfigType
}

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Functions
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
func domainNameToResourceName(name string) string => replace(name, '.', '-')

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Parameters
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
param deployOwnerRoleAssignments bool = true
@description('Immutable actor image reference. Supply the digest produced by CI for application releases.')
param actorsImage string = ''
param enableCustomerManagedKey bool = true
param enableTelemetry bool = false
param enableZoneRedundancy bool = false
param ephemeral bool = false
param forcePrivateNetworking bool = true
@secure()
param gitHubApplicationPrivateKey string?
param location string = resourceGroup().location
param lockKind ('CanNotDelete' | 'None' | 'ReadOnly') = (ephemeral ? 'None' : 'CanNotDelete')
param owner {
  objectId: string
  principalType: ('Group' | 'ServicePrincipal' | 'User')
} = {
  objectId: deployer().objectId
  principalType: 'ServicePrincipal'
}
// User-data storage-account partitioning. Users shard across `count` accounts by oid (the same
// MonotonicPartitioner the edge, silo, and browser run), so count 1 is today's single account and
// raising it adds capacity; account/topic names are derived from the prefix, so the accounts
// follow automatically.
param partitioning {
  count: int
  prefix: string
} = {
  count: 1
  prefix: 'bytrc'
}
param resources resourceType

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Variables
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
var applicationRegistrationUniqueName = guid(resources.applicationRegistration.name)
var defaultAuditDiagnosticSettings = [
  {
    logCategoriesAndGroups: [
      {
        categoryGroup: 'audit'
      }
    ]
    workspaceResourceId: logAnalyticsWorkspace.outputs.resourceId
  }
]
var defaultCustomerManagedKeySettings = (enableCustomerManagedKey
  ? {
      keyName: encryption.customerManagedKey.keyName
      keyVaultResourceId: keyVault.outputs.resourceId
      userAssignedIdentityResourceId: userAssignedIdentityCustomerManagedEncryption!.outputs.resourceId
    }
  : null)
var defaultCustomerManagedKeySettingsWithAutoRotation = (empty(defaultCustomerManagedKeySettings)
  ? null
  : {
      autoRotationEnabled: true
      ...defaultCustomerManagedKeySettings!
    })
var customerManagedEncryptionUserAssignedResourceIds = (enableCustomerManagedKey
  ? [userAssignedIdentityCustomerManagedEncryption!.outputs.resourceId]
  : [])
var encryption = {
  customerManagedKey: {
    keyName: ((enableCustomerManagedKey && ephemeral)
      ? fail('ephemeral resources cannot have customer managed keys enabled')
      : 'CustomerManagedEncryption')
  }
  dataProtection: {
    blob: {
      blobPath: '/keys.xml'
      containerName: 'data-protection'
    }
    keyName: 'DataProtection'
  }
}
var frontDoorCorsOrigins = union(
  flatten(map(
    filter(resources.frontDoor.routes, route => (route.?enableForCors ?? false)),
    route => map(route.customDomains, domain => 'https://${domain}')
  )),
  []
)
var frontDoorCustomDomains = union(flatten(map(resources.frontDoor.routes, route => route.customDomains)), [])
var frontDoorDnsValidationMap = reduce(frontDoor.outputs.dnsValidation, {}, (result, validation) => {
  ...result
  '${join(skip(split(validation.?dnsTxtRecordName ?? '', '.'), 1), '.')}': {
    dnsTxtRecordName: validation.?dnsTxtRecordName
    dnsTxtRecordValue: validation.?dnsTxtRecordValue
  }
})
var frontDoorSkuMap = {
  premium: 'Premium_AzureFrontDoor'
  standard: 'Standard_AzureFrontDoor'
}
var monitoring = {
  applicationInsightsPublicNetworkAccess: {
    ingestion: 'Enabled'
    query: 'Enabled'
  }
  logAnalyticsWorkspacePublicNetworkAccess: {
    ingestion: 'Enabled'
    query: 'Enabled'
  }
  privateLinkScopeAccessMode: {
    ingestion: 'Open'
    query: 'Open'
  }
}
var networking = {
  publicNetworkAccess: (forcePrivateNetworking ? 'Disabled' : 'Enabled')
}
// Actor-tier inbound posture. The environment is always VNet-integrated; this decides whether its
// inbound load balancer is VNet-scoped (internal) or public. ACA has no per-app private inbound on
// a public environment — internal ingress is environment-scoped only — so on a public environment
// the silo is exposed on its public FQDN, restricted to the VNet's NAT egress prefix.
var containerEnvironmentIsInternal = (forcePrivateNetworking || (resources.actors.?privateNetworking ?? false))
// App-only role that lets a ByteTerrace host (the Functions edge's managed identity) invoke the
// Puck.Actors internal API; the silo requires this role claim on every /users/* call.
var actorsInvokeAppRole = {
  allowedMemberTypes: ['Application']
  description: 'Allows a ByteTerrace host to invoke the Puck.Actors internal API.'
  displayName: 'Actors Invoke'
  id: guid(tenant().tenantId, applicationRegistrationUniqueName, 'appRole', 'Actors.Invoke')
  isEnabled: true
  value: 'Actors.Invoke'
}
var oauth2PermissionScopeMap = {
  user_impersonation: {
    adminConsentDescription: 'Allow the application to access ${resources.applicationRegistration.name} on behalf of the signed-in user.'
    adminConsentDisplayName: 'Access ${resources.applicationRegistration.name}'
    id: guid(tenant().tenantId, applicationRegistrationUniqueName, 'scope', 'user_impersonation')
    isEnabled: true
    type: 'User'
    userConsentDescription: 'Allow the application to access ${resources.applicationRegistration.name} on your behalf.'
    userConsentDisplayName: 'Access ${resources.applicationRegistration.name} as the Signed-in User'
    value: 'user_impersonation'
  }
}
// An empty objectId means "the deploying principal" — correct for pipeline runs (the ADO managed
// identity, a service principal). Human-run deployments must override via BICEPPARAM_OWNER_OBJECT_ID
// + BICEPPARAM_OWNER_PRINCIPAL_TYPE=User, because role assignments validate the principal's type.
var ownerPrincipal = {
  objectId: (empty(owner.objectId) ? deployer().objectId : owner.objectId)
  principalType: owner.principalType
}
// One config per partition; the account and its blob-event topic/subscription follow the oid
// naming convention (partition i → account index i+1, since stp000 is the fabric account).
var publicStorageAccounts = [
  for i in range(0, partitioning.count): {
    // Event Grid resources are numbered by PARTITION index (0-based), which reproduces the
    // existing bytrcegstp000/bytrcevgsp000 names for partition 0 — a system topic is unique per
    // source account, so renaming one is a delete-and-recreate that would gap the audit trail.
    // Storage accounts stay offset by one because stp000 is the fabric account.
    eventGrid: {
      functionName: 'BlobCloudEvent'
      name: '${partitioning.prefix}egstp${padLeft(string(i), 3, '0')}'
      subscriptionName: '${partitioning.prefix}evgsp${padLeft(string(i), 3, '0')}'
    }
    name: '${partitioning.prefix}stp${padLeft(string(i + 1), 3, '0')}'
  }
]
var storage = {
  deadLetterContainerName: 'azure-eventgrid-deadletters'
  functionAppContainerName: '${resources.api.functionApplication.name}-${uniqueString(resourceId('Microsoft.Web/sites', resources.api.functionApplication.name))}'
  staticSiteContainerName: '$web'
  storageAccountPublicCorsRules: {
    blob: [
      {
        allowedHeaders: ['*']
        allowedMethods: [
          'DELETE'
          'GET'
          'HEAD'
          'OPTIONS'
          'MERGE'
          'PATCH'
          'POST'
          'PUT'
        ]
        allowedOrigins: frontDoorCorsOrigins
        exposedHeaders: ['*']
        maxAgeInSeconds: 3600
      }
    ]
    file: [
      {
        allowedHeaders: ['*']
        allowedMethods: [
          'DELETE'
          'GET'
          'HEAD'
          'OPTIONS'
          'MERGE'
          'POST'
          'PUT'
        ]
        allowedOrigins: frontDoorCorsOrigins
        exposedHeaders: ['*']
        maxAgeInSeconds: 3600
      }
    ]
    queue: [
      {
        allowedHeaders: ['*']
        allowedMethods: [
          'DELETE'
          'GET'
          'HEAD'
          'OPTIONS'
          'MERGE'
          'POST'
          'PUT'
        ]
        allowedOrigins: frontDoorCorsOrigins
        exposedHeaders: ['*']
        maxAgeInSeconds: 3600
      }
    ]
    table: [
      {
        allowedHeaders: ['*']
        allowedMethods: [
          'DELETE'
          'GET'
          'HEAD'
          'OPTIONS'
          'MERGE'
          'POST'
          'PUT'
        ]
        allowedOrigins: frontDoorCorsOrigins
        exposedHeaders: ['*']
        maxAgeInSeconds: 3600
      }
    ]
  }
  storageAccountPublicSecondaryEndpoints: reference(
    resourceId('Microsoft.Storage/storageAccounts', publicStorageAccounts[0].name),
    '2019-04-01'
  ).secondaryEndpoints
  vsMarketplaceSettings: {
    extensions: {
      fileShareName: 'vsmarketplace-extensions'
      mountPath: '/data/extensions'
    }
    logs: {
      fileShareName: 'vsmarketplace-logs'
      mountPath: '/data/logs'
    }
    targetPort: 8080
  }
}
var subnetResourceIdMap {
  containerEnvironment: string
  devOpsAgentPool: string
  flexConsumptionApplicationServicePlan: string
  gitHubNetwork: string
  kubernetesMachinePool: string
  kubernetesServiceApi: string
  privateEndpoints: string
} = mapValues(
  resources.virtualNetwork.subnets,
  subnet => resourceId('Microsoft.Network/virtualNetworks/subnets', resources.virtualNetwork.name, subnet.name)
)

var deployable = {
  kubernetesService: (enabled.kubernetesService && (enabled.containerRegistry
    ? true
    : fail('kubernetesService requires containerRegistry to be enabled')))
  vsMarketplace: (enabled.vsMarketplace && (enabled.containerRegistry
    ? true
    : fail('vsMarketplace requires containerRegistry to be enabled')))
}
var enabled = {
  containerRegistry: ((resources.?containerRegistry.?deploy ?? true) && !empty(resources.?containerRegistry.?name))
  kubernetesService: ((resources.?kubernetesService.?deploy ?? true) && !empty(resources.?kubernetesService.?name))
  postgreSql: ((resources.?postgresFlexibleServer.?deploy ?? true) && !empty(resources.?postgresFlexibleServer.?name))
  vsMarketplace: ((resources.?vsMarketplace.?deploy ?? true) && !empty(resources.?vsMarketplace.?name))
}

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Networking Resources
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
resource devOpsInfrastructure_servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' existing = {
  appId: '31687f79-5e43-4c1e-8c63-d9f4bff5cf8b'
}
@onlyIfNotExists() // NOTE: This bootstrap step was added to address a cyclic dependency between the Front Door and Function Application.
resource frontDoor_bootstrap 'Microsoft.Cdn/profiles@2025-06-01' = {
  location: 'global'
  name: resources.frontDoor.name
  sku: {
    name: frontDoorSkuMap[resources.frontDoor.sku]
  }
}

module basicNetworkTopology 'ts/bvm:ptn_network_basic-topology:0.0.1' = {
  params: {
    devOpsInfrastructureServicePrincipalId: devOpsInfrastructure_servicePrincipal.id
    enableTelemetry: enableTelemetry
    location: location
    lockKind: lockKind
    natGateway: resources.natGateway
    publicDnsZones: frontDoorCustomDomains
    virtualNetwork: {
      ...resources.virtualNetwork
      subnets: {
        ...resources.virtualNetwork.subnets
        kubernetesServiceApi: {
          ...resources.virtualNetwork.subnets.kubernetesServiceApi
          roleAssignments: [
            ...(resources.virtualNetwork.subnets.kubernetesServiceApi.?roleAssignments ?? [])
            ...(deployable.kubernetesService
              ? [
                  {
                    principalId: userAssignedIdentityKubernetesControlPlane!.outputs.principalId
                    principalType: 'ServicePrincipal'
                    roleDefinitionIdOrName: 'Network Contributor'
                  }
                ]
              : [])
          ]
        }
      }
    }
  }
}
module frontDoor 'br/public:avm/res/cdn/profile:0.19.2' = {
  params: {
    afdEndpoints: [
      {
        autoGeneratedDomainNameLabelScope: 'NoReuse'
        enabledState: 'Enabled'
        name: 'default'
        routes: [
          for route in resources.frontDoor.routes: {
            cacheConfiguration: (contains(route, 'cacheConfiguration')
              ? {
                  ...(route.?cacheConfiguration ?? {})
                  compressionSettings: {
                    contentTypesToCompress: [
                      'application/eot'
                      'application/font-sfnt'
                      'application/font'
                      'application/javascript'
                      'application/json'
                      'application/opentype'
                      'application/otf'
                      'application/pkcs7-mime'
                      'application/truetype'
                      'application/ttf'
                      'application/vnd.ms-fontobject'
                      'application/x-font-opentype'
                      'application/x-font-truetype'
                      'application/x-font-ttf'
                      'application/x-httpd-cgi'
                      'application/x-javascript'
                      'application/x-mpegurl'
                      'application/x-opentype'
                      'application/x-otf'
                      'application/x-perl'
                      'application/x-ttf'
                      'application/xhtml+xml'
                      'application/xml'
                      'application/xml+rss'
                      'font/eot'
                      'font/opentype'
                      'font/otf'
                      'font/ttf'
                      'image/svg+xml'
                      'text/css'
                      'text/csv'
                      'text/html'
                      'text/javascript'
                      'text/js'
                      'text/plain'
                      'text/richtext'
                      'text/tab-separated-values'
                      'text/x-component'
                      'text/x-java-source'
                      'text/x-script'
                      'text/xml'
                    ]
                    isCompressionEnabled: false
                    ...(route.cacheConfiguration!.?compressionSettings ?? {})
                  }
                }
              : null)
            customDomainNames: map(route.customDomains, domain => domainNameToResourceName(domain))
            enabledState: 'Enabled'
            forwardingProtocol: 'HttpsOnly'
            httpsRedirect: 'Enabled'
            linkToDefaultDomain: 'Disabled'
            name: route.name
            originGroupName: route.originGroupName
            originPath: route.?originPath
            patternsToMatch: (route.?patternsToMatch ?? [])
            ruleSets: (route.?ruleSets ?? [])
            supportedProtocols: (route.?supportedProtocols ?? ['Http', 'Https'])
          }
        ]
      }
    ]
    customDomains: [
      for domain in frontDoorCustomDomains: {
        azureDnsZoneResourceId: basicNetworkTopology.outputs.publicDnsZoneMap[domain]
        certificateType: 'ManagedCertificate'
        cipherSuiteSetType: 'TLS12_2023'
        hostName: domain
        minimumTlsVersion: 'TLS12'
        name: replace(domain, '.', '-')
      }
    ]
    diagnosticSettings: defaultAuditDiagnosticSettings
    enableTelemetry: enableTelemetry
    location: 'global'
    lock: {
      kind: lockKind
    }
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [frontDoor_userAssignedIdentity.outputs.resourceId]
    }
    originGroups: [
      {
        authentication: {
          // Front Door origin authentication allows custom Entra applications only via the
          // api://<GUID>/.default scope form; the https:// identifier-URI form is rejected.
          // The issued v2 token still carries the client id as its audience.
          scope: 'api://${applicationRegistration.appId}/.default'
          type: 'UserAssignedIdentity'
          userAssignedIdentity: {
            id: frontDoor_userAssignedIdentity.outputs.resourceId
          }
        }
        healthProbeSettings: {
          probeIntervalInSeconds: 127
          probePath: '/api/health-check'
          probeProtocol: 'Https'
          probeRequestType: 'GET'
        }
        loadBalancingSettings: {
          sampleSize: 5
          successfulSamplesRequired: 3
        }
        name: 'api'
        origins: [
          {
            enabledState: 'Enabled'
            enforceCertificateNameCheck: true
            hostName: publicFlexApi.outputs.function.defaultHostname
            httpPort: 80
            httpsPort: 443
            name: replace(publicFlexApi.outputs.function.defaultHostname, '.', '-')
          }
        ]
        sessionAffinityState: 'Disabled'
      }
      {
        healthProbeSettings: null
        loadBalancingSettings: {
          sampleSize: 5
          successfulSamplesRequired: 3
        }
        name: 'blob-private'
        origins: map(
          [
            parseUri(publicFlexApi.outputs.publicStorage[0].primaryBlobEndpoint).host
            parseUri(storage.storageAccountPublicSecondaryEndpoints.blob).host
          ],
          (hostName, index) => {
            enabledState: 'Enabled'
            enforceCertificateNameCheck: true
            hostName: hostName
            httpPort: 80
            httpsPort: 443
            name: replace(hostName, '.', '-')
            priority: (index + 1)
          }
        )
        sessionAffinityState: 'Disabled'
      }
      {
        authentication: {
          scope: 'https://storage.azure.com/.default'
          type: 'UserAssignedIdentity'
          userAssignedIdentity: {
            id: frontDoor_userAssignedIdentity.outputs.resourceId
          }
        }
        healthProbeSettings: {
          probeIntervalInSeconds: 127
          probePath: '/${storage.staticSiteContainerName}/index.html'
          probeProtocol: 'Https'
          probeRequestType: 'HEAD'
        }
        loadBalancingSettings: {
          sampleSize: 5
          successfulSamplesRequired: 3
        }
        name: 'blob-public'
        origins: map(
          [
            parseUri(publicFlexApi.outputs.publicStorage[0].primaryBlobEndpoint).host
            parseUri(storage.storageAccountPublicSecondaryEndpoints.blob).host
          ],
          (hostName, index) => {
            enabledState: 'Enabled'
            enforceCertificateNameCheck: true
            hostName: hostName
            httpPort: 80
            httpsPort: 443
            name: replace(hostName, '.', '-')
            priority: (index + 1)
          }
        )
        sessionAffinityState: 'Disabled'
      }
      {
        authentication: {
          scope: 'https://appconfig.azure.com/.default'
          type: 'UserAssignedIdentity'
          userAssignedIdentity: {
            id: frontDoor_userAssignedIdentity.outputs.resourceId
          }
        }
        healthProbeSettings: {
          probeIntervalInSeconds: 127
          probePath: '/keys?api-version=1.0'
          probeProtocol: 'Https'
          probeRequestType: 'GET'
        }
        loadBalancingSettings: {
          sampleSize: 5
          successfulSamplesRequired: 3
        }
        name: 'configuration'
        origins: map([parseUri(configurationStore.outputs.endpoint).host], (hostName, index) => {
          enabledState: 'Enabled'
          enforceCertificateNameCheck: true
          hostName: hostName
          httpPort: 80
          httpsPort: 443
          name: replace(hostName, '.', '-')
          priority: (index + 1)
        })
        sessionAffinityState: 'Disabled'
      }
      {
        healthProbeSettings: {
          probeIntervalInSeconds: 127
          probePath: '/health/ready'
          probeProtocol: 'Https'
          probeRequestType: 'GET'
        }
        loadBalancingSettings: {
          sampleSize: 5
          successfulSamplesRequired: 3
        }
        name: 'vsmarketplace'
        origins: map([vsMarketplace_containerApplication.outputs.fqdn], (hostName, index) => {
          enabledState: 'Enabled'
          enforceCertificateNameCheck: true
          hostName: hostName
          httpPort: 80
          httpsPort: 443
          name: replace(hostName, '.', '-')
          priority: (index + 1)
        })
        sessionAffinityState: 'Disabled'
      }
    ]
    name: resources.frontDoor.name
    roleAssignments: []
    ruleSets: [
      {
        name: 'api'
        rules: [
          {
            actions: [
              {
                name: 'ModifyRequestHeader'
                parameters: {
                  headerAction: 'Overwrite'
                  headerName: 'ClientAuthorization'
                  typeName: 'DeliveryRuleHeaderActionParameters'
                  value: '{http_req_header_Authorization}'
                }
              }
            ]
            conditions: [
              {
                name: 'RequestHeader'
                parameters: {
                  matchValues: []
                  negateCondition: false
                  operator: 'Any'
                  selector: 'Authorization'
                  transforms: []
                  typeName: 'DeliveryRuleRequestHeaderConditionParameters'
                }
              }
            ]
            matchProcessingBehavior: 'Stop'
            name: 'SetClientAuthorization'
            order: 0
          }
        ]
      }
      {
        name: 'blob'
        rules: [
          {
            actions: [
              {
                name: 'ModifyResponseHeader'
                parameters: {
                  headerAction: 'Overwrite'
                  headerName: 'Access-Control-Allow-Origin'
                  typeName: 'DeliveryRuleHeaderActionParameters'
                  value: '{http_req_header_Origin}'
                }
              }
            ]
            conditions: [
              {
                name: 'RequestHeader'
                parameters: {
                  matchValues: frontDoorCorsOrigins
                  negateCondition: false
                  operator: 'Equal'
                  selector: 'Origin'
                  transforms: ['Lowercase']
                  typeName: 'DeliveryRuleRequestHeaderConditionParameters'
                }
              }
            ]
            matchProcessingBehavior: 'Continue'
            name: 'SetCorsHeaders'
            order: 0
          }
          {
            // Stored-XSS posture: nothing under /public/* renders in-browser. Must precede the
            // Stop-behavior rewrite rules or it never runs. Remove this block once in-browser
            // rendering of published content becomes a goal.
            actions: [
              {
                name: 'ModifyResponseHeader'
                parameters: {
                  headerAction: 'Overwrite'
                  headerName: 'Content-Disposition'
                  typeName: 'DeliveryRuleHeaderActionParameters'
                  value: 'attachment'
                }
              }
            ]
            conditions: [
              {
                name: 'UrlPath'
                parameters: {
                  matchValues: ['public/']
                  negateCondition: false
                  operator: 'BeginsWith'
                  transforms: ['Lowercase']
                  typeName: 'DeliveryRuleUrlPathMatchConditionParameters'
                }
              }
              {
                // official/* never begins with public/, so this never actually fires today — it
                // says so explicitly rather than relying on the two prefixes staying disjoint by
                // accident. The mitigation above is for user-owned /public/*, where the uploader
                // is untrusted; only CI writes /official/* (officialContentPublisherCondition in
                // abacConditions.bicep), so there is no stored-content author to defend against.
                name: 'UrlPath'
                parameters: {
                  matchValues: ['official/']
                  negateCondition: true
                  operator: 'BeginsWith'
                  transforms: ['Lowercase']
                  typeName: 'DeliveryRuleUrlPathMatchConditionParameters'
                }
              }
            ]
            matchProcessingBehavior: 'Continue'
            name: 'SetPublicContentDisposition'
            order: 1
          }
          {
            actions: [
              {
                name: 'UrlRewrite'
                parameters: {
                  destination: '/{url_path:seg1}/private/{url_path:seg2:2147483647}'
                  preserveUnmatchedPath: false
                  sourcePattern: '/'
                  typeName: 'DeliveryRuleUrlRewriteActionParameters'
                }
              }
            ]
            conditions: [
              {
                name: 'UrlPath'
                parameters: {
                  matchValues: ['private/']
                  negateCondition: false
                  operator: 'BeginsWith'
                  transforms: ['Lowercase']
                  typeName: 'DeliveryRuleUrlPathMatchConditionParameters'
                }
              }
            ]
            matchProcessingBehavior: 'Stop'
            name: 'BlobPrivateUrlRewrite'
            order: 2
          }
          {
            actions: [
              {
                name: 'UrlRewrite'
                parameters: {
                  // Per-tenant publishing: /public/<tenant>/<path> serves <tenant>/public/<path>
                  // directly from the tenant's own container (mirrors BlobPrivateUrlRewrite).
                  destination: '/{url_path:seg1}/public/{url_path:seg2:2147483647}'
                  preserveUnmatchedPath: false
                  sourcePattern: '/'
                  typeName: 'DeliveryRuleUrlRewriteActionParameters'
                }
              }
            ]
            conditions: [
              {
                name: 'UrlPath'
                parameters: {
                  matchValues: ['public/']
                  negateCondition: false
                  operator: 'BeginsWith'
                  transforms: ['Lowercase']
                  typeName: 'DeliveryRuleUrlPathMatchConditionParameters'
                }
              }
            ]
            matchProcessingBehavior: 'Stop'
            name: 'BlobPublicUrlRewrite'
            order: 3
          }
          {
            actions: [
              {
                name: 'UrlRewrite'
                parameters: {
                  // Official content has one writer (CI) and no per-tenant story, so unlike
                  // BlobPublicUrlRewrite's per-tenant reorder this bakes the platform's own
                  // identity in place of a tenant segment — the same move FaviconUrlRewrite makes
                  // below. {url_path:seg1:...} is everything after the literal "official/" the
                  // condition matched, carried through unchanged.
                  destination: '/${frontDoor_userAssignedIdentity.outputs.principalId}/public/puck/official/{url_path:seg1:2147483647}'
                  preserveUnmatchedPath: false
                  sourcePattern: '/'
                  typeName: 'DeliveryRuleUrlRewriteActionParameters'
                }
              }
            ]
            conditions: [
              {
                name: 'UrlPath'
                parameters: {
                  matchValues: ['official/']
                  negateCondition: false
                  operator: 'BeginsWith'
                  transforms: ['Lowercase']
                  typeName: 'DeliveryRuleUrlPathMatchConditionParameters'
                }
              }
            ]
            matchProcessingBehavior: 'Stop'
            name: 'OfficialContentRewrite'
            order: 4
          }
          {
            actions: [
              {
                name: 'UrlRewrite'
                parameters: {
                  destination: '/${frontDoor_userAssignedIdentity.outputs.principalId}/private/favicon.ico'
                  preserveUnmatchedPath: false
                  sourcePattern: '/'
                  typeName: 'DeliveryRuleUrlRewriteActionParameters'
                }
              }
            ]
            conditions: [
              {
                name: 'UrlPath'
                parameters: {
                  matchValues: ['favicon.ico']
                  negateCondition: false
                  operator: 'Equal'
                  transforms: ['Lowercase']
                  typeName: 'DeliveryRuleUrlPathMatchConditionParameters'
                }
              }
            ]
            matchProcessingBehavior: 'Stop'
            name: 'FaviconUrlRewrite'
            order: 5
          }
        ]
      }
      {
        name: 'configuration'
        rules: [
          {
            actions: [
              {
                name: 'RouteConfigurationOverride'
                parameters: {
                  cacheConfiguration: {
                    cacheBehavior: 'OverrideAlways'
                    cacheDuration: '01:00:00'
                    isCompressionEnabled: 'Enabled'
                    queryParameters: 'after,api-version,key,label,snapshot,tags'
                    queryStringCachingBehavior: 'IncludeSpecifiedQueryStrings'
                  }
                  originGroupOverride: null
                  typeName: 'DeliveryRuleRouteConfigurationOverrideActionParameters'
                }
              }
              {
                name: 'UrlRewrite'
                parameters: {
                  destination: '/kv'
                  preserveUnmatchedPath: false
                  sourcePattern: '/'
                  typeName: 'DeliveryRuleUrlRewriteActionParameters'
                }
              }
            ]
            conditions: [
              {
                name: 'QueryString'
                parameters: {
                  matchValues: [
                    '^(?:after=[a-zA-Z0-9]+&)?(?:api-version=[a-zA-Z0-9\\.-]+&)?key=\\*&label=public$'
                  ]
                  negateCondition: false
                  operator: 'RegEx'
                  transforms: ['UrlDecode']
                  typeName: 'DeliveryRuleQueryStringConditionParameters'
                }
              }
              {
                name: 'UrlPath'
                parameters: {
                  matchValues: ['configuration']
                  negateCondition: false
                  operator: 'Equal'
                  transforms: ['Lowercase']
                  typeName: 'DeliveryRuleUrlPathMatchConditionParameters'
                }
              }
            ]
            matchProcessingBehavior: 'Stop'
            name: 'KeyValueFilter'
            order: 0
          }
          {
            actions: [
              {
                name: 'UrlRewrite'
                parameters: {
                  destination: '/'
                  preserveUnmatchedPath: false
                  sourcePattern: '/'
                  typeName: 'DeliveryRuleUrlRewriteActionParameters'
                }
              }
            ]
            conditions: []
            matchProcessingBehavior: 'Stop'
            name: 'DenyFilter'
            order: 1
          }
        ]
      }
      {
        name: 'portal'
        rules: [
          {
            actions: [
              {
                name: 'ModifyResponseHeader'
                parameters: {
                  headerAction: 'Overwrite'
                  headerName: 'Content-Security-Policy-Report-Only'
                  typeName: 'DeliveryRuleHeaderActionParameters'
                  value: join(
                    map(
                      items(resources.frontDoor.portal.contentSecurityPolicy),
                      directive =>
                        (empty(directive.value) ? directive.key : '${directive.key} ${join(directive.value, ' ')}')
                    ),
                    '; '
                  )
                }
              }
              {
                name: 'ModifyResponseHeader'
                parameters: {
                  headerAction: 'Overwrite'
                  headerName: 'Referrer-Policy'
                  typeName: 'DeliveryRuleHeaderActionParameters'
                  value: 'strict-origin-when-cross-origin'
                }
              }
              {
                name: 'ModifyResponseHeader'
                parameters: {
                  headerAction: 'Overwrite'
                  headerName: 'Reporting-Endpoints'
                  typeName: 'DeliveryRuleHeaderActionParameters'
                  value: join(
                    map(
                      items(resources.frontDoor.portal.reportingEndpoints ?? {}),
                      endpoint => '${endpoint.key}="${endpoint.value}"'
                    ),
                    ', '
                  )
                }
              }
              {
                name: 'ModifyResponseHeader'
                parameters: {
                  headerAction: 'Overwrite'
                  headerName: 'Strict-Transport-Security'
                  typeName: 'DeliveryRuleHeaderActionParameters'
                  value: 'max-age=31536000; includeSubDomains; preload'
                }
              }
              {
                name: 'ModifyResponseHeader'
                parameters: {
                  headerAction: 'Overwrite'
                  headerName: 'X-Content-Type-Options'
                  typeName: 'DeliveryRuleHeaderActionParameters'
                  value: 'nosniff'
                }
              }
              {
                name: 'ModifyResponseHeader'
                parameters: {
                  headerAction: 'Overwrite'
                  headerName: 'X-Frame-Options'
                  typeName: 'DeliveryRuleHeaderActionParameters'
                  value: 'DENY'
                }
              }
            ]
            name: 'SpaContentSecurityPolicy'
            order: 0
          }
          {
            actions: [
              {
                name: 'UrlRewrite'
                parameters: {
                  destination: '/${storage.staticSiteContainerName}/'
                  preserveUnmatchedPath: true
                  sourcePattern: '/'
                  typeName: 'DeliveryRuleUrlRewriteActionParameters'
                }
              }
            ]
            conditions: [
              {
                name: 'UrlFileExtension'
                parameters: {
                  matchValues: ['^(css|gif|html|ico|jpg|js|json|jxl|map|md|pdf|png|svg|txt|woff2)$']
                  negateCondition: false
                  operator: 'RegEx'
                  transforms: ['Lowercase']
                  typeName: 'DeliveryRuleUrlFileExtensionMatchConditionParameters'
                }
              }
            ]
            matchProcessingBehavior: 'Continue'
            name: 'SpaUrlRewriteAssets'
            order: 1
          }
          {
            actions: [
              {
                name: 'ModifyResponseHeader'
                parameters: {
                  headerAction: 'Overwrite'
                  headerName: 'Content-Encoding'
                  typeName: 'DeliveryRuleHeaderActionParameters'
                  value: 'br'
                }
              }
              {
                name: 'ModifyResponseHeader'
                parameters: {
                  headerAction: 'Append'
                  headerName: 'Vary'
                  typeName: 'DeliveryRuleHeaderActionParameters'
                  value: 'Accept-Encoding'
                }
              }
              {
                name: 'UrlRewrite'
                parameters: {
                  destination: '/${storage.staticSiteContainerName}/index.html'
                  preserveUnmatchedPath: false
                  sourcePattern: '/'
                  typeName: 'DeliveryRuleUrlRewriteActionParameters'
                }
              }
            ]
            conditions: [
              {
                name: 'UrlFileExtension'
                parameters: {
                  matchValues: ['^(css|gif|html|ico|jpg|js|json|jxl|map|md|pdf|png|svg|txt|woff2)$']
                  negateCondition: true
                  operator: 'RegEx'
                  transforms: ['Lowercase']
                  typeName: 'DeliveryRuleUrlFileExtensionMatchConditionParameters'
                }
              }
            ]
            matchProcessingBehavior: 'Stop'
            name: 'SpaUrlRewriteGeneral'
            order: 2
          }
          {
            actions: [
              {
                name: 'ModifyResponseHeader'
                parameters: {
                  headerAction: 'Overwrite'
                  headerName: 'Content-Encoding'
                  typeName: 'DeliveryRuleHeaderActionParameters'
                  value: 'br'
                }
              }
              {
                name: 'ModifyResponseHeader'
                parameters: {
                  headerAction: 'Append'
                  headerName: 'Vary'
                  typeName: 'DeliveryRuleHeaderActionParameters'
                  value: 'Accept-Encoding'
                }
              }
            ]
            conditions: [
              {
                name: 'UrlPath'
                parameters: {
                  matchValues: ['^(assets\\/.*|index\\.html)$']
                  negateCondition: false
                  operator: 'RegEx'
                  transforms: ['Lowercase']
                  typeName: 'DeliveryRuleUrlPathMatchConditionParameters'
                }
              }
            ]
            matchProcessingBehavior: 'Stop'
            name: 'SpaContentEncodingOverwrite'
            order: 3
          }
        ]
      }
      {
        name: 'vsmarketplace'
        rules: []
      }
    ]
    securityPolicies: [
      {
        associations: [
          {
            domains: [
              for domain in union(
                flatten(map(
                  filter(resources.frontDoor.routes, route => contains((route.?securityPolicies ?? []), 'rate-limit')),
                  route => route.customDomains
                )),
                []
              ): {
                id: resourceId(
                  'Microsoft.Cdn/profiles/customDomains',
                  resources.frontDoor.name,
                  domainNameToResourceName(domain)
                )
              }
            ]
            patternsToMatch: ['/*']
          }
        ]
        name: 'rate-limit'
        wafPolicyResourceId: frontDoor_waf_rateLimit.outputs.resourceId
      }
    ]
    sku: frontDoorSkuMap[resources.frontDoor.sku]
    tags: resources.frontDoor.?tags
  }
}
module frontDoor_dns 'br/public:avm/res/network/dns-zone:0.5.4' = [
  for domain in frontDoorCustomDomains: {
    params: {
      a: [
        {
          name: '@'
          targetResourceId: '${frontDoor.outputs.resourceId}/afdEndpoints/default'
          ttl: 3600
        }
      ]
      enableTelemetry: enableTelemetry
      location: 'global'
      name: join(skip(split(frontDoorDnsValidationMap[domain].dnsTxtRecordName, '.'), 1), '.')
      txt: [
        {
          name: first(split(frontDoorDnsValidationMap[domain].dnsTxtRecordName, '.'))
          ttl: 3600
          txtRecords: [
            {
              value: [
                frontDoorDnsValidationMap[domain].dnsTxtRecordValue
              ]
            }
          ]
        }
      ]
    }
  }
]
module frontDoor_userAssignedIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = {
  params: {
    enableTelemetry: enableTelemetry
    federatedIdentityCredentials: []
    location: location
    lock: {
      kind: lockKind
    }
    name: resources.frontDoor.userAssignedIdentity.name
    roleAssignments: []
    tags: resources.frontDoor.userAssignedIdentity.?tags
  }
}
module frontDoor_waf_rateLimit 'br/public:avm/res/network/front-door-web-application-firewall-policy:0.3.3' = {
  params: {
    customRules: {
      rules: [
        {
          action: 'Block'
          enabledState: 'Enabled'
          matchConditions: [
            {
              matchValue: ['0']
              matchVariable: 'RequestHeader'
              negateCondition: false
              operator: 'GreaterThanOrEqual'
              selector: 'Host'
              transforms: []
            }
          ]
          name: 'RateLimit'
          priority: 1
          rateLimitDurationInMinutes: 5
          rateLimitThreshold: 1500
          ruleType: 'RateLimitRule'
        }
      ]
    }
    enableTelemetry: enableTelemetry
    location: 'global'
    lock: {
      kind: lockKind
    }
    managedRules: {
      managedRuleSets: []
    }
    name: resources.frontDoor.webApplicationFirewallPolicy.name
    policySettings: {
      customBlockResponseBody: null
      customBlockResponseStatusCode: null
      enabledState: 'Enabled'
      logScrubbing: null
      mode: 'Prevention'
      redirectUrl: null
      requestBodyCheck: 'Disabled'
    }
    roleAssignments: []
    sku: 'Standard_AzureFrontDoor'
    tags: resources.frontDoor.webApplicationFirewallPolicy.?tags
  }
}
module monitorPrivateLinkScope 'br/public:avm/res/insights/private-link-scope:0.7.2' = if (forcePrivateNetworking) {
  params: {
    accessModeSettings: {
      exclusions: []
      ingestionAccessMode: monitoring.privateLinkScopeAccessMode.ingestion // TODO(security-hardening): Set to 'PrivateOnly' after monitoring dependencies are validated in production.
      queryAccessMode: monitoring.privateLinkScopeAccessMode.query // TODO(security-hardening): Set to 'PrivateOnly' after monitoring dependencies are validated in production.
    }
    enableTelemetry: enableTelemetry
    location: 'global'
    lock: {
      kind: lockKind
    }
    name: resources.monitorPrivateLinkScope.name
    privateEndpoints: [
      {
        enableTelemetry: enableTelemetry
        privateDnsZoneGroup: {
          privateDnsZoneGroupConfigs: [
            {
              privateDnsZoneResourceId: basicNetworkTopology.outputs.privateDnsZoneMap.monitor.agentService
            }
            {
              privateDnsZoneResourceId: basicNetworkTopology.outputs.privateDnsZoneMap.monitor.core
            }
            {
              privateDnsZoneResourceId: basicNetworkTopology.outputs.privateDnsZoneMap.monitor.insightsOds
            }
            {
              privateDnsZoneResourceId: basicNetworkTopology.outputs.privateDnsZoneMap.monitor.insightsOms
            }
            {
              privateDnsZoneResourceId: basicNetworkTopology.outputs.privateDnsZoneMap.storageAccount.blob
            }
          ]
        }
        subnetResourceId: subnetResourceIdMap.privateEndpoints
      }
    ]
    roleAssignments: []
    scopedResources: [
      {
        linkedResourceId: applicationInsightsFrontDoor.outputs.resourceId
        name: applicationInsightsFrontDoor.outputs.applicationId
      }
      {
        linkedResourceId: publicFlexApi.outputs.function.applicationInsights.resourceId
        name: publicFlexApi.outputs.function.applicationInsights.applicationId
      }
      {
        linkedResourceId: logAnalyticsWorkspace.outputs.resourceId
        name: logAnalyticsWorkspace.outputs.logAnalyticsWorkspaceId
      }
    ]
    tags: resources.monitorPrivateLinkScope.?tags
  }
}

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// DevOps Resources
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
module devOpsAgents 'ts/bvm:ptn_dev-ops_cicd-agents-and-runners:0.0.1' = {
  dependsOn: [basicNetworkTopology]
  params: {
    devCenter: {
      name: resources.devCenter.name
      tags: resources.devCenter.?tags
    }
    devCenterProject: {
      name: resources.devCenter.project.name
      tags: resources.devCenter.project.?tags
    }
    devOpsAgentPool: {
      agentProfile: resources.devOps.agentPool.agentProfile
      concurrency: resources.devOps.agentPool.concurrency
      fabricProfileSkuName: resources.devOps.agentPool.vmSkuName
      images: resources.devOps.agentPool.images
      name: resources.devOps.agentPool.name
      organizationName: resources.devOps.organizationName
      projectName: resources.devOps.projectName
      subnetResourceId: subnetResourceIdMap.devOpsAgentPool
      tags: resources.devOps.agentPool.?tags
    }
    enableTelemetry: enableTelemetry
    gitHubNetworkSettings: {
      name: resources.gitHub.networkSettings.name
      businessId: resources.gitHub.networkSettings.businessId
      subnetId: subnetResourceIdMap.gitHubNetwork
    }
    location: location
    lockKind: lockKind
    logAnalyticsWorkspaceResourceId: logAnalyticsWorkspace.outputs.resourceId
  }
}

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Application Resources
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
resource applicationRegistration 'Microsoft.Graph/applications@v1.0' = {
  appRoles: [actorsInvokeAppRole]
  api: {
    acceptMappedClaims: false
    knownClientApplications: []
    oauth2PermissionScopes: [oauth2PermissionScopeMap.user_impersonation]
    preAuthorizedApplications: [
      for application in resources.applicationRegistration.preAuthorizedApplications: {
        appId: application.appId
        delegatedPermissionIds: map(
          application.delegatedPermissions,
          permission => (oauth2PermissionScopeMap[?permission].?id ?? permission)
        )
      }
    ]
    requestedAccessTokenVersion: 2
  }
  authenticationBehaviors: {
    blockAzureADGraphAccess: true
    removeUnverifiedEmailClaim: true
  }
  defaultRedirectUri: null
  description: null
  displayName: resources.applicationRegistration.name
  groupMembershipClaims: 'SecurityGroup'
  identifierUris: union(
    [resources.applicationRegistration.identifierUri],
    (resources.applicationRegistration.?additionalIdentifierUris ?? [])
  )
  info: {
    marketingUrl: null
    privacyStatementUrl: null
    supportUrl: null
    termsOfServiceUrl: null
  }
  isDeviceOnlyAuthSupported: false
  nativeAuthenticationApisEnabled: 'none'
  optionalClaims: {
    accessToken: []
    idToken: [
      {
        essential: false
        name: 'login_hint'
      }
    ]
    saml2Token: []
  }
  owners: {
    relationships: [ownerPrincipal.objectId]
    relationshipSemantics: 'append' // TODO: Change to 'replace' after Microsoft resolves issue with replication delay.
  }
  publicClient: {
    redirectUris: []
  }
  requiredResourceAccess: resources.applicationRegistration.?requiredResourceAccess
  servicePrincipalLockConfiguration: {
    allProperties: true
    credentialsWithUsageSign: true
    credentialsWithUsageVerify: true
    isEnabled: true
    tokenEncryptionKeyId: true
  }
  signInAudience: 'AzureADMyOrg'
  spa: resources.applicationRegistration.?spa
  tags: []
  uniqueName: applicationRegistrationUniqueName
  web: resources.applicationRegistration.?web

  resource applicationRegistration_federatedIdentityCredential 'federatedIdentityCredentials@v1.0' = {
    audiences: ['api://AzureADTokenExchange']
    description: 'Federated identity credential for authentication to Azure Function App using "Easy Auth".'
    issuer: '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
    name: '${applicationRegistrationUniqueName}/${userAssignedIdentityApplicationRegistration.outputs.clientId}'
    subject: userAssignedIdentityApplicationRegistration.outputs.principalId
  }
  resource applicationRegistration_federatedIdentityCredential_actors 'federatedIdentityCredentials@v1.0' = {
    audiences: ['api://AzureADTokenExchange']
    description: 'Federated identity credential that lets the Puck.Actors silo identity mint on-behalf-of assertions.'
    issuer: '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
    name: '${applicationRegistrationUniqueName}/${actors_userAssignedIdentity.outputs.clientId}'
    subject: actors_userAssignedIdentity.outputs.principalId
  }
}
resource applicationRegistration_servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: applicationRegistration.appId
  owners: {
    relationships: [ownerPrincipal.objectId]
    relationshipSemantics: 'append'
  }
}

module applicationInsightsContainers 'br/public:avm/res/insights/component:0.7.1' = {
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
    name: resources.containerEnvironment.applicationInsights.name
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    retentionInDays: 30
    roleAssignments: []
    samplingPercentage: 100
    tags: resources.containerEnvironment.applicationInsights.?tags
    workspaceResourceId: logAnalyticsWorkspace.outputs.resourceId
  }
}
module applicationInsightsFrontDoor 'br/public:avm/res/insights/component:0.7.1' = {
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
    name: resources.frontDoor.applicationInsights.name
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    retentionInDays: 30
    roleAssignments: [
      {
        principalId: frontDoor_userAssignedIdentity.outputs.principalId
        principalType: 'ServicePrincipal'
        roleDefinitionIdOrName: 'Monitoring Metrics Publisher'
      }
    ]
    samplingPercentage: 100
    tags: resources.frontDoor.applicationInsights.?tags
    workspaceResourceId: logAnalyticsWorkspace.outputs.resourceId
  }
}
module configurationStore 'br/public:avm/res/app-configuration/configuration-store:0.9.3' = {
  params: {
    createMode: 'Default'
    customerManagedKey: defaultCustomerManagedKeySettingsWithAutoRotation
    dataPlaneProxy: {
      authenticationMode: 'Pass-through'
      privateLinkDelegation: 'Enabled'
    }
    diagnosticSettings: defaultAuditDiagnosticSettings
    disableLocalAuth: true
    enablePurgeProtection: !ephemeral
    enableTelemetry: enableTelemetry
    location: location
    lock: {
      kind: lockKind
    }
    managedIdentities: (enableCustomerManagedKey // TODO: Open an issue in the official repo to remove the need for this conditional.
      ? {
          systemAssigned: false
          userAssignedResourceIds: customerManagedEncryptionUserAssignedResourceIds
        }
      : null)
    name: resources.configurationStore.name
    privateEndpoints: (forcePrivateNetworking
      ? [
          {
            enableTelemetry: enableTelemetry
            privateDnsZoneGroup: {
              privateDnsZoneGroupConfigs: [
                {
                  privateDnsZoneResourceId: basicNetworkTopology.outputs.privateDnsZoneMap.configurationStore
                }
              ]
            }
            subnetResourceId: subnetResourceIdMap.privateEndpoints
          }
        ]
      : null)
    publicNetworkAccess: networking.publicNetworkAccess
    roleAssignments: [
      ...(deployOwnerRoleAssignments
        ? [
            {
              principalId: ownerPrincipal.objectId
              principalType: ownerPrincipal.principalType
              roleDefinitionIdOrName: 'App Configuration Data Owner'
            }
          ]
        : [])
      {
        principalId: frontDoor_userAssignedIdentity.outputs.principalId
        principalType: 'ServicePrincipal'
        roleDefinitionIdOrName: 'App Configuration Data Reader'
      }
      {
        principalId: userAssignedIdentityFunctionApplication.outputs.principalId
        principalType: 'ServicePrincipal'
        roleDefinitionIdOrName: 'App Configuration Data Reader'
      }
    ]
    sku: resources.configurationStore.sku
    softDeleteRetentionInDays: 7
    tags: resources.configurationStore.?tags
  }
}
module containerEnvironment 'br/public:avm/res/app/managed-environment:0.15.0' = {
  dependsOn: [basicNetworkTopology]
  params: {
    appInsightsConnectionString: applicationInsightsContainers.outputs.connectionString
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsWorkspaceResourceId: logAnalyticsWorkspace.outputs.resourceId
    }
    enableTelemetry: enableTelemetry
    infrastructureResourceGroupName: 'mrg-${resources.containerEnvironment.name}-${uniqueString(resourceId('Microsoft.App/managedEnvironments', resources.containerEnvironment.name))}'
    // Always VNet-integrated (outbound egresses the subnet/NAT gateway); `internal` only controls
    // whether the inbound load balancer is public or VNet-scoped. NOTE: either change is an
    // immutable network reconfiguration — the environment must be deleted and redeployed.
    infrastructureSubnetResourceId: subnetResourceIdMap.containerEnvironment
    internal: containerEnvironmentIsInternal
    location: location
    lock: {
      kind: lockKind
    }
    name: resources.containerEnvironment.name
    openTelemetryConfiguration: {
      logsConfiguration: {
        destinations: ['appInsights']
      }
      tracesConfiguration: {
        destinations: ['appInsights']
      }
    }
    peerTrafficEncryption: true
    publicNetworkAccess: (containerEnvironmentIsInternal ? 'Disabled' : networking.publicNetworkAccess)
    roleAssignments: []
    storages: []
    tags: resources.containerEnvironment.?tags
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    zoneRedundant: enableZoneRedundancy
  }
}
module containerEnvironment_privateEndpoint 'br/public:avm/res/network/private-endpoint:0.12.0' = if (forcePrivateNetworking) {
  name: '${uniqueString(deployment().name, location)}-managedEnvironments-PrivateEndpoint-0'
  params: {
    enableTelemetry: enableTelemetry
    location: basicNetworkTopology.outputs.location
    lock: {
      kind: lockKind
    }
    name: 'pep-${last(split(containerEnvironment.outputs.resourceId, '/'))}-managedEnvironments-0'
    privateDnsZoneGroup: {
      privateDnsZoneGroupConfigs: [
        {
          privateDnsZoneResourceId: format(
            basicNetworkTopology.outputs.privateDnsZoneMap.containerEnvironment,
            basicNetworkTopology.outputs.virtualNetworkName
          )
        }
      ]
    }
    privateLinkServiceConnections: [
      {
        name: '${last(split(containerEnvironment.outputs.resourceId, '/'))}-managedEnvironments-0'
        properties: {
          groupIds: ['managedEnvironments']
          privateLinkServiceId: containerEnvironment.outputs.resourceId
        }
      }
    ]
    subnetResourceId: subnetResourceIdMap.privateEndpoints
  }
}
module containerRegistry 'br/public:avm/res/container-registry/registry:0.12.1' = if (enabled.containerRegistry) {
  params: {
    acrAdminUserEnabled: false
    acrSku: resources.containerRegistry!.sku
    anonymousPullEnabled: false
    autoGeneratedDomainNameLabelScope: 'NoReuse'
    azureADAuthenticationAsArmPolicyStatus: 'disabled'
    cacheRules: [
      {
        name: 'mcr-k8se-quickstart'
        sourceRepository: 'mcr.microsoft.com/k8se/quickstart'
        targetRepository: 'mcr/k8se/quickstart'
      }
      {
        name: 'mcr-vsmarketplace-vscode-private-marketplace'
        sourceRepository: 'mcr.microsoft.com/vsmarketplace/vscode-private-marketplace'
        targetRepository: 'mcr/vsmarketplace/vscode-private-marketplace'
      }
    ]
    customerManagedKey: defaultCustomerManagedKeySettingsWithAutoRotation
    dataEndpointEnabled: forcePrivateNetworking
    diagnosticSettings: defaultAuditDiagnosticSettings
    enableTelemetry: enableTelemetry
    exportPolicyStatus: (forcePrivateNetworking ? 'disabled' : 'enabled')
    location: location
    lock: {
      kind: lockKind
    }
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: customerManagedEncryptionUserAssignedResourceIds
    }
    name: resources.containerRegistry!.name
    networkRuleBypassOptions: (forcePrivateNetworking ? 'None' : 'AzureServices')
    networkRuleSetDefaultAction: (forcePrivateNetworking ? 'Deny' : 'Allow')
    privateEndpoints: (forcePrivateNetworking
      ? [
          {
            enableTelemetry: enableTelemetry
            privateDnsZoneGroup: {
              privateDnsZoneGroupConfigs: [
                {
                  privateDnsZoneResourceId: basicNetworkTopology.outputs.privateDnsZoneMap.containerRegistry
                }
              ]
            }
            subnetResourceId: subnetResourceIdMap.privateEndpoints
          }
        ]
      : null)
    publicNetworkAccess: networking.publicNetworkAccess
    roleAssignmentMode: 'AbacRepositoryPermissions'
    roleAssignments: [
      ...[
        {
          principalId: userAssignedIdentityPublishing.outputs.principalId
          principalType: 'ServicePrincipal'
          roleDefinitionIdOrName: 'Container Registry Repository Contributor'
        }
        {
          principalId: vsMarketplace_userAssignedIdentity.outputs.principalId
          principalType: 'ServicePrincipal'
          condition: containerRepositoryCondition(['mcr/vsmarketplace/vscode-private-marketplace'], false)
          roleDefinitionIdOrName: 'Container Registry Repository Reader'
        }
        {
          principalId: actors_userAssignedIdentity.outputs.principalId
          principalType: 'ServicePrincipal'
          condition: containerRepositoryCondition(['web-actors'], false)
          roleDefinitionIdOrName: 'Container Registry Repository Reader'
        }
      ]
      ...(deployable.kubernetesService
        ? [
            {
              principalId: userAssignedIdentityKubernetesKubelet!.outputs.principalId
              principalType: 'ServicePrincipal'
              condition: containerRepositoryCondition(['web-actors', 'world-silo'], false)
              roleDefinitionIdOrName: 'Container Registry Repository Reader'
            }
          ]
        : [])
    ]
    tags: resources.containerRegistry!.?tags
    zoneRedundancy: (enableZoneRedundancy ? 'Enabled' : 'Disabled')
  }
}
module diskEncryptionSet 'br/public:avm/res/compute/disk-encryption-set:0.6.1' = if (enableCustomerManagedKey) {
  params: {
    customerManagedKey: defaultCustomerManagedKeySettingsWithAutoRotation
    enableKeyPermissions: false
    enableTelemetry: enableTelemetry
    encryptionType: 'EncryptionAtRestWithPlatformAndCustomerKeys'
    location: location
    lock: {
      kind: lockKind
    }
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [userAssignedIdentityCustomerManagedEncryption!.outputs.resourceId]
    }
    name: resources.diskEncryptionSet.name
    roleAssignments: []
    tags: resources.diskEncryptionSet.?tags
  }
}
module keyVault 'br/public:avm/res/key-vault/vault:0.13.3' = {
  params: {
    createMode: 'default'
    diagnosticSettings: defaultAuditDiagnosticSettings
    enablePurgeProtection: !ephemeral
    enableRbacAuthorization: true
    enableSoftDelete: true
    enableTelemetry: enableTelemetry
    enableVaultForDeployment: false
    enableVaultForDiskEncryption: false
    enableVaultForTemplateDeployment: true
    keys: [
      ...(enableCustomerManagedKey
        ? [
            {
              keyOps: [
                'unwrapKey'
                'wrapKey'
              ]
              keySize: 2048
              kty: 'RSA-HSM'
              name: encryption.customerManagedKey.keyName
              rotationPolicy: {
                lifetimeActions: [
                  {
                    action: {
                      type: 'notify'
                    }
                    trigger: {
                      timeBeforeExpiry: 'P30D'
                    }
                  }
                  {
                    action: {
                      type: 'rotate'
                    }
                    trigger: {
                      timeAfterCreate: 'P60D'
                    }
                  }
                ]
              }
              roleAssignments: [
                {
                  principalId: userAssignedIdentityCustomerManagedEncryption!.outputs.principalId
                  principalType: 'ServicePrincipal'
                  roleDefinitionIdOrName: 'Key Vault Crypto Service Encryption User'
                }
              ]
            }
          ]
        : [])
      {
        keyOps: [
          'unwrapKey'
          'wrapKey'
        ]
        keySize: 2048
        kty: 'RSA-HSM'
        name: encryption.dataProtection.keyName
        rotationPolicy: {
          lifetimeActions: [
            {
              action: {
                type: 'notify'
              }
              trigger: {
                timeBeforeExpiry: 'P30D'
              }
            }
            {
              action: {
                type: 'rotate'
              }
              trigger: {
                timeAfterCreate: 'P60D'
              }
            }
          ]
        }
        roleAssignments: [
          {
            principalId: userAssignedIdentityFunctionApplication.outputs.principalId
            principalType: 'ServicePrincipal'
            roleDefinitionIdOrName: 'Key Vault Crypto Service Encryption User'
          }
        ]
      }
    ]
    location: location
    lock: {
      kind: lockKind
    }
    name: resources.keyVault.name
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
      ipRules: []
      virtualNetworkRules: (forcePrivateNetworking
        ? []
        : [
            {
              id: subnetResourceIdMap.containerEnvironment
              ignoreMissingVnetServiceEndpoint: true
            }
            {
              id: subnetResourceIdMap.flexConsumptionApplicationServicePlan
              ignoreMissingVnetServiceEndpoint: true
            }
            {
              id: subnetResourceIdMap.kubernetesMachinePool
              ignoreMissingVnetServiceEndpoint: true
            }
          ])
    }
    privateEndpoints: (forcePrivateNetworking
      ? [
          {
            enableTelemetry: enableTelemetry
            privateDnsZoneGroup: {
              privateDnsZoneGroupConfigs: [
                {
                  privateDnsZoneResourceId: basicNetworkTopology.outputs.privateDnsZoneMap.keyVault
                }
              ]
            }
            subnetResourceId: subnetResourceIdMap.privateEndpoints
          }
        ]
      : null)
    publicNetworkAccess: networking.publicNetworkAccess // TODO: Set to 'SecuredByPerimeter' when AVM for Key Vault is updated.
    roleAssignments: (deployOwnerRoleAssignments
      ? [
          {
            principalId: ownerPrincipal.objectId
            principalType: ownerPrincipal.principalType
            roleDefinitionIdOrName: 'Key Vault Administrator'
          }
        ]
      : [])
    secrets: (empty(gitHubApplicationPrivateKey)
      ? []
      : [
          {
            name: 'GitHub--Application--PrivateKey'
            roleAssignments: (deployable.kubernetesService
              ? [
                  {
                    principalId: userAssignedIdentityKubernetesKubelet!.outputs.principalId
                    principalType: 'ServicePrincipal'
                    roleDefinitionIdOrName: 'Key Vault Secrets User'
                  }
                ]
              : [])
            value: gitHubApplicationPrivateKey!
          }
        ])
    softDeleteRetentionInDays: 90
    sku: resources.keyVault.sku
    tags: resources.keyVault.?tags
  }
}
module kubernetesService 'br/public:avm/res/container-service/managed-cluster:0.13.0' = if (deployable.kubernetesService) {
  dependsOn: [containerRegistry]
  params: {
    aadProfile: {
      adminGroupObjectIDs: []
      enableAzureRBAC: true
      managed: true
    }
    advancedNetworking: {
      enabled: false
      observability: {
        enabled: false
      }
      security: {
        advancedNetworkPolicies: 'None'
        enabled: false
      }
    }
    apiServerAccessProfile: {
      authorizedIPRanges: []
      disableRunCommand: true
      enablePrivateCluster: true
      enablePrivateClusterPublicFQDN: false
      enableVnetIntegration: true
      privateDNSZone: basicNetworkTopology.outputs.privateDnsZoneMap.containerService
      subnetId: subnetResourceIdMap.kubernetesServiceApi
    }
    autoUpgradeProfile: {
      nodeOSUpgradeChannel: 'NodeImage'
      upgradeChannel: 'stable'
    }
    azurePolicyEnabled: true
    diagnosticSettings: []
    disableLocalAccounts: true
    diskEncryptionSetResourceId: diskEncryptionSet.?outputs.resourceId
    enableDnsZoneContributorRoleAssignment: false
    enableKeyvaultSecretsProvider: true
    enableOidcIssuerProfile: true
    enableRBAC: true
    enableSecretRotation: true
    enableStorageProfileBlobCSIDriver: true
    enableStorageProfileDiskCSIDriver: true
    enableStorageProfileFileCSIDriver: true
    enableStorageProfileSnapshotController: true
    enableTelemetry: enableTelemetry
    httpApplicationRoutingEnabled: false
    identityProfile: {
      kubeletidentity: {
        clientId: userAssignedIdentityKubernetesKubelet!.outputs.clientId
        objectId: userAssignedIdentityKubernetesKubelet!.outputs.principalId
        resourceId: userAssignedIdentityKubernetesKubelet!.outputs.resourceId
      }
    }
    ingressApplicationGatewayEnabled: false
    kubeDashboardEnabled: false
    location: location
    lock: {
      kind: lockKind
    }
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [userAssignedIdentityKubernetesControlPlane!.outputs.resourceId]
    }
    monitoringWorkspaceResourceId: logAnalyticsWorkspace.outputs.resourceId
    name: resources.kubernetesService!.name
    networkDataplane: 'cilium'
    networkPlugin: 'azure'
    networkPluginMode: 'overlay'
    networkPolicy: 'cilium'
    nodeProvisioningProfile: {
      defaultNodePools: 'Auto'
      mode: 'Auto'
    }
    nodeResourceGroup: 'mrg-${resources.kubernetesService!.name}-${uniqueString(resourceId('Microsoft.ContainerService/managedClusters', resources.kubernetesService!.name))}'
    nodeResourceGroupProfile: {
      restrictionLevel: 'ReadOnly'
    }
    omsAgentEnabled: true
    omsAgentUseAADAuth: true
    outboundType: 'userAssignedNATGateway'
    primaryAgentPoolProfiles: [
      {
        availabilityZones: (enableZoneRedundancy ? [1, 2, 3] : [])
        mode: 'System'
        osDiskSizeGB: 110
        osDiskType: 'Ephemeral'
        osSKU: 'AzureLinux3'
        osType: 'Linux'
        name: 'system'
        vmSize: 'Standard_D2ds_v6'
        vnetSubnetResourceId: subnetResourceIdMap.kubernetesMachinePool
      }
    ]
    publicNetworkAccess: 'Enabled'
    roleAssignments: []
    skuName: 'Base'
    skuTier: 'Free'
    tags: resources.kubernetesService!.?tags
    webApplicationRoutingEnabled: false
  }
}
module kubernetesService_containerServiceDnsZoneRoleAssignment 'br/public:avm/ptn/authorization/resource-role-assignment:0.1.2' = if (deployable.kubernetesService) {
  params: {
    enableTelemetry: enableTelemetry
    principalId: userAssignedIdentityKubernetesControlPlane!.outputs.principalId
    principalType: 'ServicePrincipal'
    resourceId: basicNetworkTopology.outputs.privateDnsZoneMap.containerService
    roleDefinitionId: az.roleDefinitions('Network Contributor').id
  }
}
module logAnalyticsWorkspace 'br/public:avm/res/operational-insights/workspace:0.15.0' = {
  params: {
    dataRetention: 30
    diagnosticSettings: []
    enableTelemetry: enableTelemetry
    features: {
      disableLocalAuth: true
      enableDataExport: false
      enableLogAccessUsingOnlyResourcePermissions: true
      immediatePurgeDataOn30Days: false
    }
    forceCmkForQuery: false
    location: location
    lock: {
      kind: lockKind
    }
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: []
    }
    name: resources.logAnalyticsWorkspace.name
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    roleAssignments: []
    skuName: 'PerGB2018'
    tags: resources.logAnalyticsWorkspace.?tags
  }
}
module postgreSql 'br/public:avm/res/db-for-postgre-sql/flexible-server:0.15.2' = if (enabled.postgreSql) {
  params: {
    administrators: resources.postgresFlexibleServer!.administrators
    authConfig: {
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Disabled'
    }
    autoGrow: 'Disabled'
    availabilityZone: -1
    backupRetentionDays: 7
    configurations: [
      {
        name: 'pgaadauth.enable_group_sync'
        source: 'user-override'
        value: 'on'
      }
    ]
    createMode: 'Default'
    customerManagedKey: defaultCustomerManagedKeySettingsWithAutoRotation
    databases: []
    diagnosticSettings: []
    enableTelemetry: enableTelemetry
    geoRedundantBackup: 'Disabled'
    highAvailability: 'Disabled'
    location: location
    lock: {
      kind: lockKind
    }
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: customerManagedEncryptionUserAssignedResourceIds
    }
    name: resources.postgresFlexibleServer!.name
    privateEndpoints: (forcePrivateNetworking
      ? [
          {
            enableTelemetry: enableTelemetry
            privateDnsZoneGroup: {
              privateDnsZoneGroupConfigs: [
                {
                  privateDnsZoneResourceId: basicNetworkTopology.outputs.privateDnsZoneMap.postgresFlexibleServer
                }
              ]
            }
            subnetResourceId: subnetResourceIdMap.privateEndpoints
          }
        ]
      : null)
    publicNetworkAccess: networking.publicNetworkAccess
    roleAssignments: []
    skuName: (resources.postgresFlexibleServer!.sku)
    storageSizeGB: (resources.postgresFlexibleServer!.?storageSizeGB ?? 64)
    tags: resources.postgresFlexibleServer!.?tags
    tier: (startsWith(resources.postgresFlexibleServer!.sku, 'Standard_B')
      ? 'Burstable'
      : (startsWith(resources.postgresFlexibleServer!.sku, 'Standard_D')
          ? 'GeneralPurpose'
          : (startsWith(resources.postgresFlexibleServer!.sku, 'Standard_E') || startsWith(
                resources.postgresFlexibleServer!.sku,
                'Standard_M'
              ))
              ? 'MemoryOptimized'
              : fail('unsupported sku for postgres flexible server')))
    version: resources.postgresFlexibleServer!.version
  }
}
module publicFlexApi 'ts/bvm:ptn_platform_public-flex-api:0.0.3' = {
  params: {
    customerManagedKey: defaultCustomerManagedKeySettings
    enableTelemetry: enableTelemetry
    enableZoneRedundancy: enableZoneRedundancy
    forcePrivateNetworking: forcePrivateNetworking
    frontDoor: {
      corsOrigins: frontDoorCorsOrigins
      id: frontDoor_bootstrap.properties.frontDoorId
      userAssignedIdentityResourceId: frontDoor_userAssignedIdentity.outputs.resourceId
    }
    function: {
      applicationInsights: resources.api.functionApplication.applicationInsights
      applicationSettings: {
        configurationStoreEndpoint: configurationStore.outputs.endpoint
        dataProtection: {
          blobContainerName: encryption.dataProtection.blob.containerName
          blobPath: encryption.dataProtection.blob.blobPath
          keyName: encryption.dataProtection.keyName
          keyVaultResourceId: keyVault.outputs.resourceId
        }
        redisCacheEndpoint: redisCache.outputs.endpoint
      }
      cors: resources.api.functionApplication.?cors
      devOpsAgentPoolSubnetResourceId: subnetResourceIdMap.devOpsAgentPool
      identityProviders: {
        azureActiveDirectory: {
          applicationRegistration: {
            clientId: applicationRegistration.appId
            identifierUri: resources.applicationRegistration.identifierUri
          }
          userAssignedIdentityResourceId: userAssignedIdentityApplicationRegistration.outputs.resourceId
        }
      }
      name: resources.api.functionApplication.name
      servicePlan: {
        subnetResourceId: subnetResourceIdMap.flexConsumptionApplicationServicePlan
        ...resources.api.functionApplication.servicePlan
      }
      tags: resources.api.functionApplication.?tags
      userAssignedIdentity: resources.api.functionApplication.userAssignedIdentity
    }
    location: location
    lockKind: lockKind
    logAnalyticsWorkspaceResourceId: logAnalyticsWorkspace.outputs.resourceId
    storage: {
      networking: {
        // The Orleans silo (container environment subnet) needs the private account for
        // clustering/reminder tables, grain state, and the DataProtection key ring.
        additionalSubnetResourceIds: [subnetResourceIdMap.containerEnvironment]
        blobPrivateDnsZoneResourceId: basicNetworkTopology.outputs.privateDnsZoneMap.storageAccount.blob
        privateEndpointSubnetResourceId: subnetResourceIdMap.privateEndpoints
        queuePrivateDnsZoneResourceId: basicNetworkTopology.outputs.privateDnsZoneMap.storageAccount.queue
        tablePrivateDnsZoneResourceId: basicNetworkTopology.outputs.privateDnsZoneMap.storageAccount.table
      }
      private: resources.api.storage.private
      public: publicStorageAccounts
    }
  }
}
// Per-tenant publishing: /public/<tenant>/<path> rewrites to <tenant>/public/<path>, so the Front
// Door identity gets single-blob read on public/* across all tenant containers. Listing is denied
// outright (the Blob.List clause has no OR branch) so this identity can never enumerate a
// container. The host identities need no extra grants here — their conditioned "ByteTerrace
// Storage User" assignments already cover every non-private/ path, including public/*.
// The portal deploy (azcopy sync) writes the static site as the deploying identity, which needs
// blob data-plane access — but only to $web. Scoped to that container so the deploy identity has
// no standing read over users' private/ data; anything broader would bypass the ABAC conditions
// silently, since a standing grant leaves no role-assignment entry when it is used.
module publicFlexApi_staticSiteRoleAssignmentOwner './avm-temp/resource-role-assignment/main.bicep' = if (deployOwnerRoleAssignments) {
  dependsOn: [publicFlexApi]
  params: {
    description: 'Portal deployment: static-site container only.'
    enableTelemetry: false
    name: guid(
      resourceId(
        'Microsoft.Storage/storageAccounts/blobServices/containers',
        publicStorageAccounts[0].name,
        'default',
        storage.staticSiteContainerName
      ),
      ownerPrincipal.objectId,
      'Storage Blob Data Contributor'
    )
    principalId: ownerPrincipal.objectId
    principalType: ownerPrincipal.principalType
    resourceId: resourceId(
      'Microsoft.Storage/storageAccounts/blobServices/containers',
      publicStorageAccounts[0].name,
      'default',
      storage.staticSiteContainerName
    )
    roleDefinitionId: az.roleDefinitions('Storage Blob Data Contributor').id
  }
}
module publicFlexApi_publicFilesRoleAssignmentFrontDoor './avm-temp/resource-role-assignment/main.bicep' = [
  for account in publicStorageAccounts: {
    dependsOn: [publicFlexApi]
    params: {
      condition: frontDoorPublicContentReadCondition()
      description: 'Publishing pipeline: Front Door origin reads of public/* blobs in tenant containers; listing denied.'
      enableTelemetry: false
      name: guid(
        resourceId('Microsoft.Storage/storageAccounts', account.name),
        frontDoor_userAssignedIdentity.outputs.principalId,
        'Storage Blob Data Reader'
      )
      principalId: frontDoor_userAssignedIdentity.outputs.principalId
      principalType: 'ServicePrincipal'
      resourceId: resourceId('Microsoft.Storage/storageAccounts', account.name)
      roleDefinitionId: az.roleDefinitions('Storage Blob Data Reader').id
    }
  }
]
// Official content: the CI publishing identity gets write access to exactly its own prefix, in
// the platform's own container (named by the Front Door identity's principal id — the same
// container avm-temp/ptn/platform/public-flex-api/main.bicep provisions for platform-owned public
// content such as favicon.ico). Scoped to that one container, never the account, so a broadened
// publisher grant can never reach a tenant's data.
module publicFlexApi_officialContentRoleAssignmentPublisher './avm-temp/resource-role-assignment/main.bicep' = {
  dependsOn: [publicFlexApi]
  params: {
    condition: officialContentPublisherCondition()
    description: 'Publishing pipeline: CI writes puck.official.v1 content under public/puck/official.'
    enableTelemetry: false
    name: guid(
      resourceId(
        'Microsoft.Storage/storageAccounts/blobServices/containers',
        publicStorageAccounts[0].name,
        'default',
        frontDoor_userAssignedIdentity.outputs.principalId
      ),
      userAssignedIdentityPublishing.outputs.principalId,
      'Storage Blob Data Contributor'
    )
    principalId: userAssignedIdentityPublishing.outputs.principalId
    principalType: 'ServicePrincipal'
    resourceId: resourceId(
      'Microsoft.Storage/storageAccounts/blobServices/containers',
      publicStorageAccounts[0].name,
      'default',
      frontDoor_userAssignedIdentity.outputs.principalId
    )
    roleDefinitionId: az.roleDefinitions('Storage Blob Data Contributor').id
  }
}
// docs.yml (the Docs workflow) uploads docs/api and docs/site output straight to $web via
// az storage blob sync/upload, authenticated as this identity over OIDC — it needs the same
// container-scoped write access the portal deploy identity gets above, just for a different writer.
module publicFlexApi_staticSiteRoleAssignmentPublishing './avm-temp/resource-role-assignment/main.bicep' = {
  dependsOn: [publicFlexApi]
  params: {
    description: 'Docs workflow: static-site container only.'
    enableTelemetry: false
    name: guid(
      resourceId(
        'Microsoft.Storage/storageAccounts/blobServices/containers',
        publicStorageAccounts[0].name,
        'default',
        storage.staticSiteContainerName
      ),
      userAssignedIdentityPublishing.outputs.principalId,
      'Storage Blob Data Contributor'
    )
    principalId: userAssignedIdentityPublishing.outputs.principalId
    principalType: 'ServicePrincipal'
    resourceId: resourceId(
      'Microsoft.Storage/storageAccounts/blobServices/containers',
      publicStorageAccounts[0].name,
      'default',
      storage.staticSiteContainerName
    )
    roleDefinitionId: az.roleDefinitions('Storage Blob Data Contributor').id
  }
}
module redisCache 'br/public:avm/res/cache/redis-enterprise:0.5.1' = {
  params: {
    availabilityZones: (enableZoneRedundancy ? [1, 2, 3] : [])
    capacity: (resources.redisCache.?capacity ?? 2)
    customerManagedKey: defaultCustomerManagedKeySettings
    database: {
      accessKeysAuthentication: 'Disabled'
      accessPolicyAssignments: [
        {
          accessPolicyName: 'default'
          name: resources.api.functionApplication.userAssignedIdentity.name
          userObjectId: userAssignedIdentityFunctionApplication.outputs.principalId
        }
      ]
      clusteringPolicy: 'NoCluster'
      clientProtocol: 'Encrypted'
      deferUpgrade: 'NotDeferred'
      diagnosticSettings: []
      evictionPolicy: (resources.redisCache.?evictionPolicy ?? 'VolatileLRU')
      modules: []
      name: 'default'
      persistence: {
        type: 'disabled'
      }
      port: 10000
    }
    diagnosticSettings: []
    enableTelemetry: enableTelemetry
    highAvailability: (enableZoneRedundancy ? 'Enabled' : 'Disabled')
    location: location
    lock: {
      kind: lockKind
    }
    managedIdentities: {
      userAssignedResourceIds: customerManagedEncryptionUserAssignedResourceIds
    }
    minimumTlsVersion: '1.2'
    name: resources.redisCache.name
    privateEndpoints: (forcePrivateNetworking
      ? [
          {
            enableTelemetry: enableTelemetry
            privateDnsZoneGroup: {
              privateDnsZoneGroupConfigs: [
                {
                  privateDnsZoneResourceId: basicNetworkTopology.outputs.privateDnsZoneMap.redisCache
                }
              ]
            }
            subnetResourceId: subnetResourceIdMap.privateEndpoints
          }
        ]
      : null)
    publicNetworkAccess: networking.publicNetworkAccess
    roleAssignments: []
    skuName: (resources.redisCache.?sku ?? 'Balanced_B0')
    tags: resources.redisCache.?tags
  }
}
module userAssignedIdentityApplicationRegistration 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = {
  params: {
    enableTelemetry: enableTelemetry
    federatedIdentityCredentials: []
    location: location
    lock: {
      kind: lockKind
    }
    name: resources.userAssignedIdentityApplicationRegistration.name
    roleAssignments: []
    tags: resources.userAssignedIdentityApplicationRegistration.?tags
  }
}
module userAssignedIdentityCustomerManagedEncryption 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = if (enableCustomerManagedKey) {
  params: {
    enableTelemetry: enableTelemetry
    federatedIdentityCredentials: []
    location: location
    lock: {
      kind: lockKind
    }
    name: resources.userAssignedIdentityCustomerManagedEncryption.name
    roleAssignments: []
    tags: resources.userAssignedIdentityCustomerManagedEncryption.?tags
  }
}
module userAssignedIdentityFunctionApplication 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = {
  params: {
    enableTelemetry: enableTelemetry
    federatedIdentityCredentials: []
    location: location
    lock: {
      kind: lockKind
    }
    name: resources.api.functionApplication.userAssignedIdentity.name
    roleAssignments: []
    tags: resources.api.functionApplication.userAssignedIdentity.?tags
  }
}
module userAssignedIdentityKubernetesControlPlane 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = if (deployable.kubernetesService) {
  params: {
    enableTelemetry: enableTelemetry
    federatedIdentityCredentials: []
    location: location
    lock: {
      kind: lockKind
    }
    name: resources.userAssignedIdentityKubernetesControlPlane.name
    roleAssignments: []
    tags: resources.userAssignedIdentityKubernetesControlPlane.?tags
  }
}
module userAssignedIdentityKubernetesKubelet 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = if (deployable.kubernetesService) {
  params: {
    enableTelemetry: enableTelemetry
    federatedIdentityCredentials: []
    location: location
    lock: {
      kind: lockKind
    }
    name: resources.userAssignedIdentityKubernetesKubelet.name
    roleAssignments: [
      {
        principalId: userAssignedIdentityKubernetesControlPlane!.outputs.principalId
        principalType: 'ServicePrincipal'
        roleDefinitionIdOrName: 'Managed Identity Operator'
      }
    ]
    tags: resources.userAssignedIdentityKubernetesKubelet.?tags
  }
}
// Shared CI identity. The Puck environment uses GitHub's immutable repository subject;
// preserve the existing credential name so redeployment updates rather than duplicates it.
module userAssignedIdentityPublishing 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = {
  params: {
    enableTelemetry: enableTelemetry
    federatedIdentityCredentials: [
      {
        audiences: ['api://AzureADTokenExchange']
        issuer: 'https://token.actions.githubusercontent.com'
        name: '77854d2e-f1a3-4c91-bd8b-b05a5e2b9608'
        subject: 'repo:ByteTerrace@18753984/Puck@1271519029:environment:Puck'
      }
    ]
    location: location
    lock: {
      kind: lockKind
    }
    name: resources.userAssignedIdentityPublishing.name
    roleAssignments: []
    tags: resources.userAssignedIdentityPublishing.?tags
  }
}

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Actors (Orleans Silo) Resources
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Internal container environments get a random default domain; VNet consumers (the Functions
// edge) can only resolve it through a private DNS zone bearing that exact name.
// staticIp is populated for the internal VNet environments guarded by this condition.
module actors_containerEnvironmentDefaultDomainDnsZone 'br/public:avm/res/network/private-dns-zone:0.8.0' = if (containerEnvironmentIsInternal) {
  params: {
    a: [
      {
        aRecords: [
          {
            ipv4Address: containerEnvironment.outputs.staticIp!
          }
        ]
        name: '*'
        ttl: 3600
      }
      {
        aRecords: [
          {
            ipv4Address: containerEnvironment.outputs.staticIp!
          }
        ]
        name: '@'
        ttl: 3600
      }
    ]
    enableTelemetry: enableTelemetry
    lock: {
      kind: lockKind
    }
    name: containerEnvironment.outputs.defaultDomain
    tags: resources.containerEnvironment.?tags
    virtualNetworkLinks: [
      {
        registrationEnabled: false
        virtualNetworkResourceId: resourceId('Microsoft.Network/virtualNetworks', resources.virtualNetwork.name)
      }
    ]
  }
}
resource actors_graphServicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' existing = {
  appId: '00000003-0000-0000-c000-000000000000' // Microsoft Graph
}
resource actors_natGatewayPublicIpPrefix 'Microsoft.Network/publicIPPrefixes@2024-05-01' existing = {
  name: resources.natGateway.publicIpPrefix.name
}
// The Functions edge authenticates to the silo with an app-only token carrying this role.
resource actors_edgeInvokeAppRoleAssignment 'Microsoft.Graph/appRoleAssignedTo@v1.0' = {
  appRoleId: actorsInvokeAppRole.id
  principalId: userAssignedIdentityFunctionApplication.outputs.principalId
  resourceId: applicationRegistration_servicePrincipal.id
}
resource actors_graphAppRoleAssignments 'Microsoft.Graph/appRoleAssignedTo@v1.0' = [
  for appRoleId in [
    'de89b5e4-5b8f-48eb-8925-29c2b33bd8bd' // CustomSecAttributeAssignment.ReadWrite.All
    'dbaae8cf-10b5-4b86-a4a1-f871c94c6695' // GroupMember.ReadWrite.All
    'df021288-bdef-4463-88db-98f22de89214' // User.Read.All
  ]: {
    appRoleId: appRoleId
    principalId: actors_userAssignedIdentity.outputs.principalId
    resourceId: actors_graphServicePrincipal.id
  }
]
module actors_containerApplication 'br/public:avm/res/app/container-app:0.22.1' = {
  dependsOn: [containerEnvironment_privateEndpoint]
  params: {
    activeRevisionsMode: 'Single'
    additionalPortMappings: [
      {
        exposedPort: 11111
        external: false
        targetPort: 11111
      }
      {
        exposedPort: 30000
        external: false
        targetPort: 30000
      }
    ]
    containers: [
      {
        env: [
          {
            name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
            value: applicationInsightsContainers.outputs.connectionString
          }
          {
            // App-only tokens acquired via the identifier URI carry it as `aud`; the client-id
            // audience comes from the shared configuration store.
            name: 'Authorization__JwtBearer__TokenValidationParameters__ValidAudiences__0'
            value: resources.applicationRegistration.identifierUri
          }
          {
            name: 'AZURE_CLIENT_ID'
            value: actors_userAssignedIdentity.outputs.clientId
          }
          {
            name: 'ConfigurationStore__Endpoint'
            value: configurationStore.outputs.endpoint
          }
          {
            // TODO: Both hosts must move to a shared, deliberate application name; today this
            // mirrors the Functions host, which uses its identity's client id as the name.
            name: 'DataProtection__ApplicationName'
            value: userAssignedIdentityFunctionApplication.outputs.clientId
          }
          {
            name: 'DataProtection__BlobUri'
            value: 'https://${resources.api.storage.private.name}.blob.${environment().suffixes.storage}/${encryption.dataProtection.blob.containerName}${encryption.dataProtection.blob.blobPath}'
          }
          {
            name: 'DataProtection__KeyUri'
            value: '${keyVault.outputs.uri}keys/${encryption.dataProtection.keyName}'
          }
          {
            name: 'OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID'
            value: actors_userAssignedIdentity.outputs.clientId
          }
          {
            name: 'PrivateStorage__BlobEndpoint'
            value: 'https://${resources.api.storage.private.name}.blob.${environment().suffixes.storage}'
          }
          {
            name: 'PrivateStorage__TableEndpoint'
            value: 'https://${resources.api.storage.private.name}.table.${environment().suffixes.storage}'
          }
        ]
        image: empty(actorsImage) ? '${containerRegistry!.outputs.loginServer}/web-actors:latest' : actorsImage
        name: 'main'
        probes: [
          {
            httpGet: {
              path: '/healthz'
              port: 8080
              scheme: 'HTTP'
            }
            type: 'Startup'
          }
          {
            httpGet: {
              path: '/healthz'
              port: 8080
              scheme: 'HTTP'
            }
            type: 'Readiness'
          }
          {
            httpGet: {
              path: '/healthz'
              port: 8080
              scheme: 'HTTP'
            }
            type: 'Liveness'
          }
        ]
        resources: {
          cpu: json('0.5')
          memory: '1Gi'
        }
        volumeMounts: []
      }
    ]
    environmentResourceId: resourceId('Microsoft.App/managedEnvironments', resources.containerEnvironment.name)
    enableTelemetry: enableTelemetry
    ingressAllowInsecure: false
    // App-level "internal" ingress is environment-scoped only, which the Functions edge cannot
    // reach — so the silo always rides the environment's inbound load balancer: VNet-scoped when
    // the environment is internal, public-but-IP-restricted (below) when it is not.
    ingressExternal: (resources.actors.?ingressExternal ?? true)
    ingressTargetPort: 8080
    ingressTransport: 'auto'
    // On a public environment, only the VNet's own egress (the NAT gateway prefix the Functions
    // subnet routes through) may reach the silo; layered with Entra Actors.Invoke and the
    // DataProtection-bound escrow. An internal environment needs no ingress filter.
    ipSecurityRestrictions: (containerEnvironmentIsInternal
      ? []
      : [
          {
            action: 'Allow'
            description: 'ByteTerrace VNet egress (NAT gateway public IP prefix).'
            ipAddressRange: actors_natGatewayPublicIpPrefix.properties.ipPrefix
            name: 'vnet-nat-egress'
          }
        ])
    location: location
    lock: {
      kind: lockKind
    }
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [actors_userAssignedIdentity.outputs.resourceId]
    }
    name: resources.actors.name
    registries: [
      {
        identity: actors_userAssignedIdentity.outputs.resourceId
        server: containerRegistry!.outputs.loginServer
      }
    ]
    roleAssignments: []
    // Single replica until internal TCP exposure for silo-to-silo gossip is confirmed on the
    // managed environment's workload profile.
    scaleSettings: {
      maxReplicas: 1
      minReplicas: 1
    }
    secrets: []
    tags: resources.actors.?tags
    volumes: []
    workloadProfileName: 'Consumption'
  }
}
module actors_userAssignedIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = {
  params: {
    enableTelemetry: enableTelemetry
    federatedIdentityCredentials: []
    location: location
    lock: {
      kind: lockKind
    }
    name: resources.actors.userAssignedIdentity.name
    roleAssignments: []
    tags: resources.actors.userAssignedIdentity.?tags
  }
}

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// VS Marketplace Resources
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
module vsMarketplace_containerApplication 'br/public:avm/res/app/container-app:0.22.1' = {
  dependsOn: [containerEnvironment_privateEndpoint]
  params: {
    activeRevisionsMode: 'Single'
    containers: [
      {
        env: [
          {
            name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
            value: applicationInsightsContainers.outputs.connectionString
          }
          {
            name: 'Marketplace__ArtifactsClientId'
            value: vsMarketplace_userAssignedIdentity.outputs.clientId
          }
          {
            name: 'Marketplace__ArtifactsFeed'
            value: 'vsmarketplace'
          }
          {
            name: 'Marketplace__ArtifactsOrganization'
            value: resources.devOps.organizationName
          }
          {
            name: 'Marketplace__Logging__LogToConsole'
            value: 'false'
          }
          {
            name: 'Marketplace__Upstreaming__Mode'
            value: 'None'
          }
        ]
        image: '${containerRegistry!.outputs.loginServer}/mcr/vsmarketplace/vscode-private-marketplace:latest'
        name: 'main'
        probes: [
          {
            httpGet: {
              path: '/health/alive'
              port: storage.vsMarketplaceSettings.targetPort
              scheme: 'HTTP'
            }
            type: 'Startup'
          }
          {
            httpGet: {
              path: '/health/ready'
              port: storage.vsMarketplaceSettings.targetPort
              scheme: 'HTTP'
            }
            type: 'Readiness'
          }
        ]
        resources: {
          cpu: json('0.5')
          memory: '1Gi'
        }
        volumeMounts: []
      }
    ]
    environmentResourceId: resourceId('Microsoft.App/managedEnvironments', resources.containerEnvironment.name)
    enableTelemetry: enableTelemetry
    ingressAllowInsecure: false
    ingressExternal: true
    ingressTargetPort: storage.vsMarketplaceSettings.targetPort
    ingressTransport: 'auto'
    location: location
    lock: {
      kind: lockKind
    }
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [vsMarketplace_userAssignedIdentity.outputs.resourceId]
    }
    name: resources.vsMarketplace!.name
    registries: [
      {
        identity: vsMarketplace_userAssignedIdentity.outputs.resourceId
        server: containerRegistry!.outputs.loginServer
      }
    ]
    roleAssignments: []
    scaleSettings: {
      maxReplicas: 1
      minReplicas: 1
    }
    secrets: []
    tags: resources.vsMarketplace!.?tags
    volumes: []
    workloadProfileName: 'Consumption'
  }
}
module vsMarketplace_userAssignedIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = {
  params: {
    enableTelemetry: enableTelemetry
    federatedIdentityCredentials: []
    location: location
    lock: {
      kind: lockKind
    }
    name: resources.vsMarketplace.userAssignedIdentity.name
    roleAssignments: []
    tags: resources.vsMarketplace.userAssignedIdentity.?tags
  }
}

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Access Management
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
module accessManagement './accessManagement.bicep' = {
  dependsOn: [
    actors_containerApplication
    actors_userAssignedIdentity
    applicationInsightsContainers
    applicationInsightsFrontDoor
    basicNetworkTopology
    configurationStore
    containerEnvironment
    containerRegistry
    devOpsAgents
    frontDoor
    frontDoor_waf_rateLimit
    keyVault
    kubernetesService
    logAnalyticsWorkspace
    monitorPrivateLinkScope
    postgreSql
    publicFlexApi
    redisCache
    userAssignedIdentityApplicationRegistration
    vsMarketplace_containerApplication
  ]
  params: {
    groups: resources.accessManagement.?groups
    roleAssignments: resources.accessManagement.?roleAssignments
  }
}

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Outputs
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
output actorsEndpoint string = 'https://${actors_containerApplication.outputs.fqdn}'
output configurationStoreEndpoint string = configurationStore.outputs.endpoint
output containerRegistryEndpoint string = (containerRegistry.?outputs.loginServer ?? '')
output functionApplicationEndpoint string = 'https://${publicFlexApi.outputs.function.defaultHostname}/api'
// The literal puck.byteterrace.com host, not derived from resources.frontDoor.routes[].customDomains:
// main.bicep has no structured "the" primary domain parameter (the .bicepparam file only ever
// builds per-route customDomains strings), and OfficialContentRewrite is a fixed alias regardless
// of which custom domain a request arrived on.
output officialContentBaseUrl string = 'https://puck.byteterrace.com/official'
output officialContentContainerName string = frontDoor_userAssignedIdentity.outputs.principalId
output postgreSqlEndpoint string = (postgreSql.?outputs.fqdn ?? '')
// docs.yml's azure/login step consumes these two: AZURE_CLIENT_ID from clientId, plus the repo's
// own AZURE_TENANT_ID/AZURE_SUBSCRIPTION_ID variables — see CHECKLIST.md.
output publishingIdentityClientId string = userAssignedIdentityPublishing.outputs.clientId
output publishingIdentityPrincipalId string = userAssignedIdentityPublishing.outputs.principalId
output redisCacheEndpoint string = redisCache.outputs.endpoint
output staticSiteEndpoint string = '${publicFlexApi.outputs.publicStorage[0].primaryBlobEndpoint}${storage.staticSiteContainerName}/index.html'
