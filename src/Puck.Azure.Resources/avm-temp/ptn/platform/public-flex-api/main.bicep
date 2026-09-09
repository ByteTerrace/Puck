// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Types
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
type tagsType = { *: string }

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Parameters
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
param customerManagedKey {
  keyName: string
  keyVaultResourceId: string
  userAssignedIdentityResourceId: string
}?
param enableTelemetry bool = false
param enableZoneRedundancy bool = true
param forcePrivateNetworking bool = true
param frontDoor {
  corsOrigins: string[]
  id: string
  userAssignedIdentityResourceId: string
}
param function {
  applicationInsights: {
    name: string
    tags: tagsType?
  }
  applicationSettings: {
    configurationStoreEndpoint: string
    dataProtection: {
      blobContainerName: string
      blobPath: string
      keyName: string
      keyVaultResourceId: string
    }
    redisCacheEndpoint: string
  }
  cors: {
    allowedOrigins: string[]?
    supportCredentials: bool?
  }?
  devOpsAgentPoolSubnetResourceId: string
  identityProviders: {
    azureActiveDirectory: {
      applicationRegistration: {
        clientId: string
        identifierUri: string
      }
      authorizationPolicy: {
        allowedApplications: string[]?
        allowedPrincipals: {
          groups: string[]?
          identities: string[]?
        }?
      }?
      userAssignedIdentityResourceId: string
    }
  }
  name: string
  servicePlan: {
    name: string
    subnetResourceId: string
    tags: tagsType?
  }
  tags: tagsType?
  userAssignedIdentity: {
    name: string
    tags: tagsType?
  }
}
param location string = resourceGroup().location
param lockKind ('CanNotDelete' | 'None' | 'ReadOnly') = 'None'
param logAnalyticsWorkspaceResourceId string
param storage {
  networking: {
    additionalSubnetResourceIds: string[]?
    blobPrivateDnsZoneResourceId: string
    privateEndpointSubnetResourceId: string
    queuePrivateDnsZoneResourceId: string
    tablePrivateDnsZoneResourceId: string
  }
  private: {
    name: string
    tags: tagsType?
  }
  // One entry per storage-account partition. Users shard across these by oid (the deterministic
  // MonotonicPartitioner), so growing the array adds capacity; every partition gets its own blob
  // event topic feeding the same audit function.
  public: {
    eventGrid: {
      functionName: string
      name: string
      subscriptionName: string
      tags: tagsType?
    }
    name: string
    tags: tagsType?
  }[]
}

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Variables
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
var applicationRegistrationUserAssignedIdentityResourceIdParts = split(
  function.identityProviders.azureActiveDirectory.userAssignedIdentityResourceId,
  '/'
)
var customerManagedKeyUserAssignedResourceIds = (enableCustomerManagedKey
  ? [customerManagedKey!.userAssignedIdentityResourceId]
  : [])
