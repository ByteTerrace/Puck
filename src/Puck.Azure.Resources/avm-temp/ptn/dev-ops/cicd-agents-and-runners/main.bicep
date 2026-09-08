import {
  agentProfileType
  imageType
} from 'br/public:avm/res/dev-ops-infrastructure/pool:0.8.0'

type tagsType = { *: string }

param devCenter {
  name: string
  tags: tagsType?
}
param devCenterProject {
  name: string
  tags: tagsType?
}
param devOpsAgentPool {
  agentProfile: agentProfileType
  concurrency: int
  fabricProfileSkuName: string
  images: imageType[]
  name: string
  organizationName: string
  projectName: string
  subnetResourceId: string
  tags: tagsType?
}
param enableTelemetry bool = false
param gitHubNetworkSettings {
  name: string
  businessId: string
  subnetId: string
}
param location string = resourceGroup().location
param lockKind string = 'CanNotDelete'
param logAnalyticsWorkspaceResourceId string

module devCenter_mod 'br/public:avm/res/dev-center/devcenter:0.2.0' = {
  params: {
    diagnosticSettings: [
      {
        logCategoriesAndGroups: [
          {
            categoryGroup: 'audit'
          }
        ]
        workspaceResourceId: logAnalyticsWorkspaceResourceId
      }
    ]
    enableTelemetry: enableTelemetry
    location: location
    lock: {
      kind: lockKind
    }
    name: devCenter.name
    roleAssignments: []
    tags: devCenter.?tags
  }
}
module devCenter_project_mod 'br/public:avm/res/dev-center/project:0.1.2' = {
  params: {
    devCenterResourceId: devCenter_mod.outputs.resourceId
    enableTelemetry: enableTelemetry
    location: location
    lock: {
      kind: lockKind
    }
    name: devCenterProject.name
    roleAssignments: []
    tags: devCenterProject.?tags
  }
}
module devOpsAgentPool_mod 'br/public:avm/res/dev-ops-infrastructure/pool:0.8.0' = {
  params: {
    agentProfile: devOpsAgentPool.agentProfile
    concurrency: devOpsAgentPool.concurrency
    devCenterProjectResourceId: devCenter_project_mod.outputs.resourceId
    diagnosticSettings: [
      {
        logCategoriesAndGroups: [
          {
            category: 'ProvisioningLogs'
          }
        ]
        workspaceResourceId: logAnalyticsWorkspaceResourceId
      }
    ]
    enableTelemetry: enableTelemetry
    fabricProfileSkuName: devOpsAgentPool.fabricProfileSkuName
    images: devOpsAgentPool.images
    location: location
    lock: {
      kind: lockKind
    }
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: []
    }
    name: devOpsAgentPool.name
    organizationProfile: {
      kind: 'AzureDevOps'
      organizations: [
        {
          openAccess: false
          parallelism: devOpsAgentPool.concurrency
          projects: [devOpsAgentPool.projectName]
          url: 'https://dev.azure.com/${devOpsAgentPool.organizationName}'
        }
      ]
      permissionProfile: {
        kind: 'CreatorOnly'
      }
    }
    osProfile: {
      logonType: 'Interactive'
    }
    roleAssignments: []
    subnetResourceId: devOpsAgentPool.subnetResourceId
    tags: devOpsAgentPool.?tags
  }
}
resource githubNetworkSettings 'GitHub.Network/networkSettings@2024-04-02' = {
  location: location
  name: gitHubNetworkSettings.name
  properties: {
    businessId: gitHubNetworkSettings.businessId
    subnetId: gitHubNetworkSettings.subnetId
  }
}

