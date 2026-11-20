// The Log Analytics workspace, written once so `main.bicep` can deploy it twice. The workspace's default data
// collection rule (its workspace transformation) has the workspace as its destination, so the rule cannot exist
// before the workspace and the workspace cannot name the rule in the same step. `main.bicep` deploys this module
// without a rule, then the rule, then this module again with `defaultDataCollectionRuleResourceId` set.

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Parameters
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// The most data the workspace ingests in a day, in GB; -1 means no cap.
param dailyQuotaGb int
param defaultDataCollectionRuleResourceId string?
param enableTelemetry bool
param location string
param lockKind ('CanNotDelete' | 'None' | 'ReadOnly')
param name string
param tags { *: string }

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Resources
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
module workspace 'br/public:avm/res/operational-insights/workspace:0.16.1' = {
  params: {
    dailyQuotaGb: string(dailyQuotaGb)
    dataRetention: 30
    defaultDataCollectionRuleResourceId: defaultDataCollectionRuleResourceId
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
    name: name
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    roleAssignments: []
    skuName: 'PerGB2018'
    tags: tags
  }
}

// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
// Outputs
// ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
output logAnalyticsWorkspaceId string = workspace.outputs.logAnalyticsWorkspaceId
output resourceId string = workspace.outputs.resourceId
