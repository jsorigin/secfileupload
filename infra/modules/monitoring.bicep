param location string
param logAnalyticsWorkspaceName string
param applicationInsightsName string
param actionGroupName string
param alertEmailReceivers array
param globalBytesPerWindow int
param defenderMonthlyBytesCap int
param managementAppName string
param processorAppName string
param tags object = {}

resource workspace 'Microsoft.OperationalInsights/workspaces@2025-07-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  tags: tags
  properties: {
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    retentionInDays: 30
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: applicationInsightsName
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    DisableLocalAuth: true
    IngestionMode: 'LogAnalytics'
    WorkspaceResourceId: workspace.id
  }
}

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: actionGroupName
  location: 'global'
  tags: tags
  properties: {
    enabled: true
    groupShortName: take(actionGroupName, 12)
    emailReceivers: [
      for receiver in alertEmailReceivers: {
        name: receiver.name
        emailAddress: receiver.emailAddress
        useCommonAlertSchema: true
      }
    ]
  }
}

resource scanErrorAlert 'Microsoft.Insights/scheduledQueryRules@2025-01-01-preview' = {
  name: '${applicationInsightsName}-scan-errors'
  location: location
  tags: tags
  properties: {
    displayName: 'Secure upload scan errors'
    description: 'Alerts when Defender or the processor reports a scan error.'
    enabled: true
    severity: 1
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [
      workspace.id
    ]
    criteria: {
      allOf: [
        {
          query: 'union isfuzzy=true StorageMalwareScanningResults, AppMetrics | where TimeGenerated > ago(15m) | where (Name == "secure_upload.scan.outcome" and tostring(Properties["secure_upload.outcome"]) == "scanerrorrecorded") or tostring(ResultType) =~ "Error"'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Count'
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    skipQueryValidation: true
  }
}

resource scanIntegrityAlert 'Microsoft.Insights/scheduledQueryRules@2025-01-01-preview' = {
  name: '${applicationInsightsName}-scan-integrity'
  location: location
  tags: tags
  properties: {
    displayName: 'Secure upload scan integrity signals'
    description: 'Alerts on stale pending records, invalid scan events, and terminal conflicts.'
    enabled: true
    severity: 1
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [
      workspace.id
    ]
    criteria: {
      allOf: [
        {
          query: 'AppMetrics | where TimeGenerated > ago(15m) | where Name in ("secure_upload.scan.stale_pending", "secure_upload.scan.invalid_event", "secure_upload.scan.terminal_conflict")'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Count'
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    skipQueryValidation: true
  }
}

resource processingRetryAlert 'Microsoft.Insights/scheduledQueryRules@2025-01-01-preview' = {
  name: '${applicationInsightsName}-processing-retries'
  location: location
  tags: tags
  properties: {
    displayName: 'Secure upload repeated processor retries'
    description: 'Alerts when scan processing retry volume indicates an Event Grid delivery may exhaust its budget.'
    enabled: true
    severity: 2
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [
      workspace.id
    ]
    criteria: {
      allOf: [
        {
          query: 'AppMetrics | where TimeGenerated > ago(15m) and Name == "secure_upload.scan.processing_retry" | summarize RetryCount=sum(Sum) | where RetryCount >= 5'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Count'
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    skipQueryValidation: true
  }
}

resource uploadSafetyAlert 'Microsoft.Insights/scheduledQueryRules@2025-01-01-preview' = {
  name: '${applicationInsightsName}-upload-safety'
  location: location
  tags: tags
  properties: {
    displayName: 'Secure upload admission safety controls'
    description: 'Alerts on kill-switch activation and sustained request, byte, concurrency, or Defender-cap rejection.'
    enabled: true
    severity: 2
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [
      workspace.id
    ]
    criteria: {
      allOf: [
        {
          query: 'AppMetrics | where TimeGenerated > ago(15m) | where Name == "secure_upload.upload.kill_switch" or (Name == "secure_upload.upload.rate_limited" and tostring(Properties["secure_upload.reason"]) in ("request-budget", "byte-budget", "concurrency", "defender-cap")) | summarize Rejections=sum(Sum) by Name | where Name == "secure_upload.upload.kill_switch" or Rejections >= 10'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Count'
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    skipQueryValidation: true
  }
}

resource capacityProximityAlert 'Microsoft.Insights/scheduledQueryRules@2025-01-01-preview' = {
  name: '${applicationInsightsName}-capacity-proximity'
  location: location
  tags: tags
  properties: {
    displayName: 'Secure upload byte budget or Defender cap proximity'
    description: 'Alerts when accepted bytes approach the configured rolling byte budget or monthly Defender admission cap.'
    enabled: true
    severity: 2
    evaluationFrequency: 'PT15M'
    windowSize: 'PT1H'
    scopes: [
      workspace.id
    ]
    criteria: {
      allOf: [
        {
          query: 'let WindowBytes = toscalar(AppMetrics | where TimeGenerated > ago(15m) and Name == "secure_upload.upload.bytes" | summarize sum(Sum)); let MonthBytes = toscalar(AppMetrics | where TimeGenerated > ago(31d) and Name == "secure_upload.upload.bytes" | summarize sum(Sum)); print WindowRatio=WindowBytes / todouble(${globalBytesPerWindow}), MonthRatio=MonthBytes / todouble(${defenderMonthlyBytesCap}) | where WindowRatio >= 0.8 or MonthRatio >= 0.8'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Count'
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    skipQueryValidation: true
  }
}

resource scanLagAlert 'Microsoft.Insights/scheduledQueryRules@2025-01-01-preview' = {
  name: '${applicationInsightsName}-scan-lag'
  location: location
  tags: tags
  properties: {
    displayName: 'Secure upload scan lag'
    description: 'Alerts when recorded scan latency or oldest stale pending age exceeds three hours.'
    enabled: true
    severity: 2
    evaluationFrequency: 'PT15M'
    windowSize: 'PT4H'
    scopes: [
      workspace.id
    ]
    criteria: {
      allOf: [
        {
          query: 'AppMetrics | where TimeGenerated > ago(4h) and Name in ("secure_upload.scan.latency", "secure_upload.scan.oldest_pending_age") | summarize Maximum=max(Max) by Name | where Maximum >= 10800'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Count'
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    skipQueryValidation: true
  }
}

resource missingScanActivityAlert 'Microsoft.Insights/scheduledQueryRules@2025-01-01-preview' = {
  name: '${applicationInsightsName}-missing-scan-activity'
  location: location
  tags: tags
  properties: {
    displayName: 'Secure upload missing scan activity'
    description: 'Alerts when uploads were accepted but no scan outcome telemetry arrived during the four-hour window.'
    enabled: true
    severity: 1
    evaluationFrequency: 'PT30M'
    windowSize: 'PT4H'
    scopes: [
      workspace.id
    ]
    criteria: {
      allOf: [
        {
          query: 'AppMetrics | where TimeGenerated > ago(4h) and Name in ("secure_upload.upload.accepted", "secure_upload.scan.outcome") | summarize Accepted=sumif(Sum, Name == "secure_upload.upload.accepted"), Scans=sumif(Sum, Name == "secure_upload.scan.outcome") | where Accepted > 0 and Scans == 0'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Count'
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    skipQueryValidation: true
  }
}

resource managementAuthDenialAlert 'Microsoft.Insights/scheduledQueryRules@2025-01-01-preview' = {
  name: '${applicationInsightsName}-management-auth-denials'
  location: location
  tags: tags
  properties: {
    displayName: 'Secure upload management auth or role denials'
    description: 'Alerts on sustained App Service authentication failures or in-app 403 denials for the management site.'
    enabled: true
    severity: 2
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [
      workspace.id
    ]
    criteria: {
      allOf: [
        {
          query: 'let EasyAuthDenials = toscalar(AppServiceAuthenticationLogs | where TimeGenerated > ago(15m) and SiteName == "${managementAppName}" and StatusCode in (401, 403) | count); let AppDenials = toscalar(AppRequests | where TimeGenerated > ago(15m) and AppRoleName == "${managementAppName}" and toint(ResultCode) == 403 | count); print Denials=EasyAuthDenials + AppDenials | where Denials >= 5'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Count'
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    skipQueryValidation: true
  }
}

resource managementStorageRbacAlert 'Microsoft.Insights/scheduledQueryRules@2025-01-01-preview' = {
  name: '${applicationInsightsName}-management-storage-rbac'
  location: location
  tags: tags
  properties: {
    displayName: 'Secure upload management storage RBAC failures'
    description: 'Alerts when the management site hits Azure Storage authorization failures that indicate missing or broadened RBAC.'
    enabled: true
    severity: 1
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [
      workspace.id
    ]
    criteria: {
      allOf: [
        {
          query: 'AppExceptions | where TimeGenerated > ago(15m) and AppRoleName == "${managementAppName}" and ExceptionType == "RequestFailedException" | where Message has_any ("AuthorizationPermissionMismatch", "AuthorizationFailure", "AuthorizationResourceTypeMismatch", "AuthenticationFailed") or InnermostMessage has_any ("AuthorizationPermissionMismatch", "AuthorizationFailure", "AuthorizationResourceTypeMismatch", "AuthenticationFailed") | summarize Failures=count() | where Failures > 0'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Count'
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    skipQueryValidation: true
  }
}

resource managementDeletionCleanupAlert 'Microsoft.Insights/scheduledQueryRules@2025-01-01-preview' = {
  name: '${applicationInsightsName}-management-deletion-cleanup'
  location: location
  tags: tags
  properties: {
    displayName: 'Secure upload management deletion cleanup backlog'
    description: 'Alerts when processor deletion cleanup remains incomplete or is retrying heavily, which can leave files stuck in Deleting.'
    enabled: true
    severity: 1
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [
      workspace.id
    ]
    criteria: {
      allOf: [
        {
          query: 'AppMetrics | where TimeGenerated > ago(15m) and AppRoleName == "${processorAppName}" and Name in ("secure_upload.scan.deletion_cleanup_failure", "secure_upload.scan.deletion_cleanup_retry") | summarize Failures=sumif(Sum, Name == "secure_upload.scan.deletion_cleanup_failure"), Retries=sumif(Sum, Name == "secure_upload.scan.deletion_cleanup_retry") | where Failures > 0 or Retries >= 5'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Count'
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    skipQueryValidation: true
  }
}

resource managementInventoryCapacityAlert 'Microsoft.Insights/scheduledQueryRules@2025-01-01-preview' = {
  name: '${applicationInsightsName}-management-inventory-capacity'
  location: location
  tags: tags
  properties: {
    displayName: 'Secure upload management inventory capacity'
    description: 'Alerts when management inventory enumeration exceeds the configured safe browsing limit.'
    enabled: true
    severity: 2
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [
      workspace.id
    ]
    criteria: {
      allOf: [
        {
          query: 'AppMetrics | where TimeGenerated > ago(15m) and AppRoleName == "${managementAppName}" and Name == "secure_upload.management.inventory_capacity_exceeded" | summarize Exhausted=sum(Sum) | where Exhausted > 0'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Count'
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    skipQueryValidation: true
  }
}

resource platformFailureAlert 'Microsoft.Insights/scheduledQueryRules@2025-01-01-preview' = {
  name: '${applicationInsightsName}-platform-failures'
  location: location
  tags: tags
  properties: {
    displayName: 'Secure upload platform failures'
    description: 'Alerts on Function exceptions and App Service HTTP 5xx responses.'
    enabled: true
    severity: 2
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [
      workspace.id
    ]
    criteria: {
      allOf: [
        {
          query: 'union isfuzzy=true AppExceptions, AppRequests | where TimeGenerated > ago(15m) | where ItemType == "exception" or toint(ResultCode) >= 500'
          operator: 'GreaterThan'
          threshold: 0
          timeAggregation: 'Count'
          failingPeriods: {
            minFailingPeriodsToAlert: 1
            numberOfEvaluationPeriods: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [
        actionGroup.id
      ]
    }
    autoMitigate: true
    checkWorkspaceAlertsStorageConfigured: false
    skipQueryValidation: true
  }
}

output workspaceId string = workspace.id
output applicationInsightsId string = applicationInsights.id
output applicationInsightsConnectionString string = applicationInsights.properties.ConnectionString
output actionGroupId string = actionGroup.id
output managementMonitoringPosture object = {
  authDiagnosticsCategory: 'AppServiceAuthenticationLogs'
  authDenialAlertName: managementAuthDenialAlert.name
  storageRbacAlertName: managementStorageRbacAlert.name
  deletionCleanupAlertName: managementDeletionCleanupAlert.name
  inventoryCapacityAlertName: managementInventoryCapacityAlert.name
  eventGridDeadLetterAlertProvidedBy: 'event-processing'
}
