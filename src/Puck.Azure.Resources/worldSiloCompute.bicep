// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Imports
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
import { worldSiloConfigType } from 'ts/bvm:ptn_platform_world-silo:0.0.6'

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Parameters
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
param configuration worldSiloConfigType
param identityResourceId string
@secure()
param bootstrapCommand string
param release string
param sshPublicKey string
@description('Expose the silo OAuth-protected MCP TLS listener. Its configuration and certificate arrive through the existing protected bootstrap settings.')
param mcpEnabled bool = false
param location string = resourceGroup().location
param tags { *: string } = {}

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Variables
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
var poolId = resourceId(
  'Microsoft.Network/loadBalancers/backendAddressPools',
  configuration.network.loadBalancerName,
  'world'
)
var frontendId = resourceId(
  'Microsoft.Network/loadBalancers/frontendIPConfigurations',
  configuration.network.loadBalancerName,
  'world'
)
var probeId = resourceId('Microsoft.Network/loadBalancers/probes', configuration.network.loadBalancerName, 'world')
var spot = configuration.compute.?spot

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Resources
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
resource network 'Microsoft.Network/virtualNetworks@2025-01-01' existing = {
  name: configuration.network.virtualNetworkName
}
resource nat 'Microsoft.Network/natGateways@2025-01-01' existing = { name: configuration.network.natGatewayName }
resource security 'Microsoft.Network/networkSecurityGroups@2025-01-01' = {
  name: configuration.network.securityGroupName
  location: location
  tags: union(tags, configuration.?tags ?? {})
  properties: {
    securityRules: [
      {
        name: 'WorldQuic'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Udp'
          sourcePortRange: '*'
          destinationPortRange: string(configuration.port)
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
      {
        name: 'WorldHealth'
        properties: {
          priority: 110
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: string(configuration.lifecycle.healthPort)
          sourceAddressPrefix: 'AzureLoadBalancer'
          destinationAddressPrefix: '*'
        }
      }
      ...(mcpEnabled
        ? [
            {
              name: 'WorldMcp'
              properties: {
                priority: 120
                direction: 'Inbound'
                access: 'Allow'
                protocol: 'Tcp'
                sourcePortRange: '*'
                destinationPortRange: '8443'
                sourceAddressPrefix: '*'
                destinationAddressPrefix: '*'
              }
            }
          ]
        : [])
      {
        name: 'DenyOtherInbound'
        properties: {
          priority: 200
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
    ]
  }
}
resource subnet 'Microsoft.Network/virtualNetworks/subnets@2025-01-01' = {
  parent: network
  name: configuration.network.subnetName
  properties: {
    addressPrefix: configuration.network.subnetPrefix
    defaultOutboundAccess: false
    natGateway: { id: nat.id }
    networkSecurityGroup: { id: security.id }
    privateEndpointNetworkPolicies: 'Disabled'
    privateLinkServiceNetworkPolicies: 'Enabled'
  }
}
resource publicIp 'Microsoft.Network/publicIPAddresses@2025-01-01' = {
  name: configuration.network.publicIpName
  location: location
  tags: union(tags, configuration.?tags ?? {})
  sku: { name: 'Standard', tier: 'Regional' }
  properties: { publicIPAllocationMethod: 'Static', publicIPAddressVersion: 'IPv4' }
}
resource balancer 'Microsoft.Network/loadBalancers@2025-01-01' = {
  name: configuration.network.loadBalancerName
  location: location
  tags: union(tags, configuration.?tags ?? {})
  sku: { name: 'Standard', tier: 'Regional' }
  properties: {
    frontendIPConfigurations: [{ name: 'world', properties: { publicIPAddress: { id: publicIp.id } } }]
    backendAddressPools: [{ name: 'world' }]
    probes: [
      {
        name: 'world'
        properties: {
          protocol: 'Http'
          port: configuration.lifecycle.healthPort
          requestPath: '/healthz'
          intervalInSeconds: 5
          numberOfProbes: 2
        }
      }
    ]
    loadBalancingRules: [
      {
        name: 'WorldQuic'
        properties: {
          frontendIPConfiguration: { id: frontendId }
          backendAddressPool: { id: poolId }
          probe: { id: probeId }
          protocol: 'Udp'
          frontendPort: configuration.port
          backendPort: configuration.port
          disableOutboundSnat: true
        }
      }
      ...(mcpEnabled
        ? [
            {
              name: 'WorldMcp'
              properties: {
                frontendIPConfiguration: { id: frontendId }
                backendAddressPool: { id: poolId }
                probe: { id: probeId }
                protocol: 'Tcp'
                frontendPort: 443
                backendPort: 8443
                disableOutboundSnat: true
                enableTcpReset: true
                idleTimeoutInMinutes: 4
              }
            }
          ]
        : [])
    ]
  }
}
// Declared directly: the AVM scale-set module exposes neither Delete eviction nor Try & Restore.
// Azure refuses to change priority in place; the world rollout recreates the scale set after its drain.
resource workers 'Microsoft.Compute/virtualMachineScaleSets@2025-04-01' = {
  dependsOn: [balancer]
  name: configuration.name
  location: location
  tags: union(tags, configuration.?tags ?? {})
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identityResourceId}': {} } }
  zones: map(configuration.compute.zones, zone => string(zone))
  sku: { name: configuration.compute.sku, capacity: configuration.compute.capacity }
  properties: {
    orchestrationMode: 'Flexible'
    platformFaultDomainCount: 1
    singlePlacementGroup: false
    upgradePolicy: { mode: 'Manual', automaticOSUpgradePolicy: { enableAutomaticOSUpgrade: false } }
    automaticRepairsPolicy: {
      enabled: configuration.compute.automaticRepairs
      gracePeriod: configuration.compute.repairGracePeriod
    }
    // Checkpoints and journals live in blob storage, so an evicted worker's disk holds nothing to keep.
    spotRestorePolicy: (spot == null) ? null : { enabled: true, restoreTimeout: spot!.restoreTimeout }
    virtualMachineProfile: {
      priority: (spot == null) ? 'Regular' : 'Spot'
      evictionPolicy: (spot == null) ? null : 'Delete'
      // -1 evicts only when Azure reclaims capacity, never on price, and never bills above the regular rate.
      billingProfile: (spot == null) ? null : { maxPrice: -1 }
      osProfile: {
        computerNamePrefix: configuration.compute.vmNamePrefix
        adminUsername: configuration.compute.adminUsername
        linuxConfiguration: {
          disablePasswordAuthentication: true
          provisionVMAgent: true
          ssh: {
            publicKeys: [
              { path: '/home/${configuration.compute.adminUsername}/.ssh/authorized_keys', keyData: sshPublicKey }
            ]
          }
          patchSettings: {
            patchMode: configuration.compute.patchMode
            assessmentMode: configuration.compute.patchAssessmentMode
            automaticByPlatformSettings: (configuration.compute.patchMode == 'AutomaticByPlatform')
              ? { bypassPlatformSafetyChecksOnUserSchedule: false, rebootSetting: 'IfRequired' }
              : null
          }
        }
      }
      securityProfile: { encryptionAtHost: true }
      storageProfile: {
        imageReference: configuration.compute.imageReference
        osDisk: {
          createOption: 'FromImage'
          deleteOption: 'Delete'
          diskSizeGB: configuration.compute.osDiskSizeGB
          managedDisk: { storageAccountType: configuration.compute.osDiskStorageType }
        }
      }
      networkProfile: {
        networkApiVersion: '2020-11-01'
        networkInterfaceConfigurations: [
          {
            name: configuration.compute.vmNamePrefix
            properties: {
              primary: true
              deleteOption: 'Delete'
              enableAcceleratedNetworking: true
              ipConfigurations: [
                {
                  name: 'primary'
                  properties: {
                    primary: true
                    subnet: { id: subnet.id }
                    loadBalancerBackendAddressPools: [{ id: poolId }]
                  }
                }
              ]
            }
          }
        ]
      }
      diagnosticsProfile: { bootDiagnostics: { enabled: true } }
      extensionProfile: {
        extensions: [
          {
            name: 'HealthExtension'
            properties: {
              publisher: 'Microsoft.ManagedServices'
              type: 'ApplicationHealthLinux'
              typeHandlerVersion: '2.0'
              autoUpgradeMinorVersion: false
              settings: {
                protocol: 'http'
                port: configuration.lifecycle.healthPort
                requestPath: '/livez/azure'
                intervalInSeconds: 5
                numberOfProbes: 1
                gracePeriod: 5
              }
            }
          }
          {
            name: 'CustomScriptExtension'
            properties: {
              publisher: 'Microsoft.Azure.Extensions'
              type: 'CustomScript'
              typeHandlerVersion: '2.1'
              autoUpgradeMinorVersion: true
              enableAutomaticUpgrade: false
              forceUpdateTag: take(last(split(release, ':')), 40)
              suppressFailures: false
              protectedSettings: { commandToExecute: bootstrapCommand }
            }
          }
        ]
      }
      // Terminate covers scale-in and deletion; a Spot Preempt always gives 30 seconds regardless.
      scheduledEventsProfile: { terminateNotificationProfile: { enable: true, notBeforeTimeout: 'PT5M' } }
    }
  }
}
resource readinessAlert 'Microsoft.Insights/metricAlerts@2026-01-01' = {
  name: configuration.monitoring.alertName
  location: 'global'
  tags: union(tags, configuration.?tags ?? {})
  properties: {
    description: 'The public world has no healthy load-balancer backend.'
    severity: configuration.monitoring.severity
    enabled: true
    scopes: [balancer.id]
    evaluationFrequency: configuration.monitoring.evaluationFrequency
    windowSize: configuration.monitoring.windowSize
    autoMitigate: true
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'WorldReadiness'
          metricNamespace: 'Microsoft.Network/loadBalancers'
          metricName: 'DipAvailability'
          operator: 'LessThan'
          threshold: 1
          timeAggregation: 'Average'
          criterionType: 'StaticThresholdCriterion'
        }
      ]
    }
    // Action group IDs arrive as configured resource IDs; the rule cannot follow loop items.
    #disable-next-line use-resource-id-functions
    actions: [for actionGroupId in configuration.monitoring.actionGroupResourceIds: { actionGroupId: actionGroupId }]
  }
}
resource zone 'Microsoft.Network/dnsZones@2018-05-01' existing = { name: configuration.dns.zoneName }
resource record 'Microsoft.Network/dnsZones/A@2018-05-01' = {
  parent: zone
  name: configuration.dns.recordName
  properties: { TTL: configuration.dns.ttl, ARecords: [{ ipv4Address: publicIp.properties.ipAddress }] }
}

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Outputs
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
output endpoint string = '${configuration.dns.recordName}.${configuration.dns.zoneName}:${configuration.port}'
output scaleSetResourceId string = workers.id
