param topicName string
param functionAppName string
param functionName string
param storageAccountName string
param deadLetterContainerName string
param maxDeliveryAttempts int
param eventTimeToLiveInMinutes int
param enableEventSubscription bool
param actionGroupId string

resource topic 'Microsoft.EventGrid/topics@2025-02-15' existing = {
  name: topicName
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-01-01' existing = {
  name: storageAccountName
}

resource functionApp 'Microsoft.Web/sites@2025-03-01' existing = {
  name: functionAppName
}

resource eventSubscription 'Microsoft.EventGrid/topics/eventSubscriptions@2025-02-15' = if (enableEventSubscription) {
  parent: topic
  name: 'defender-scan-results'
  properties: {
    destination: {
      endpointType: 'AzureFunction'
      properties: {
        resourceId: '${functionApp.id}/functions/${functionName}'
        maxEventsPerBatch: 1
        preferredBatchSizeInKilobytes: 64
      }
    }
    deadLetterWithResourceIdentity: {
      identity: {
        type: 'SystemAssigned'
      }
      deadLetterDestination: {
        endpointType: 'StorageBlob'
        properties: {
          resourceId: storageAccount.id
          blobContainerName: deadLetterContainerName
        }
      }
    }
    eventDeliverySchema: 'EventGridSchema'
    filter: {
      includedEventTypes: [
        'Microsoft.Security.MalwareScanningResult'
      ]
      isSubjectCaseSensitive: false
    }
    retryPolicy: {
      maxDeliveryAttempts: maxDeliveryAttempts
      eventTimeToLiveInMinutes: eventTimeToLiveInMinutes
    }
  }
}

resource deadLetterAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = if (enableEventSubscription) {
  name: '${topicName}-dead-lettered'
  location: 'global'
  properties: {
    description: 'Alerts whenever a Defender scan result is dead-lettered.'
    severity: 1
    enabled: true
    scopes: [
      topic.id
    ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'DeadLetteredEvents'
          criterionType: 'StaticThresholdCriterion'
          metricNamespace: 'Microsoft.EventGrid/topics'
          metricName: 'DeadLetteredCount'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Total'
        }
      ]
    }
    actions: [
      {
        actionGroupId: actionGroupId
      }
    ]
    autoMitigate: true
  }
}

output eventSubscriptionId string = enableEventSubscription ? eventSubscription.id : ''
output eventDeliveryPosture object = {
  enabled: enableEventSubscription
  schema: 'EventGridSchema'
  includedEventType: 'Microsoft.Security.MalwareScanningResult'
  maxDeliveryAttempts: maxDeliveryAttempts
  eventTimeToLiveInMinutes: eventTimeToLiveInMinutes
  deadLetterContainerName: deadLetterContainerName
}
