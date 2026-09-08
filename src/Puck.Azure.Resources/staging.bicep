// Application staging reuses the platform's network, registry, and runtime identities.
// Configuration labels, Orleans service IDs, and grain-state containers are separate.
param location string = resourceGroup().location
param actorsImage string
param siloImage string
param dashboardImage string
param revision string
param prefix string = 'bytrc'

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: '${prefix}caep000'
}
resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: '${prefix}crp000'
}
resource actorIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${prefix}idp007'
}
resource functionIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${prefix}idp002'
}
resource assertionIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${prefix}idp000'
}
resource ciIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: '${prefix}idpzzz'
}
resource privateStorage 'Microsoft.Storage/storageAccounts@2025-01-01' existing = {
  name: '${prefix}stp000'
}
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' existing = {
  parent: privateStorage
  name: 'default'
}
resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobService
  name: 'functions-staging-deployment'
  properties: { publicAccess: 'None' }
}
resource natPrefix 'Microsoft.Network/publicIPPrefixes@2024-05-01' existing = {
  name: '${prefix}ipprep000'
}

var registryCredentials = [{ server: registry.properties.loginServer, identity: actorIdentity.id }]
var actorEnvironment = [
  { name: 'AZURE_CLIENT_ID', value: actorIdentity.properties.clientId }
  { name: 'ConfigurationStore__Endpoint', value: 'https://${prefix}appcsp000.azconfig.io' }
  { name: 'ConfigurationStore__Label', value: 'staging' }
  { name: 'PrivateStorage__BlobEndpoint', value: privateStorage.properties.primaryEndpoints.blob }
  { name: 'PrivateStorage__TableEndpoint', value: privateStorage.properties.primaryEndpoints.table }
  { name: 'Orleans__ClusterId', value: 'puck-staging' }
  { name: 'Orleans__ServiceId', value: 'actors-staging' }
  { name: 'Orleans__GrainStateContainerName', value: 'orleans-state-staging' }
  { name: 'DataProtection__ApplicationName', value: functionIdentity.properties.clientId }
  { name: 'DataProtection__BlobUri', value: '${privateStorage.properties.primaryEndpoints.blob}data-protection/staging-keys.xml' }
  { name: 'DataProtection__KeyUri', value: 'https://${prefix}kvp000${environment().suffixes.keyvaultDns}/keys/DataProtection' }
  { name: 'OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID', value: actorIdentity.properties.clientId }
  { name: 'Authorization__JwtBearer__TokenValidationParameters__ValidAudiences__0', value: 'https://api.byteterrace.com' }
]

