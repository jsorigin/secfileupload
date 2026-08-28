param location string
param storageAccountName string
param functionHostStorageAccountName string
param storageSkuName string
param pendingContainerName string
param cleanContainerName string
param quarantineContainerName string
param deadLetterContainerName string
param statusTableName string
param uploadAdmissionTableName string
param quarantineRetentionDays int
param tags object = {}

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-01-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: storageSkuName
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    isHnsEnabled: false
    isNfsV3Enabled: false
    isSftpEnabled: false
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Disabled'
    supportsHttpsTrafficOnly: true
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
    encryption: {
      keySource: 'Microsoft.Storage'
      requireInfrastructureEncryption: true
      services: {
        blob: {
          enabled: true
          keyType: 'Account'
        }
        file: {
          enabled: true
          keyType: 'Account'
        }
        queue: {
          enabled: true
          keyType: 'Service'
        }
        table: {
          enabled: true
          keyType: 'Service'
        }
      }

    }
  }
}

resource functionHostStorageAccount 'Microsoft.Storage/storageAccounts@2025-01-01' = {
  name: functionHostStorageAccountName
  location: location
  tags: tags
  sku: {
    name: storageSkuName
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Disabled'
    supportsHttpsTrafficOnly: true
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
    encryption: {
      keySource: 'Microsoft.Storage'
      requireInfrastructureEncryption: true
      services: {
        blob: {
          enabled: true
          keyType: 'Account'
        }
        file: {
          enabled: true
          keyType: 'Account'
        }
        queue: {
          enabled: true
          keyType: 'Service'
        }
        table: {
          enabled: true
          keyType: 'Service'
        }
      }
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: false
    }
    isVersioningEnabled: false
  }
}

resource pendingContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobService
  name: pendingContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource cleanContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobService
  name: cleanContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource quarantineContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobService
  name: quarantineContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource deadLetterContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = {
  parent: blobService
  name: deadLetterContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2025-01-01' = {
  parent: storageAccount
  name: 'default'
}

resource statusTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2025-01-01' = {
  parent: tableService
  name: statusTableName
}

resource uploadAdmissionTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2025-01-01' = {
  parent: tableService
  name: uploadAdmissionTableName
}

resource managementPolicy 'Microsoft.Storage/storageAccounts/managementPolicies@2025-01-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'delete-quarantine'
          enabled: true
          type: 'Lifecycle'
          definition: {
            actions: {
              baseBlob: {
                delete: {
                  daysAfterModificationGreaterThan: quarantineRetentionDays
                }
              }
            }
            filters: {
              blobTypes: [
                'blockBlob'
              ]
              prefixMatch: [
                '${quarantineContainerName}/'
              ]
            }
          }
        }
      ]
    }
  }
}

output storageAccountId string = storageAccount.id
output functionHostStorageAccountId string = functionHostStorageAccount.id
output storageAccountName string = storageAccount.name
output functionHostStorageAccountName string = functionHostStorageAccount.name
output functionHostBlobServiceUri string = functionHostStorageAccount.properties.primaryEndpoints.blob
output functionHostQueueServiceUri string = functionHostStorageAccount.properties.primaryEndpoints.queue
output functionHostTableServiceUri string = functionHostStorageAccount.properties.primaryEndpoints.table
output blobServiceUri string = storageAccount.properties.primaryEndpoints.blob
output queueServiceUri string = storageAccount.properties.primaryEndpoints.queue
output tableServiceUri string = storageAccount.properties.primaryEndpoints.table
output pendingContainerId string = pendingContainer.id
output cleanContainerId string = cleanContainer.id
output quarantineContainerId string = quarantineContainer.id
output deadLetterContainerId string = deadLetterContainer.id
output statusTableId string = statusTable.id
output uploadAdmissionTableId string = uploadAdmissionTable.id
output securityPosture object = {
  allowBlobPublicAccess: false
  allowSharedKeyAccess: false
  supportsHttpsTrafficOnly: true
  minimumTlsVersion: 'TLS1_2'
  containerPublicAccess: 'None'
  quarantineLifecyclePrefix: '${quarantineContainerName}/'
}
