param storageAccountName string
param functionHostStorageAccountName string
param pendingContainerName string
param cleanContainerName string
param quarantineContainerName string
param statusTableName string
param uploadAdmissionTableName string
param webPrincipalId string
param processorPrincipalId string
param managementPrincipalId string
param hostPrincipalId string
param eventGridTopicPrincipalId string

var storageBlobDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var storageBlobDataOwnerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
var storageAccountContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '17d1049b-9a84-46fb-8f53-869881c3d3ab')
var storageQueueDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
var storageTableDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-01-01' existing = {
  name: storageAccountName
}

resource functionHostStorageAccount 'Microsoft.Storage/storageAccounts@2025-01-01' existing = {
  name: functionHostStorageAccountName
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' existing = {
  parent: storageAccount
  name: 'default'
}

resource pendingContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' existing = {
  parent: blobService
  name: pendingContainerName
}

resource cleanContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' existing = {
  parent: blobService
  name: cleanContainerName
}

resource quarantineContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' existing = {
  parent: blobService
  name: quarantineContainerName
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2025-01-01' existing = {
  parent: storageAccount
  name: 'default'
}

resource statusTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2025-01-01' existing = {
  parent: tableService
  name: statusTableName
}

resource uploadAdmissionTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2025-01-01' existing = {
  parent: tableService
  name: uploadAdmissionTableName
}

resource hostCleanRole 'Microsoft.Authorization/roleDefinitions@2022-05-01-preview' = {
  name: guid(subscription().id, 'secure-upload-clean-read-list-delete')
  properties: {
    roleName: 'Secure Upload Clean Blob Reader and Deleter'
    description: 'Read, list, and delete clean blobs without creating or overwriting content.'
    type: 'CustomRole'
    assignableScopes: [
      cleanContainer.id
    ]
    permissions: [
      {
        actions: []
        notActions: []
        dataActions: [
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete'
        ]
        notDataActions: [
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write'
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/add/action'
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/move/action'
        ]
      }
    ]
  }
}

resource webPendingRoleDefinition 'Microsoft.Authorization/roleDefinitions@2022-05-01-preview' = {
  name: guid(subscription().id, 'secure-upload-pending-writer')
  properties: {
    roleName: 'Secure Upload Pending Blob Writer'
    description: 'Create, commit, and delete pending blobs without reading blob content.'
    type: 'CustomRole'
    assignableScopes: [
      pendingContainer.id
    ]
    permissions: [
      {
        actions: []
        notActions: []
        dataActions: [
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write'
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/add/action'
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete'
        ]
        notDataActions: [
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'
        ]
      }
    ]
  }
}

resource managementStatusRoleDefinition 'Microsoft.Authorization/roleDefinitions@2022-05-01-preview' = {
  name: guid(subscription().id, 'secure-upload-management-status-read-update')
  properties: {
    roleName: 'Secure Upload Management Status Table Reader and Updater'
    description: 'Read and update existing file status entities without creating or deleting rows.'
    type: 'CustomRole'
    assignableScopes: [
      statusTable.id
    ]
    permissions: [
      {
        actions: []
        notActions: []
        dataActions: [
          'Microsoft.Storage/storageAccounts/tableServices/tables/entities/read'
          'Microsoft.Storage/storageAccounts/tableServices/tables/entities/update/action'
        ]
        notDataActions: [
          'Microsoft.Storage/storageAccounts/tableServices/tables/entities/add/action'
          'Microsoft.Storage/storageAccounts/tableServices/tables/entities/delete'
        ]
      }
    ]
  }
}

resource managementCleanRoleDefinition 'Microsoft.Authorization/roleDefinitions@2022-05-01-preview' = {
  name: guid(subscription().id, 'secure-upload-management-clean-read')
  properties: {
    roleName: 'Secure Upload Management Clean Blob Reader'
    description: 'Read clean blobs only without write, add, move, or delete permissions.'
    type: 'CustomRole'
    assignableScopes: [
      cleanContainer.id
    ]
    permissions: [
      {
        actions: []
        notActions: []
        dataActions: [
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'
        ]
        notDataActions: [
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write'
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/add/action'
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/move/action'
          'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete'
        ]
      }
    ]
  }
}

resource webPendingRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(pendingContainer.id, webPrincipalId, webPendingRoleDefinition.id)
  scope: pendingContainer
  properties: {
    principalId: webPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: webPendingRoleDefinition.id
  }
}

resource webStatusRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(statusTable.id, webPrincipalId, storageTableDataContributorRoleId)
  scope: statusTable
  properties: {
    principalId: webPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageTableDataContributorRoleId
  }
}

resource webUploadAdmissionRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(uploadAdmissionTable.id, webPrincipalId, storageTableDataContributorRoleId)
  scope: uploadAdmissionTable
  properties: {
    principalId: webPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageTableDataContributorRoleId
  }
}

resource processorPendingRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(pendingContainer.id, processorPrincipalId, storageBlobDataContributorRoleId)
  scope: pendingContainer
  properties: {
    principalId: processorPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataContributorRoleId
  }
}

