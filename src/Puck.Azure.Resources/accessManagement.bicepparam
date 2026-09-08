using './accessManagement.bicep'
import {
  byteTerraceAccessManagementGroups
  byteTerraceAccessManagementRoleAssignments
} from './accessManagementDefaults.bicep'

var partitionCount = int(readEnvironmentVariable('BICEPPARAM_PARTITION_COUNT', '1'))
var prefix = 'bytrc'

param groups = byteTerraceAccessManagementGroups()
param roleAssignments = byteTerraceAccessManagementRoleAssignments(prefix, partitionCount)

