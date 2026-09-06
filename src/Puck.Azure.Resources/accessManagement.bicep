extension 'br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0'

@export()
type groupType = {
  name: string
  uniqueName: string?
}
@export()
type roleAssignmentType = {
  condition: string?
  description: string?
  groupName: string?
  principalId: string?
  principalType: ('Device' | 'ForeignGroup' | 'Group' | 'ServicePrincipal' | 'User')?
  resourceGroupName: string?
  resourceId: string?
  resourcePath: string?
  resourceProvider: string?
  roleDefinitionName: string
  subscriptionId: string?
  userAssignedIdentityId: string?
}

param groups groupType[] = []
param roleAssignments roleAssignmentType[] = []

// Note: The { '': 0 } hack found multiple times below is to work around this issue: https://github.com/Azure/bicep/issues/3990.
var groupsMap { *: int } = { ...{ '': 0 }, ...toObject(range(0, length(groups)), i => groups[i].name, i => i) }
var roleAssignmentResourceIds = map(
  map(roleAssignments, assignment => {
    resourceGroupName: (assignment.?resourceGroupName ?? resourceGroup().name)
    resourceId: assignment.?resourceId
    resourcePathParts: split(assignment.?resourcePath ?? '', '/')
    resourceProviderParts: split(assignment.?resourceProvider ?? '', '/')
    subscriptionId: (assignment.?subscriptionId ?? subscription().subscriptionId)
  }),
  assignment =>
    (assignment.?resourceId ?? '/subscriptions/${assignment.subscriptionId}/resourceGroups/${assignment.resourceGroupName}/providers/${first(assignment.resourceProviderParts)}/${join(map(skip(assignment.resourceProviderParts, 1), (s, i) => '${s}/${assignment.resourcePathParts[i]}'), '/')}')
)
var userAssignedIdentityMap = {
  ...{ '': 0 }
  ...toObject(
    map(
      union(
        [],
        map(
          filter(roleAssignments, assignment => contains(assignment, 'userAssignedIdentityId')),
          assignment => assignment.userAssignedIdentityId!
        )
      ),
      (id, index) => {
        index: index
        userAssignedIdentityId: id
      }
    ),
    a => a.userAssignedIdentityId,
    a => a.index
  )
}

resource groupsResource 'Microsoft.Graph/groups@v1.0' existing = [
  for group in groups: {
    uniqueName: (group.?uniqueName ?? guid(tenant().tenantId, group.name))
  }
]
module roleAssignmentsModule './avm-temp/resource-role-assignment/main.bicep' = [
  for (assignment, index) in roleAssignments: {
    params: {
      condition: assignment.?condition
      description: assignment.?description
      enableTelemetry: false
      name: guid(
        roleAssignmentResourceIds[index],
        (assignment.?principalId ?? (contains(assignment, 'userAssignedIdentityId')
          ? userAssignedIdentities[userAssignedIdentityMap[?assignment.?userAssignedIdentityId ?? '']].properties.principalId
          : groupsResource[groupsMap[?assignment.?groupName ?? '']].id)),
        az.roleDefinitions(assignment.roleDefinitionName).id
      )
      principalId: (assignment.?principalId ?? (contains(assignment, 'userAssignedIdentityId')
        ? userAssignedIdentities[userAssignedIdentityMap[?assignment.?userAssignedIdentityId ?? '']].properties.principalId
        : groupsResource[groupsMap[?assignment.?groupName ?? '']].id))
      principalType: (assignment.?principalType ?? (contains(assignment, 'userAssignedIdentityId')
        ? 'ServicePrincipal'
        : 'Group'))
      resourceId: roleAssignmentResourceIds[index]
      roleDefinitionId: az.roleDefinitions(assignment.roleDefinitionName).id
    }
  }
]
resource userAssignedIdentities 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = [
  for identity in filter(items(userAssignedIdentityMap), item => !empty(item.key)): {
    name: last(split(identity.key, '/'))
    scope: resourceGroup(
      (split(identity.key, '/')[?2] ?? subscription().subscriptionId),
      (split(identity.key, '/')[?4] ?? resourceGroup().name)
    )
  }
]

