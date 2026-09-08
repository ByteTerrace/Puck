targetScope = 'resourceGroup'

@export()
type dnsZoneMapType = {
  cognitiveServices: {
    account: string
    openAi: string
    services: string
  }
  configurationStore: string
  containerEnvironment: string
  containerRegistry: string
  containerService: string
  cosmosDb: {
    sql: string
  }
  keyVault: string
  monitor: {
    agentService: string
    core: string
    insightsOds: string
    insightsOms: string
  }
  postgresFlexibleServer: string
  redisCache: string
  searchServices: string
  storageAccount: {
    blob: string
    dfs: string
    file: string
    queue: string
    table: string
    web: string
  }
  webApp: string
}

param locations string[] = [resourceGroup().location]
param tags { *: string } = {}
param lockKind ('CanNotDelete' | 'None' | 'ReadOnly') = 'CanNotDelete'
param virtualNetworkResourceIds string[] = []

var dnsZoneMap dnsZoneMapType = {
  cognitiveServices: {
    account: 'privatelink.cognitiveservices.azure.com'
    openAi: 'privatelink.openai.azure.com'
    services: 'privatelink.services.ai.azure.com'
  }
  configurationStore: 'privatelink.azconfig.io'
  containerEnvironment: 'privatelink.{0}.azurecontainerapps.io'
  containerRegistry: 'privatelink.azurecr.io'
  containerService: 'privatelink.{0}.azmk8s.io'
  cosmosDb: {
    sql: 'privatelink.documents.azure.com'
  }
  keyVault: 'privatelink.vaultcore.azure.net'
  monitor: {
    agentService: 'privatelink.agentsvc.azure-automation.net'
    core: 'privatelink.monitor.azure.com'
    insightsOds: 'privatelink.ods.opinsights.azure.com'
    insightsOms: 'privatelink.oms.opinsights.azure.com'
  }
  postgresFlexibleServer: 'privatelink.postgres.database.azure.com'
  redisCache: 'privatelink.redis.azure.net'
  searchServices: 'privatelink.search.windows.net'
  storageAccount: {
    blob: 'privatelink.blob.${environment().suffixes.storage}'
    dfs: 'privatelink.dfs.${environment().suffixes.storage}'
    file: 'privatelink.file.${environment().suffixes.storage}'
    queue: 'privatelink.queue.${environment().suffixes.storage}'
    table: 'privatelink.table.${environment().suffixes.storage}'
    web: 'privatelink.web.${environment().suffixes.storage}'
  }
  webApp: 'privatelink.azurewebsites.net'
}
var dnsZoneResourceIds = [for i in range(0, length(dnsZones)): privateDnsZones[i].id]
var dnsZones {
  category: string
  index: int
  subCategory: string?
  value: string
}[] = map(
  flatten(map(
    items(dnsZoneMap),
    category =>
      (contains(category.value, 'privatelink.')
        ? (contains(category.value, '{0}')
            ? map(locations, location => {
                category: category.key
                subCategory: null
                value: format(category.value, location)
              })
            : [
                {
                  category: category.key
                  subCategory: null
                  value: category.value
                }
              ])
        : map(items(category.value), subCategory => {
            category: category.key
            subCategory: subCategory.key
            value: subCategory.value
          }))
  )),
  (zone, index) => {
    ...zone
    index: index
  }
)
@onlyIfNotExists()
resource privateDnsZones 'Microsoft.Network/privateDnsZones@2024-06-01' = [
  for zone in dnsZones: {
    location: 'global'
    name: zone.value
    tags: tags
  }
]
resource privateDnsZones_lock 'Microsoft.Authorization/locks@2020-05-01' = [
  for zone in dnsZones: if ('None' != lockKind) {
    name: 'lock-${zone.value}'
    properties: {
      level: lockKind
      notes: ((lockKind == 'CanNotDelete')
        ? 'Cannot delete resource or child resources.'
        : 'Cannot delete or modify the resource or child resources.')
    }
    scope: privateDnsZones[zone.index]
  }
]
@onlyIfNotExists()
resource privateDnsZones_virtualNetworkLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [
  for entry in flatten(map(
    filter(dnsZones, zone => ('privatelink.monitor.azure.com' != zone.value)),
    zone =>
      map(virtualNetworkResourceIds, id => {
        id: id
        index: zone.index
      })
  )): {
    location: 'global'
    name: '${last(split(entry.id, '/'))}-${uniqueString(privateDnsZones[entry.index].id, entry.id)}'
    parent: privateDnsZones[entry.index]
    properties: {
      registrationEnabled: false
      resolutionPolicy: 'Default'
      virtualNetwork: {
        id: entry.id
      }
    }
  }
]

output dnsZoneMap dnsZoneMapType = reduce(dnsZones, {}, (result, zone) => {
  ...result
  '${zone.category}': (empty(zone.?subCategory)
    ? dnsZoneResourceIds[zone.index]
    : {
        ...(result[?zone.category] ?? {})
        '${zone.subCategory!}': dnsZoneResourceIds[zone.index]
      })
})

