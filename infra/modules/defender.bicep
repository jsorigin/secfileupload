param location string
param storageAccountName string
param eventGridTopicName string
param defenderMonthlyGbCap int
param cleanContainerName string
param quarantineContainerName string
param workspaceId string
param tags object = {}

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-01-01' existing = {
  name: storageAccountName
}

resource topic 'Microsoft.EventGrid/topics@2025-02-15' = {
  name: eventGridTopicName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    inputSchema: 'EventGridSchema'
    publicNetworkAccess: 'Enabled'
  }
}

resource defenderSettings 'Microsoft.Security/defenderForStorageSettings@2025-06-01' = {
  name: 'current'
  scope: storageAccount
  properties: {
    isEnabled: true
    overrideSubscriptionLevelSettings: true
    malwareScanning: {
      blobScanResultsOptions: 'None'
      onUpload: {
        isEnabled: true
        capGBPerMonth: defenderMonthlyGbCap
        filters: {
          excludeBlobsWithPrefix: [
            '${cleanContainerName}/'
            '${quarantineContainerName}/'
          ]
        }
      }
      scanResultsEventGridTopicResourceId: topic.id
    }
    sensitiveDataDiscovery: {
      isEnabled: false
    }
  }
}

resource scanResultDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'service'
  scope: defenderSettings
  properties: {
    workspaceId: workspaceId
    logs: [
      {
        category: 'ScanResults'
        enabled: true
      }
    ]
  }
}

output topicId string = topic.id
output topicPrincipalId string = topic.identity.principalId
output defenderSettingsId string = defenderSettings.id
output defenderPosture object = {
  apiVersion: '2025-06-01'
  inputSchema: 'EventGridSchema'
  publicNetworkAccess: 'Enabled'
  capGBPerMonth: defenderMonthlyGbCap
  excludedPrefixes: [
    '${cleanContainerName}/'
    '${quarantineContainerName}/'
  ]
  blobScanResultsOptions: 'None'
}
