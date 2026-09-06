using './main.bicep'

param applicationInsights = {
  name: 'bytrcappip003'
}
param cognitiveServices = {
  deployments: [
    {
      model: {
        format: 'OpenAI'
        name: 'gpt-4.1-nano'
      }
      raiPolicyName: 'Microsoft.DefaultV2'
      sku: {
        name: 'GlobalStandard'
      }
      versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
    }
    {
      model: {
        format: 'OpenAI'
        name: 'o4-mini'
      }
      raiPolicyName: 'Microsoft.DefaultV2'
      sku: {
        name: 'GlobalStandard'
      }
      versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
    }
  ]
  name: 'bytrcaifp001'
  networking: {
    agentSubnetResourceId: '/subscriptions/fd49ea67-135b-449f-a62c-3e4b8d26d3d6/resourceGroups/byteterrace/providers/Microsoft.Network/virtualNetworks/bytrcvnetp000/subnets/bytrcsnetp007'
    aiServicesPrivateDnsZoneResourceId: '/subscriptions/fd49ea67-135b-449f-a62c-3e4b8d26d3d6/resourceGroups/byteterrace/providers/Microsoft.Network/privateDnsZones/privatelink.services.ai.azure.com'
    cognitiveServicesPrivateDnsZoneResourceId: '/subscriptions/fd49ea67-135b-449f-a62c-3e4b8d26d3d6/resourceGroups/byteterrace/providers/Microsoft.Network/privateDnsZones/privatelink.cognitiveservices.azure.com'
    openAiPrivateDnsZoneResourceId: '/subscriptions/fd49ea67-135b-449f-a62c-3e4b8d26d3d6/resourceGroups/byteterrace/providers/Microsoft.Network/privateDnsZones/privatelink.openai.azure.com'
    privateEndpointSubnetResourceId: '/subscriptions/fd49ea67-135b-449f-a62c-3e4b8d26d3d6/resourceGroups/byteterrace/providers/Microsoft.Network/virtualNetworks/bytrcvnetp000/subnets/bytrcsnetp000'
  }
}
param cosmosDb = {
  name: 'bytrccosnop000'
  networking: {
    privateDnsZoneResourceId: '/subscriptions/fd49ea67-135b-449f-a62c-3e4b8d26d3d6/resourceGroups/byteterrace/providers/Microsoft.Network/privateDnsZones/privatelink.documents.azure.com'
    privateEndpointSubnetResourceId: '/subscriptions/fd49ea67-135b-449f-a62c-3e4b8d26d3d6/resourceGroups/byteterrace/providers/Microsoft.Network/virtualNetworks/bytrcvnetp000/subnets/bytrcsnetp000'
  }
}
param enableTelemetry = false
param enableZoneRedundancy = false
param forcePrivateNetworking = false
param lockKind = 'None'
param logAnalyticsWorkspaceResourceId = '/subscriptions/fd49ea67-135b-449f-a62c-3e4b8d26d3d6/resourcegroups/byteterrace/providers/microsoft.operationalinsights/workspaces/bytrclogp000'