resource processorCleanRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(cleanContainer.id, processorPrincipalId, storageBlobDataContributorRoleId)
  scope: cleanContainer
  properties: {
    principalId: processorPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataContributorRoleId
  }
}

resource processorQuarantineRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(quarantineContainer.id, processorPrincipalId, storageBlobDataContributorRoleId)
  scope: quarantineContainer
  properties: {
    principalId: processorPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataContributorRoleId
  }
}

resource processorStatusRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(statusTable.id, processorPrincipalId, storageTableDataContributorRoleId)
  scope: statusTable
  properties: {
    principalId: processorPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageTableDataContributorRoleId
  }
}

resource managementStatusRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(statusTable.id, managementPrincipalId, managementStatusRoleDefinition.id)
  scope: statusTable
  properties: {
    principalId: managementPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: managementStatusRoleDefinition.id
  }
}

resource managementCleanRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(cleanContainer.id, managementPrincipalId, managementCleanRoleDefinition.id)
  scope: cleanContainer
  properties: {
    principalId: managementPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: managementCleanRoleDefinition.id
  }
}

resource functionHostBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(functionHostStorageAccount.id, processorPrincipalId, storageBlobDataOwnerRoleId)
  scope: functionHostStorageAccount
  properties: {
    principalId: processorPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataOwnerRoleId
  }
}

resource functionHostTableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(functionHostStorageAccount.id, processorPrincipalId, storageTableDataContributorRoleId)
  scope: functionHostStorageAccount
  properties: {
    principalId: processorPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageTableDataContributorRoleId
  }
}

resource functionHostQueueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(functionHostStorageAccount.id, processorPrincipalId, storageQueueDataContributorRoleId)
  scope: functionHostStorageAccount
  properties: {
    principalId: processorPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageQueueDataContributorRoleId
  }
}

resource functionHostAccountRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(functionHostStorageAccount.id, processorPrincipalId, storageAccountContributorRoleId)
  scope: functionHostStorageAccount
  properties: {
    principalId: processorPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageAccountContributorRoleId
  }
}

resource hostCleanAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(cleanContainer.id, hostPrincipalId, hostCleanRole.id)
  scope: cleanContainer
  properties: {
    principalId: hostPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: hostCleanRole.id
  }
}

resource eventGridDeadLetterRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, eventGridTopicPrincipalId, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    principalId: eventGridTopicPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataContributorRoleId
  }
}

output hostCleanRoleDefinitionId string = hostCleanRole.id
output managementStatusRoleDefinitionId string = managementStatusRoleDefinition.id
output managementCleanRoleDefinitionId string = managementCleanRoleDefinition.id
output managementAccessPosture object = {
  scopes: [
    statusTable.id
    cleanContainer.id
  ]
  statusTableReadUpdateOnly: true
  statusTableDataActions: [
    'Microsoft.Storage/storageAccounts/tableServices/tables/entities/read'
    'Microsoft.Storage/storageAccounts/tableServices/tables/entities/update/action'
  ]
  statusTableNotDataActions: [
    'Microsoft.Storage/storageAccounts/tableServices/tables/entities/add/action'
    'Microsoft.Storage/storageAccounts/tableServices/tables/entities/delete'
  ]
  cleanBlobReadOnly: true
  cleanBlobDataActions: [
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'
  ]
  cleanBlobNotDataActions: [
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write'
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/add/action'
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/move/action'
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete'
  ]
  pendingBlobAccess: false
  quarantineBlobAccess: false
  uploadAdmissionTableAccess: false
  functionHostStorageAccess: false
  eventGridAccess: false
}
output rbacPosture object = {
  webScopes: [
    pendingContainer.id
    statusTable.id
    uploadAdmissionTable.id
  ]
  webPendingDataActions: [
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write'
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/add/action'
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete'
  ]
  webPendingReadAllowed: false
  processorScopes: [
    pendingContainer.id
    cleanContainer.id
    quarantineContainer.id
    statusTable.id
    functionHostStorageAccount.id
  ]
  hostScopes: [
    cleanContainer.id
  ]
  hostDataActions: [
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete'
  ]
  hostWriteAllowed: false
  managementScopes: [
    statusTable.id
    cleanContainer.id
  ]
  managementStatusTableDataActions: [
    'Microsoft.Storage/storageAccounts/tableServices/tables/entities/read'
    'Microsoft.Storage/storageAccounts/tableServices/tables/entities/update/action'
  ]
  managementStatusTableNotDataActions: [
    'Microsoft.Storage/storageAccounts/tableServices/tables/entities/add/action'
    'Microsoft.Storage/storageAccounts/tableServices/tables/entities/delete'
  ]
  managementCleanDataActions: [
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'
  ]
  managementCleanNotDataActions: [
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write'
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/add/action'
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/move/action'
    'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete'
  ]
  managementPendingAccess: false
  managementQuarantineAccess: false
  managementUploadAdmissionAccess: false
  managementFunctionHostAccess: false
  managementEventGridAccess: false
}
