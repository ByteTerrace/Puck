extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0'
import { worldSiloConfigType } from 'ts/bvm:ptn_platform_world-silo:0.0.5'

@export()
type worldMcpType = {
  @maxLength(64)
  participants: { subject: string, grants: object[] }[]
  observations: object[]?
  testLocations: string[]?
}
param settings worldMcpType
param configuration worldSiloConfigType
param applicationUniqueName string
param applicationId string
param authorizationScope string
param identityClientId string
param identityPrincipalId string
param onboardingUrl string
param applicationInsightsName string
param location string = resourceGroup().location
param tags object = {}

var issuer = '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
var hostname = '${configuration.dns.recordName}.${configuration.dns.zoneName}'
resource trust 'Microsoft.Graph/applications/federatedIdentityCredentials@v1.0' = {
  name: '${applicationUniqueName}/${identityClientId}'
  audiences: ['api://AzureADTokenExchange']
  description: 'The existing World silo identity authenticates delegated OAuth exchanges for the MCP API.'
  issuer: issuer
  subject: identityPrincipalId
}
resource insights 'Microsoft.Insights/components@2020-02-02' existing = { name: applicationInsightsName }
resource availability 'Microsoft.Insights/webtests@2022-06-15' = {
  name: '${configuration.name}-mcp'
  location: location
  kind: 'standard'
  tags: union(tags, { 'hidden-link:${insights.id}': 'Resource' })
  properties: {
    Name: '${configuration.name}-mcp'
    SyntheticMonitorId: '${configuration.name}-mcp'
    Kind: 'standard'
    Enabled: true
    Frequency: 900
    Timeout: 30
    RetryEnabled: true
    Locations: map(settings.?testLocations ?? ['us-va-ash-azr', 'us-ca-sjc-azr'], id => { Id: id })
    Request: { RequestUrl: 'https://${hostname}/healthz', HttpVerb: 'GET', FollowRedirects: false, ParseDependentRequests: false }
    ValidationRules: { ExpectedHttpStatusCode: 200, SSLCheck: true, SSLCertRemainingLifetimeCheck: 7 }
  }
}
resource alert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${configuration.name}-mcp'
  location: 'global'
  tags: tags
  properties: {
    description: 'World MCP is unavailable or its automatically renewed TLS certificate has fewer than seven days remaining.'
    enabled: true
    severity: configuration.monitoring.severity
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [availability.id, insights.id]
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.WebtestLocationAvailabilityCriteria'
      webTestId: availability.id
      componentId: insights.id
      failedLocationCount: 1
    }
    actions: map(configuration.monitoring.actionGroupResourceIds, actionGroupId => { actionGroupId: actionGroupId })
  }
}
output worldMcpConfiguration object = {
  admission: map(settings.participants, participant => {
    domain: issuer
    subject: participant.subject
    mode: 'OAuth'
    algorithm: ''
    publicKey: ''
    disclosure: 'Replica'
    grants: participant.grants
  })
  options: {
    target: configuration.worldName
    publicUrl: 'https://${hostname}/mcp'
    listenUrl: 'http://127.0.0.1:8082'
    issuer: issuer
    audience: applicationId
    scope: 'user_impersonation'
    authorizationScope: authorizationScope
    subjectClaim: 'oid'
    tenantId: tenant().tenantId
    allowedSubjects: map(settings.participants, participant => participant.subject)
    services: {
      managedIdentityClientId: identityClientId
      onboardingUrl: onboardingUrl
      observations: settings.?observations ?? []
    }
  }
}
