// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Imports
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
import { worldSiloConfigType } from 'ts/bvm:ptn_platform_world-silo:0.0.4'

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
// Resources
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
resource network 'Microsoft.Network/virtualNetworks@2025-01-01' existing = { name: configuration.network.virtualNetworkName }
resource nat 'Microsoft.Network/natGateways@2025-01-01' existing = { name: configuration.network.natGatewayName }
resource security 'Microsoft.Network/networkSecurityGroups@2025-01-01' = {
  name: configuration.network.securityGroupName
  location: location
  tags: union(tags, configuration.?tags ?? {})
  properties: {
    securityRules: [
      { name: 'WorldQuic', properties: { priority: 100, direction: 'Inbound', access: 'Allow', protocol: 'Udp', sourcePortRange: '*', destinationPortRange: string(configuration.port), sourceAddressPrefix: '*', destinationAddressPrefix: '*' } }
      { name: 'WorldHealth', properties: { priority: 110, direction: 'Inbound', access: 'Allow', protocol: 'Tcp', sourcePortRange: '*', destinationPortRange: string(configuration.lifecycle.healthPort), sourceAddressPrefix: 'AzureLoadBalancer', destinationAddressPrefix: '*' } }
      ...(mcpEnabled ? [{ name: 'WorldMcp', properties: { priority: 120, direction: 'Inbound', access: 'Allow', protocol: 'Tcp', sourcePortRange: '*', destinationPortRange: '8443', sourceAddressPrefix: '*', destinationAddressPrefix: '*' } }] : [])
      { name: 'DenyOtherInbound', properties: { priority: 200, direction: 'Inbound', access: 'Deny', protocol: '*', sourcePortRange: '*', destinationPortRange: '*', sourceAddressPrefix: '*', destinationAddressPrefix: '*' } }
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
var poolId = resourceId('Microsoft.Network/loadBalancers/backendAddressPools', configuration.network.loadBalancerName, 'world')
var frontendId = resourceId('Microsoft.Network/loadBalancers/frontendIPConfigurations', configuration.network.loadBalancerName, 'world')
var probeId = resourceId('Microsoft.Network/loadBalancers/probes', configuration.network.loadBalancerName, 'world')
resource balancer 'Microsoft.Network/loadBalancers@2025-01-01' = {
  name: configuration.network.loadBalancerName
  location: location
  tags: union(tags, configuration.?tags ?? {})
  sku: { name: 'Standard', tier: 'Regional' }
  properties: {
    frontendIPConfigurations: [{ name: 'world', properties: { publicIPAddress: { id: publicIp.id } } }]
    backendAddressPools: [{ name: 'world' }]
    probes: [{ name: 'world', properties: { protocol: 'Http', port: configuration.lifecycle.healthPort, requestPath: '/healthz', intervalInSeconds: 5, numberOfProbes: 2 } }]
    loadBalancingRules: [
      { name: 'WorldQuic', properties: { frontendIPConfiguration: { id: frontendId }, backendAddressPool: { id: poolId }, probe: { id: probeId }, protocol: 'Udp', frontendPort: configuration.port, backendPort: configuration.port, disableOutboundSnat: true } }
      ...(mcpEnabled ? [{ name: 'WorldMcp', properties: { frontendIPConfiguration: { id: frontendId }, backendAddressPool: { id: poolId }, probe: { id: probeId }, protocol: 'Tcp', frontendPort: 443, backendPort: 8443, disableOutboundSnat: true, enableTcpReset: true, idleTimeoutInMinutes: 4 } }] : [])
    ]
  }
}
module workers 'br/public:avm/res/compute/virtual-machine-scale-set:0.11.1' = {
  dependsOn: [balancer]
  params: {
    name: configuration.name
    location: location
    tags: union(tags, configuration.?tags ?? {})
    adminUsername: configuration.compute.adminUsername
    adminPassword: ''
    disablePasswordAuthentication: true
    publicKeys: [{ path: '/home/${configuration.compute.adminUsername}/.ssh/authorized_keys', keyData: sshPublicKey }]
    imageReference: configuration.compute.imageReference
    osType: 'Linux'
    osDisk: { createOption: 'FromImage', diskSizeGB: configuration.compute.osDiskSizeGB, managedDisk: { storageAccountType: configuration.compute.osDiskStorageType } }
    skuName: configuration.compute.sku
    skuCapacity: configuration.compute.capacity
    vmNamePrefix: configuration.compute.vmNamePrefix
    availabilityZones: configuration.compute.zones
    orchestrationMode: 'Flexible'
    upgradePolicyMode: 'Manual'
    overprovision: false
    automaticRepairsPolicyEnabled: configuration.compute.automaticRepairs
    gracePeriod: configuration.compute.repairGracePeriod
    enableAutomaticOSUpgrade: false
    patchMode: configuration.compute.patchMode
    patchAssessmentMode: configuration.compute.patchAssessmentMode
    managedIdentities: { userAssignedResourceIds: [identityResourceId] }
    bootDiagnosticEnabled: true
    nicConfigurations: [{ name: configuration.compute.vmNamePrefix, ipConfigurations: [{ name: 'primary', properties: { primary: true, subnet: { id: subnet.id }, loadBalancerBackendAddressPools: [{ id: poolId }] } }] }]
    extensionHealthConfig: { enabled: true, protocol: 'http', port: configuration.lifecycle.healthPort, requestPath: '/livez' }
    extensionCustomScriptConfig: { forceUpdateTag: take(last(split(release, ':')), 40), protectedSettings: { commandToExecute: bootstrapCommand } }
    scheduledEventsProfile: { terminateNotificationProfile: { enable: true, notBeforeTimeout: 'PT5M' } }
  }
}
resource readinessAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
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
      allOf: [{ name: 'WorldReadiness', metricNamespace: 'Microsoft.Network/loadBalancers', metricName: 'DipAvailability', operator: 'LessThan', threshold: 1, timeAggregation: 'Average', criterionType: 'StaticThresholdCriterion' }]
    }
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
output scaleSetResourceId string = workers.outputs.resourceId
