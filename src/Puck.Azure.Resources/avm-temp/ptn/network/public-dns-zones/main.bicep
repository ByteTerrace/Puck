@export()
type publicDnsZoneType = {
  enableDnssec: bool?
}

param lockKind ('CanNotDelete' | 'None' | 'ReadOnly') = 'CanNotDelete'
param zones { *: publicDnsZoneType }

var digestAlgorithmType = 2
var keySigningKeyFlags = 257
var zoneKeys = objectKeys(zones)
var zoneResourceIds = [for i in range(0, length(zoneKeys)): publicDnsZones[i].id]
var zonesWithMetadata {
  enableDnssec: bool
  index: int
  name: string
  parentIndex: int
  value: string
}[] = [
  for (key, index) in zoneKeys: {
    enableDnssec: (zones[key].?enableDnssec ?? true)
    index: index
    name: first(split(key, '.'))
    parentIndex: indexOf(zoneKeys, join(skip(split(key, '.'), 1), '.'))
    value: key
  }
]

@onlyIfNotExists()
resource publicDnsZones 'Microsoft.Network/dnsZones@2018-05-01' = [
  for zone in zonesWithMetadata: {
    location: 'global'
    name: zone.value
  }
]
@onlyIfNotExists()
resource publicDnsZones_dnssecConfigs 'Microsoft.Network/dnsZones/dnssecConfigs@2023-07-01-preview' = [
  for zone in zonesWithMetadata: if (zone.enableDnssec) {
    name: 'default'
    parent: publicDnsZones[zone.index]
  }
]
resource publicDnsZones_lock 'Microsoft.Authorization/locks@2020-05-01' = [
  for zone in zonesWithMetadata: if ('None' != lockKind) {
    name: 'lock-${zone.value}'
    properties: {
      level: lockKind
      notes: ((lockKind == 'CanNotDelete')
        ? 'Cannot delete resource or child resources.'
        : 'Cannot delete or modify the resource or child resources.')
    }
    scope: publicDnsZones[zone.index]
  }
]
resource publicDnsZones_parentDsRecord 'Microsoft.Network/dnsZones/DS@2023-07-01-preview' = [
  for zone in zonesWithMetadata: if (zone.enableDnssec && (-1 < zone.parentIndex)) {
    name: zone.name
    parent: publicDnsZones[zone.parentIndex]
    properties: {
      DSRecords: map(
        filter(
          publicDnsZones_dnssecConfigs[zone.index]!.properties.signingKeys,
          key => (keySigningKeyFlags == key.flags)
        ),
        key => {
          algorithm: key.securityAlgorithmType
          digest: first(map(
            filter(key.delegationSignerInfo, info => (digestAlgorithmType == info.digestAlgorithmType)),
            info => {
              algorithmType: info.digestAlgorithmType
              value: info.digestValue
            }
          ))
          keyTag: key.keyTag
        }
      )
      TTL: 3600
    }
  }
]
resource publicDnsZones_parentNsRecord 'Microsoft.Network/dnsZones/NS@2023-07-01-preview' = [
  for zone in zonesWithMetadata: if (-1 < zone.parentIndex) {
    name: zone.name
    parent: publicDnsZones[zone.parentIndex]
    properties: {
      NSRecords: map(publicDnsZones[zone.index].properties.nameServers, nameServer => {
        nsdname: nameServer
      })
      TTL: 3600
    }
  }
]

output dnsZoneMap { *: string } = toObject(zonesWithMetadata, zone => zone.value, zone => zoneResourceIds[zone.index])