resource actors 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${prefix}actors-staging'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${actorIdentity.id}': {} } }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: registryCredentials
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
        ipSecurityRestrictions: [{ name: 'platform-egress', action: 'Allow', ipAddressRange: natPrefix.properties.ipPrefix }]
      }
    }
    template: {
      revisionSuffix: revision
      containers: [{
        name: 'actors'
        image: actorsImage
        env: actorEnvironment
        resources: { cpu: json('0.5'), memory: '1Gi' }
        probes: [for kind in ['Startup', 'Readiness', 'Liveness']: {
          type: kind
          httpGet: { path: '/healthz', port: 8080 }
          initialDelaySeconds: 10
          periodSeconds: 10
          failureThreshold: 30
        }]
      }]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${prefix}functions-staging'
  location: location
  kind: 'functionapp'
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  properties: { reserved: true }
}
resource functions 'Microsoft.Web/sites@2024-04-01' = {
  name: '${prefix}functions-staging'
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${functionIdentity.id}': {}, '${assertionIdentity.id}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    virtualNetworkSubnetId: resourceId('Microsoft.Network/virtualNetworks/subnets', '${prefix}vnetp000', '${prefix}snetp001')
    vnetRouteAllEnabled: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${privateStorage.properties.primaryEndpoints.blob}${deploymentContainer.name}'
          authentication: { type: 'UserAssignedIdentity', userAssignedIdentityResourceId: functionIdentity.id }
        }
      }
      runtime: { name: 'dotnet-isolated', version: '10.0' }
      scaleAndConcurrency: { maximumInstanceCount: 40, instanceMemoryMB: 512 }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'AZURE_CLIENT_ID', value: functionIdentity.properties.clientId }
        { name: 'AzureWebJobsStorage__accountName', value: privateStorage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: functionIdentity.properties.clientId }
        { name: 'ConfigurationStore__Endpoint', value: 'https://${prefix}appcsp000.azconfig.io' }
        { name: 'ConfigurationStore__Label', value: 'staging' }
        { name: 'DataProtection__BlobUri', value: '${privateStorage.properties.primaryEndpoints.blob}data-protection/staging-keys.xml' }
        { name: 'DataProtection__KeyUri', value: 'https://${prefix}kvp000${environment().suffixes.keyvaultDns}/keys/DataProtection' }
        { name: 'RedisCache__Configuration', value: '${prefix}amrp000.${location}.redis.azure.net:10000' }
        { name: 'OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID', value: assertionIdentity.properties.clientId }
      ]
    }
  }
}
resource authentication 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: functions
  name: 'authsettingsV2'
  properties: {
    platform: { enabled: true }
    globalValidation: { requireAuthentication: true, unauthenticatedClientAction: 'Return401' }
    httpSettings: { requireHttps: true }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: 'e6a7ab9f-19af-4eb0-b23f-a5bde0f90eb7'
          openIdIssuer: '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: ['https://api.byteterrace.com', 'e6a7ab9f-19af-4eb0-b23f-a5bde0f90eb7']
          defaultAuthorizationPolicy: {
            allowedApplications: ['e6a7ab9f-19af-4eb0-b23f-a5bde0f90eb7', ciIdentity.properties.clientId]
          }
        }
      }
    }
  }
}

// A bootable authority node, with no tenant worlds admitted by this infrastructure template.
// A populated silo document and per-world federation keys are workload inputs, not image contents.
resource silo 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${prefix}silo-staging'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${actorIdentity.id}': {} } }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: registryCredentials
      secrets: [{ name: 'silo-document', value: string({
        schema: 'puck.silo.def.v1'
        worlds: []
        doors: { budget: 0 }
        store: { kind: 'Azure', accountUrl: privateStorage.properties.primaryEndpoints.blob }
        stateDir: '/state'
        clustering: { kind: 'Localhost' }
      }) }]
    }
    template: {
      revisionSuffix: revision
      containers: [{
        name: 'silo'
        image: siloImage
        env: [{ name: 'AZURE_CLIENT_ID', value: actorIdentity.properties.clientId }]
        resources: { cpu: json('0.5'), memory: '1Gi' }
        volumeMounts: [{ volumeName: 'configuration', mountPath: '/configuration' }, { volumeName: 'state', mountPath: '/state' }]
      }]
      volumes: [
        { name: 'configuration', storageType: 'Secret', secrets: [{ secretRef: 'silo-document', path: 'silo.json' }] }
        { name: 'state', storageType: 'EmptyDir' }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource dashboard 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${prefix}dashboard-staging'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${actorIdentity.id}': {} } }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: registryCredentials
      ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false }
    }
    template: {
      revisionSuffix: revision
      containers: [{
        name: 'dashboard'
        image: dashboardImage
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
        env: [{ name: 'FUNCTION_HOST', value: functions.properties.defaultHostName }]
        probes: [{ type: 'Readiness', httpGet: { path: '/release.json', port: 8080 } }]
      }]
      scale: { minReplicas: 0, maxReplicas: 1 }
    }
  }
}
output actorsEndpoint string = 'https://${actors.properties.configuration.ingress.fqdn}'
output functionAppName string = functions.name
output functionEndpoint string = 'https://${functions.properties.defaultHostName}/api'
output dashboardEndpoint string = 'https://${dashboard.properties.configuration.ingress.fqdn}'
output siloAppName string = silo.name