var dataProtectionKeyVaultResourceIdParts = split(function.applicationSettings.dataProtection.keyVaultResourceId, '/')
var defaultAuditDiagnosticSettings = [
  {
    logCategoriesAndGroups: [
      {
        categoryGroup: 'audit'
      }
    ]
    workspaceResourceId: logAnalyticsWorkspace.id
  }
]
var deadLetterContainerName = 'azure-eventgrid-deadletters'
var enableCustomerManagedKey = !empty(customerManagedKey)
var frontDoorUserAssignedIdentityResourceIdParts = split(frontDoor.userAssignedIdentityResourceId, '/')
var functionAppContainerName = '${function.name}-${uniqueString(resourceId('Microsoft.Web/sites', function.name))}'
var logAnalyticsWorkspaceResourceIdParts = split(logAnalyticsWorkspaceResourceId, '/')
var publicNetworkAccess = (forcePrivateNetworking ? 'Disabled' : 'Enabled')
var storageAccountPublicCorsRules = {
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
      allowedOrigins: frontDoor.corsOrigins
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
      allowedOrigins: frontDoor.corsOrigins
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
      allowedOrigins: frontDoor.corsOrigins
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
      allowedOrigins: frontDoor.corsOrigins
      exposedHeaders: ['*']
      maxAgeInSeconds: 3600
    }
  ]
}

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Existing Resources
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
resource applicationRegistration_userAssignedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: last(applicationRegistrationUserAssignedIdentityResourceIdParts)
  scope: resourceGroup(
    (applicationRegistrationUserAssignedIdentityResourceIdParts[?2] ?? subscription().subscriptionId),
    (applicationRegistrationUserAssignedIdentityResourceIdParts[?4] ?? resourceGroup().name)
  )
}
resource dataProtection_keyVault 'Microsoft.KeyVault/vaults@2025-05-01' existing = {
  name: last(dataProtectionKeyVaultResourceIdParts)
  scope: resourceGroup(
    (dataProtectionKeyVaultResourceIdParts[?2] ?? subscription().subscriptionId),
    (dataProtectionKeyVaultResourceIdParts[?4] ?? resourceGroup().name)
  )
}
resource frontDoor_userAssignedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: last(frontDoorUserAssignedIdentityResourceIdParts)
  scope: resourceGroup(
    (frontDoorUserAssignedIdentityResourceIdParts[?2] ?? subscription().subscriptionId),
    (frontDoorUserAssignedIdentityResourceIdParts[?4] ?? resourceGroup().name)
  )
}
resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2025-07-01' existing = {
  name: last(logAnalyticsWorkspaceResourceIdParts)
  scope: resourceGroup(
    (logAnalyticsWorkspaceResourceIdParts[?2] ?? subscription().subscriptionId),
    (logAnalyticsWorkspaceResourceIdParts[?4] ?? resourceGroup().name)
  )
}

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Managed Resources
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
module applicationServicePlan 'br/public:avm/res/web/serverfarm:0.7.0' = {
  params: {
    appServiceEnvironmentResourceId: null
    diagnosticSettings: []
    enableTelemetry: enableTelemetry
    kind: 'functionapp'
    location: location
    lock: {
      kind: lockKind
    }
    name: function.servicePlan.name
    reserved: true
    roleAssignments: []
    skuCapacity: 1
    skuName: 'FC1'
    tags: function.servicePlan.?tags
    zoneRedundant: false
  }
}
module functionApplication 'br/public:avm/res/web/site:0.22.0' = {
  params: {
    autoGeneratedDomainNameLabelScope: null
    basicPublishingCredentialsPolicies: [
      {
        allow: false
        name: 'ftp'
      }
      {
        allow: false
        name: 'scm'
      }
    ]
    clientAffinityEnabled: false
    clientAffinityPartitioningEnabled: false
    clientAffinityProxyEnabled: false
    clientCertEnabled: false
    clientCertExclusionPaths: null
    clientCertMode: 'Optional'
    configs: [
      {
        applicationInsightResourceId: functionApplication_applicationInsights.outputs.resourceId
        name: 'appsettings'
        properties: {
          APPLICATIONINSIGHTS_AUTHENTICATION_STRING: 'Authorization=AAD;ClientId=${functionApplication_userAssignedIdentity.outputs.clientId}'
          AzureWebJobsStorage__clientId: functionApplication_userAssignedIdentity.outputs.clientId
          AzureWebJobsStorage__credential: 'managedidentity'
          AZURE_CLIENT_ID: functionApplication_userAssignedIdentity.outputs.clientId
          ConfigurationStore__Endpoint: function.applicationSettings.configurationStoreEndpoint
          DataProtection__BlobUri: '${storageAccountPrivate.outputs.primaryBlobEndpoint}${function.applicationSettings.dataProtection.blobContainerName}${function.applicationSettings.dataProtection.blobPath}'
          DataProtection__KeyUri: '${dataProtection_keyVault.properties.vaultUri}keys/${function.applicationSettings.dataProtection.keyName}'
          OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID: applicationRegistration_userAssignedIdentity.properties.clientId
          RedisCache__Configuration: function.applicationSettings.redisCacheEndpoint
          WEBSITE_AUTH_AAD_ALLOWED_TENANTS: tenant().tenantId
        }
        retainCurrentAppSettings: false
        storageAccountResourceId: storageAccountPrivate.outputs.resourceId
        storageAccountUseIdentityAuthentication: true
      }
      {
        name: 'authsettingsV2'
        properties: {
          globalValidation: {
            redirectToProvider: null
            requireAuthentication: true
            unauthenticatedClientAction: 'Return401'
          }
          httpSettings: {
            forwardProxy: {
              convention: 'Standard'
            }
            requireHttps: true
          }
          identityProviders: {
            azureActiveDirectory: {
              enabled: true
              login: {
                loginParameters: []
              }
              registration: {
                clientId: function.identityProviders.azureActiveDirectory.applicationRegistration.clientId
                clientSecretSettingName: 'OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID'
                openIdIssuer: '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
              }
              validation: {
                allowedAudiences: [
                  function.identityProviders.azureActiveDirectory.applicationRegistration.identifierUri
                ]
                defaultAuthorizationPolicy: {
                  ...(function.?identityProviders.?azureActiveDirectory.?authorizationPolicy ?? {})
                  ...{
                    allowedApplications: [
                      function.identityProviders.azureActiveDirectory.applicationRegistration.clientId
                      frontDoor_userAssignedIdentity.properties.clientId
                      ...(function.?identityProviders.?azureActiveDirectory.?authorizationPolicy.?allowedApplications ?? [])
                    ]
                  }
                }
                jwtClaimChecks: {
                  allowedClientApplications: null
                  allowedGroups: null
                }
              }
            }
          }
          login: {
            tokenStore: {
              enabled: true
            }
          }
          platform: {
            enabled: true
            runtimeVersion: '~1'
          }
        }
      }
    ]
    diagnosticSettings: defaultAuditDiagnosticSettings
    enableTelemetry: enableTelemetry
    extensions: []
    e2eEncryptionEnabled: null
    functionAppConfig: {
      deployment: {
        storage: {
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: functionApplication_userAssignedIdentity.outputs.resourceId
          }
          type: 'blobContainer'
          value: '${storageAccountPrivate.outputs.primaryBlobEndpoint}${functionAppContainerName}'
        }
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
      scaleAndConcurrency: {
        instanceMemoryMB: 512
        maximumInstanceCount: 40
      }
    }
    httpsOnly: true
    hostNameSslStates: []
    keyVaultAccessIdentityResourceId: functionApplication_userAssignedIdentity.outputs.resourceId
    kind: 'functionapp,linux'
    location: location
    lock: {
      kind: lockKind
    }
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [
        applicationRegistration_userAssignedIdentity.id
        functionApplication_userAssignedIdentity.outputs.resourceId
      ]
    }
    name: function.name
    outboundVnetRouting: {
      allTraffic: true
      applicationTraffic: true
      backupRestoreTraffic: true
      contentShareTraffic: true
      imagePullTraffic: true
    }
    privateEndpoints: []
    publicNetworkAccess: 'Enabled'
    redundancyMode: null
    roleAssignments: []
    serverFarmResourceId: applicationServicePlan.outputs.resourceId
    siteConfig: {
      alwaysOn: false
      cors: {
        allowedOrigins: [
          'https://portal.azure.com'
          ...(function.?cors.?allowedOrigins ?? [])
          ...frontDoor.corsOrigins
        ]
        supportCredentials: (function.?cors.?supportCredentials ?? false)
      }
      ftpsState: 'Disabled'
      healthCheckPath: '/api/health-check'
      http20Enabled: true
      ipSecurityRestrictions: [
        {
          action: 'Allow'
          description: 'Allows the Azure Front Door instance to access the site.'
          headers: {
            'x-azure-fdid': [frontDoor.id]
          }
          ipAddress: 'AzureFrontDoor.Backend'
          name: 'AllowAzureFrontDoor'
          priority: 1
          tag: 'ServiceTag'
        }
        {
          action: 'Allow'
          description: 'Allows Azure Event Grid to access the site.'
          ipAddress: 'AzureEventGrid'
          name: 'AllowAzureEventGrid'
          priority: 1
          tag: 'ServiceTag'
        }
        {
          action: 'Allow'
          description: 'Allows the Azure Managed DevOps Pool subnet to access the site.'
          name: 'AllowManagedDevOpsPool'
          priority: 1
          tag: 'Default'
          vnetSubnetResourceId: function.devOpsAgentPoolSubnetResourceId
        }
      ]
      ipSecurityRestrictionsDefaultAction: 'Deny'
      keyVaultReferenceIdentity: functionApplication_userAssignedIdentity.outputs.resourceId
      localMySqlEnabled: false
      minTlsCipherSuite: 'TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256'
      minTlsVersion: '1.2'
      remoteDebuggingEnabled: false
      scmIpSecurityRestrictions: []
      scmIpSecurityRestrictionsDefaultAction: 'Deny'
      scmIpSecurityRestrictionsUseMain: true
      scmMinTlsVersion: '1.2'
      scmType: 'None'
      use32BitWorkerProcess: false
      webSocketsEnabled: false
    }
    slots: []
    sshEnabled: false
    storageAccountRequired: true
    tags: function.?tags
    virtualNetworkSubnetResourceId: function.servicePlan.subnetResourceId
  }
}
module functionApplication_applicationInsights 'br/public:avm/res/insights/component:0.7.1' = {
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
    name: function.applicationInsights.name
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    retentionInDays: 30
    roleAssignments: [
      {
        principalId: functionApplication_userAssignedIdentity.outputs.principalId
        principalType: 'ServicePrincipal'
        roleDefinitionIdOrName: 'Monitoring Metrics Publisher'
      }
    ]
    samplingPercentage: 100
    tags: function.applicationInsights.?tags
    workspaceResourceId: logAnalyticsWorkspace.id
  }
}
module functionApplication_userAssignedIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.5.0' = {
  params: {
    enableTelemetry: enableTelemetry
    federatedIdentityCredentials: []
    location: location
    lock: {
      kind: lockKind
    }
    name: function.userAssignedIdentity.name
    roleAssignments: []
    tags: function.userAssignedIdentity.?tags
  }
}
module storageAccountPrivate 'br/public:avm/res/storage/storage-account:0.32.0' = {
  params: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    allowedCopyScope: 'PrivateLink'
    allowSharedKeyAccess: false
    azureFilesIdentityBasedAuthentication: {
      defaultSharePermission: 'None'
      directoryServiceOptions: 'None'
      smbOAuthSettings: {
        isSmbOAuthEnabled: true
      }
    }
    blobServices: {
      automaticSnapshotPolicyEnabled: false
      changeFeedEnabled: false
      changeFeedRetentionInDays: null
      containerDeleteRetentionPolicyAllowPermanentDelete: false
      containerDeleteRetentionPolicyDays: 13
      containerDeleteRetentionPolicyEnabled: true
      containers: [
        {
          name: deadLetterContainerName
        }
        {
          name: function.applicationSettings.dataProtection.blobContainerName
        }
        {
          name: functionAppContainerName
        }
      ]
      corsRules: []
      deleteRetentionPolicyAllowPermanentDelete: false
      deleteRetentionPolicyDays: 13
      deleteRetentionPolicyEnabled: true
      diagnosticSettings: defaultAuditDiagnosticSettings
      isVersioningEnabled: true
      lastAccessTimeTrackingPolicyEnabled: true
      restorePolicyDays: null
      restorePolicyEnabled: false
      versionDeletePolicyDays: null
    }
    customerManagedKey: (enableCustomerManagedKey
      ? {
          autoRotationEnabled: true
          ...customerManagedKey!
        }
      : null)
    defaultToOAuthAuthentication: true
    diagnosticSettings: []
    enableSftp: false
    enableTelemetry: enableTelemetry
    fileServices: {
      corsRules: []
      diagnosticSettings: defaultAuditDiagnosticSettings
      protocolSettings: {
        smb: {
          authenticationMethods: 'Kerberos'
          channelEncryption: 'AES-256-GCM'
          kerberosTicketEncryption: 'AES-256'
          versions: 'SMB3.1.1'
        }
      }
      shareDeleteRetentionPolicy: {
        days: 13
        enabled: true
      }
      shares: []
    }
    isLocalUserEnabled: false
    keyType: 'Account'
    kind: 'StorageV2'
    location: location
    lock: {
      kind: lockKind
    }
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: customerManagedKeyUserAssignedResourceIds
    }
    minimumTlsVersion: 'TLS1_2'
    name: storage.private.name
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
      ipRules: []
      resourceAccessRules: []
      virtualNetworkRules: (forcePrivateNetworking
        ? []
        : map(
            union(
              [function.servicePlan.subnetResourceId],
              (storage.networking.?additionalSubnetResourceIds ?? [])
            ),
            subnetResourceId => {
              action: 'Allow'
              id: subnetResourceId
            }
          ))
    }
    privateEndpoints: (forcePrivateNetworking
      ? [
          {
            enableTelemetry: enableTelemetry
            name: 'pep-${storage.private.name}-blob-0'
            privateDnsZoneGroup: {
              privateDnsZoneGroupConfigs: [
                {
                  privateDnsZoneResourceId: storage.networking.blobPrivateDnsZoneResourceId
                }
              ]
            }
            privateLinkServiceConnectionName: '${storage.private.name}-blob-0'
            service: 'blob'
            subnetResourceId: storage.networking.privateEndpointSubnetResourceId
          }
          {
            enableTelemetry: enableTelemetry
            name: 'pep-${storage.private.name}-queue-0'
            privateDnsZoneGroup: {
              privateDnsZoneGroupConfigs: [
                {
                  privateDnsZoneResourceId: storage.networking.queuePrivateDnsZoneResourceId
                }
              ]
            }
            privateLinkServiceConnectionName: '${storage.private.name}-queue-0'
            service: 'queue'
            subnetResourceId: storage.networking.privateEndpointSubnetResourceId
          }
          {
            enableTelemetry: enableTelemetry
            name: 'pep-${storage.private.name}-table-0'
            privateDnsZoneGroup: {
              privateDnsZoneGroupConfigs: [
                {
                  privateDnsZoneResourceId: storage.networking.tablePrivateDnsZoneResourceId
                }
              ]
            }
            privateLinkServiceConnectionName: '${storage.private.name}-table-0'
            service: 'table'
            subnetResourceId: storage.networking.privateEndpointSubnetResourceId
          }
        ]
      : null)
    publicNetworkAccess: publicNetworkAccess
    queueServices: {
      corsRules: []
      diagnosticSettings: defaultAuditDiagnosticSettings
      queues: []
    }
    requireInfrastructureEncryption: true
    roleAssignments: [
      {
        principalId: functionApplication_userAssignedIdentity.outputs.principalId
        principalType: 'ServicePrincipal'
        roleDefinitionIdOrName: 'Storage Blob Data Owner'
      }
      {
        principalId: functionApplication_userAssignedIdentity.outputs.principalId
        principalType: 'ServicePrincipal'
        roleDefinitionIdOrName: 'Storage Queue Data Contributor'
      }
      {
        principalId: functionApplication_userAssignedIdentity.outputs.principalId
        principalType: 'ServicePrincipal'
        roleDefinitionIdOrName: 'Storage Table Data Contributor'
      }
    ]
    skuName: (enableZoneRedundancy ? 'Standard_RAGZRS' : 'Standard_RAGRS')
    supportsHttpsTrafficOnly: true
    tableServices: {
      corsRules: []
      diagnosticSettings: defaultAuditDiagnosticSettings
      tables: []
    }
    tags: storage.private.?tags
  }
}
module storageAccountPublic 'br/public:avm/res/storage/storage-account:0.32.0' = [for account in storage.public: {
  params: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    allowedCopyScope: 'PrivateLink'
    allowSharedKeyAccess: false
    azureFilesIdentityBasedAuthentication: {
      defaultSharePermission: 'None'
      directoryServiceOptions: 'None'
      smbOAuthSettings: {
        isSmbOAuthEnabled: true
      }
    }
    blobServices: {
      automaticSnapshotPolicyEnabled: false
      changeFeedEnabled: false
      changeFeedRetentionInDays: null
      containerDeleteRetentionPolicyAllowPermanentDelete: false
      containerDeleteRetentionPolicyDays: 13
      containerDeleteRetentionPolicyEnabled: true
      containers: [
        {
          name: frontDoor_userAssignedIdentity.properties.principalId
          publicAccess: 'None'
          roleAssignments: [
            {
              principalId: frontDoor_userAssignedIdentity.properties.principalId
              principalType: 'ServicePrincipal'
              roleDefinitionIdOrName: 'Storage Blob Data Reader'
            }
          ]
        }
        {
          name: '$web'
          publicAccess: 'None'
          roleAssignments: [
            {
              principalId: frontDoor_userAssignedIdentity.properties.principalId
              principalType: 'ServicePrincipal'
              roleDefinitionIdOrName: 'Storage Blob Data Reader'
            }
          ]
        }
      ]
      corsRules: storageAccountPublicCorsRules.blob
      deleteRetentionPolicyAllowPermanentDelete: false
      deleteRetentionPolicyDays: 13
      deleteRetentionPolicyEnabled: true
      diagnosticSettings: defaultAuditDiagnosticSettings
      isVersioningEnabled: true
      lastAccessTimeTrackingPolicyEnabled: true
      restorePolicyDays: null
      restorePolicyEnabled: false
      versionDeletePolicyDays: null
    }
    customerManagedKey: (enableCustomerManagedKey
      ? {
          autoRotationEnabled: true
          ...customerManagedKey!
        }
      : null)
    defaultToOAuthAuthentication: true
    diagnosticSettings: []
    enableSftp: false
    enableTelemetry: enableTelemetry
    fileServices: {
      corsRules: storageAccountPublicCorsRules.file
      diagnosticSettings: defaultAuditDiagnosticSettings
      protocolSettings: {
        smb: {
          authenticationMethods: 'Kerberos'
          channelEncryption: 'AES-256-GCM'
          kerberosTicketEncryption: 'AES-256'
          versions: 'SMB3.1.1'
        }
      }
      shareDeleteRetentionPolicy: {
        days: 13
        enabled: true
      }
      shares: []
    }
    isLocalUserEnabled: false
    keyType: 'Account'
    kind: 'StorageV2'
    location: location
    lock: {
      kind: lockKind
    }
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: customerManagedKeyUserAssignedResourceIds
    }
    minimumTlsVersion: 'TLS1_2'
    name: account.name
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Allow'
      ipRules: []
      resourceAccessRules: []
      virtualNetworkRules: []
    }
    privateEndpoints: []
    publicNetworkAccess: 'Enabled'
    queueServices: {
      corsRules: storageAccountPublicCorsRules.queue
      diagnosticSettings: defaultAuditDiagnosticSettings
      queues: []
    }
    requireInfrastructureEncryption: true
    skuName: (enableZoneRedundancy ? 'Standard_RAGZRS' : 'Standard_RAGRS')
    supportsHttpsTrafficOnly: true
    tableServices: {
      corsRules: storageAccountPublicCorsRules.table
      diagnosticSettings: defaultAuditDiagnosticSettings
      tables: []
    }
    tags: account.?tags
  }
}]

