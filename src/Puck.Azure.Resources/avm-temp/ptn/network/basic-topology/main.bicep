import {
  diagnosticSettingFullType
  roleAssignmentType
} from 'br/public:avm/utl/types/avm-common-types:0.7.0'

import {
  dnsZoneMapType
} from 'ts/bvm:ptn_network_private-dns-zones:0.0.1'

@export()
type subnetType = {
  addressPrefixes: string[]
  defaultOutboundAccess: bool
  delegation: string?
  name: string
  natGatewayResourceId: string?
  privateEndpointNetworkPolicies: ('Disabled' | 'Enabled')?
  privateLinkServiceNetworkPolicies: ('Disabled' | 'Enabled')?
  roleAssignments: roleAssignmentType[]?
  serviceEndpoints: string[]?
}
type tagsType = { *: string }
@export()
type virtualNetworkType = {
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
  }
  tags: tagsType?
}

param devOpsInfrastructureServicePrincipalId string
param enableTelemetry bool = false
param location string = resourceGroup().location
param lockKind string = 'CanNotDelete'
param natGateway {
  name: string
  publicIpPrefix: {
    name: string
    tags: tagsType?
  }
  tags: tagsType?
}
param publicDnsZones string[]
param virtualNetwork virtualNetworkType

module natGateway_mod 'br/public:avm/res/network/nat-gateway:2.0.1' = {
  params: {
    availabilityZone: -1
    enableTelemetry: enableTelemetry
    location: location
    lock: {
      kind: lockKind
    }
    name: natGateway.name
    publicIPPrefixResourceIds: [natGateway_publicIpPrefix_mod.outputs.resourceId]
    roleAssignments: []
    tags: natGateway.?tags
  }
}
module natGateway_publicIpPrefix_mod 'br/public:avm/res/network/public-ip-prefix:0.8.0' = {
  params: {
    availabilityZones: [
      1
      2
      3
    ]
    enableTelemetry: enableTelemetry
    ipTags: []
    location: location
    lock: {
      kind: lockKind
    }
    name: natGateway.publicIpPrefix.name
    prefixLength: 31
    publicIPAddressVersion: 'IPv4'
    roleAssignments: []
    skuName: 'Standard'
    tags: natGateway.publicIpPrefix.?tags
    tier: 'Regional'
  }
}
module networkSecurityGroups_mod 'br/public:avm/res/network/network-security-group:0.5.3' = [
  for subnet in items(virtualNetwork.subnets): {
    params: {
      diagnosticSettings: []
      enableTelemetry: enableTelemetry
      flushConnection: false
      location: location
      lock: {
        kind: lockKind
      }
      name: replace(subnet.value.name, 'snet', 'nsg')
      roleAssignments: []
      securityRules: []
    }
  }
]
module privateEndpointDnsZones_mod 'ts/bvm:ptn_network_private-dns-zones:0.0.1' = {
  params: {
    lockKind: lockKind
    virtualNetworkResourceIds: [virtualNetwork_mod.outputs.resourceId]
  }
}
module publicDnsZones_mod 'ts/bvm:ptn_network_public-dns-zones:0.0.1' = {
  params: {
    lockKind: lockKind
    zones: toObject(publicDnsZones, zone => zone, zone => {})
  }
}
module virtualNetwork_mod 'br/public:avm/res/network/virtual-network:0.7.2' = {
  params: {
    addressPrefixes: virtualNetwork.addressPrefixes
    diagnosticSettings: virtualNetwork.?diagnosticSettings
    dnsServers: virtualNetwork.?dnsServers
    enableTelemetry: enableTelemetry
    enableVmProtection: true
    location: location
    lock: {
      kind: 'None' // NOTE: Lock is not set in order to allow subnet delegation modifications (example: Azure Managed DevOps Pools).
    }
    name: virtualNetwork.name
    peerings: []
    roleAssignments: (0 != length(filter(
        items(virtualNetwork.subnets),
        subnet => ('microsoft.devopsinfrastructure/pools' == toLower(subnet.value.?delegation ?? ''))
      ))
      ? [
          {
            principalId: devOpsInfrastructureServicePrincipalId
            principalType: 'ServicePrincipal'
            roleDefinitionIdOrName: 'Reader'
          }
        ]
      : [])
    subnets: [
      for (subnet, index) in items(virtualNetwork.subnets): {
        ...subnet.value
        natGatewayResourceId: (contains(subnet.value, 'natGatewayResourceId')
          ? (contains(subnet.value.natGatewayResourceId!, '/')
              ? subnet.value.natGatewayResourceId!
              : natGateway_mod.outputs.resourceId)
          : null)
        networkSecurityGroupResourceId: networkSecurityGroups_mod[index].outputs.resourceId
        roleAssignments: [
          ...(subnet.value.?roleAssignments ?? [])
          ...(('microsoft.devopsinfrastructure/pools' == toLower(subnet.value.?delegation ?? ''))
            ? [
                {
                  principalId: devOpsInfrastructureServicePrincipalId
                  principalType: 'ServicePrincipal'
                  roleDefinitionIdOrName: 'Network Contributor'
                }
              ]
            : [])
        ]
      }
    ]
    vnetEncryption: false
    vnetEncryptionEnforcement: null
    tags: virtualNetwork.?tags
  }
}

output location string = location
output privateDnsZoneMap dnsZoneMapType = privateEndpointDnsZones_mod.outputs.dnsZoneMap
output publicDnsZoneMap { *: string } = publicDnsZones_mod.outputs.dnsZoneMap
output subnetResourceIds string[] = virtualNetwork_mod.outputs.subnetResourceIds
output virtualNetworkName string = virtualNetwork.name