resource storageAccountPublic_blobEventsTopic 'Microsoft.EventGrid/systemTopics@2025-07-15-preview' = [for (account, index) in storage.public: {
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', function.userAssignedIdentity.name)}': {}
      '${resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', last(frontDoorUserAssignedIdentityResourceIdParts))}': {}
    }
  }
  location: location
  name: account.eventGrid.name
  properties: {
    source: storageAccountPublic[index].outputs.resourceId
    topicType: 'Microsoft.Storage.StorageAccounts'
  }
  tags: account.eventGrid.?tags
}]
resource storageAccountPublic_blobEventsSubscription 'Microsoft.EventGrid/systemTopics/eventSubscriptions@2025-07-15-preview' = [for (account, index) in storage.public: {
    parent: storageAccountPublic_blobEventsTopic[index]
    name: account.eventGrid.subscriptionName
    properties: {
      deadLetterWithResourceIdentity: {
        deadLetterDestination: {
          endpointType: 'StorageBlob'
          properties: {
            blobContainerName: deadLetterContainerName
            resourceId: storageAccountPrivate.outputs.resourceId
          }
        }
        identity: {
          type: 'UserAssigned'
          userAssignedIdentity: functionApplication_userAssignedIdentity.outputs.resourceId
        }
      }
      deliveryWithResourceIdentity: {
        destination: {
          endpointType: 'WebHook'
          properties: {
            azureActiveDirectoryApplicationIdOrUri: function.identityProviders.azureActiveDirectory.applicationRegistration.identifierUri
            azureActiveDirectoryTenantId: tenant().tenantId
            endpointUrl: 'https://${functionApplication.outputs.defaultHostname}/runtime/webhooks/EventGrid?functionName=${account.eventGrid.functionName}&code=${listKeys('${resourceId('Microsoft.Web/sites', function.name)}/host/default', '2025-03-01').systemKeys.eventgrid_extension}'
            maxEventsPerBatch: 1000
            minimumTlsVersionAllowed: '1.2'
            preferredBatchSizeInKilobytes: 1024
          }
        }
        identity: {
          type: 'UserAssigned'
          userAssignedIdentity: frontDoor_userAssignedIdentity.id
        }
      }
      eventDeliverySchema: 'CloudEventSchemaV1_0'
      filter: {
        advancedFilters: [
          {
            key: 'subject'
            operatorType: 'StringContains'
            values: ['/blobs/private/']
          }
        ]
        enableAdvancedFilteringOnArrays: true
        includedEventTypes: [
          'Microsoft.Storage.BlobCreated'
          'Microsoft.Storage.BlobDeleted'
          'Microsoft.Storage.BlobRenamed'
        ]
      }
      retryPolicy: {
        eventTimeToLiveInMinutes: 30
        maxDeliveryAttempts: 5
      }
    }
}]

output function {
  applicationInsights: {
    applicationId: string
    resourceId: string
  }
  defaultHostname: string
} = {
  applicationInsights: {
    applicationId: functionApplication_applicationInsights.outputs.applicationId
    resourceId: functionApplication_applicationInsights.outputs.resourceId
  }
  defaultHostname: functionApplication.outputs.defaultHostname
}
output publicStorage {
  primaryBlobEndpoint: string
}[] = [
  for (account, index) in storage.public: {
    primaryBlobEndpoint: storageAccountPublic[index].outputs.primaryBlobEndpoint
  }
]

