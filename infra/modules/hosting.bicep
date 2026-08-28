param location string
param appServicePlanName string
param appServicePlanSkuName string
param appServicePlanSkuTier string
param appServicePlanCapacity int
param webAppName string
param functionAppName string
param managementAppName string
param appIntegrationSubnetId string
param functionHostBlobServiceUri string
param functionHostQueueServiceUri string
param functionHostTableServiceUri string
param dataBlobServiceUri string
param dataTableServiceUri string
param logAnalyticsWorkspaceId string
param storageAccountName string
param pendingContainerName string
param cleanContainerName string
param quarantineContainerName string
param statusTableName string
param uploadAdmissionTableName string
param eventGridTopicId string
param applicationInsightsId string
param applicationInsightsConnectionString string
param tenantId string
param apiAudience string
param allowedHostClientApplicationId string
param requiredHostRole string
param allowedOrigins array
param allowedExtensions array
param allowedMediaTypes array
param maximumFileSizeBytes int
param requestsPerIpPerWindow int
param rateLimitWindow string
param maximumConcurrentUploads int
param globalRequestsPerWindow int
param globalBytesPerWindow int
param pollingRequestsPerMinute int
param watchdogThreshold string
param defenderMonthlyGbCap int
param uploadsEnabled bool
param managementTenantId string
param managementClientId string
param managementAudience string
param managementIssuer string
param managementRequiredRole string
param managementInventoryCapacity int
param managementAuthClientSecretUri string
param managementAuthCredentialSettingName string
param tags object = {}

var allowedOriginSettings = [
  for (origin, index) in allowedOrigins: {
    name: 'AllowedOrigins__Origins__${index}'
    value: origin
  }
]
var allowedExtensionSettings = [
  for (extension, index) in allowedExtensions: {
    name: 'FilePolicy__AllowedExtensions__${index}'
    value: extension
  }
]
var allowedMediaTypeSettings = [
  for (mediaType, index) in allowedMediaTypes: {
    name: 'FilePolicy__AllowedMediaTypes__${index}'
    value: mediaType
  }
]
var telemetryCorrelationKey = '${uniqueString(subscription().id, resourceGroup().id, storageAccountName, 'telemetry-a')}${uniqueString(subscription().id, resourceGroup().id, storageAccountName, 'telemetry-b')}${uniqueString(subscription().id, resourceGroup().id, storageAccountName, 'telemetry-c')}'
var managementSessionLifetime = '00:15:00'

resource plan 'Microsoft.Web/serverfarms@2025-03-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: appServicePlanSkuName
    tier: appServicePlanSkuTier
    capacity: appServicePlanCapacity
  }

  properties: {
    reserved: true
    zoneRedundant: false
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: last(split(applicationInsightsId, '/'))
}

resource webApp 'Microsoft.Web/sites@2025-03-01' = {
  name: webAppName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    enabled: true
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    serverFarmId: plan.id
    virtualNetworkSubnetId: appIntegrationSubnetId
    siteConfig: {
      alwaysOn: true
      ftpsState: 'Disabled'
      http20Enabled: true
      linuxFxVersion: 'DOTNETCORE|10.0'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      remoteDebuggingEnabled: false
      use32BitWorkerProcess: false
      vnetRouteAllEnabled: true
      appSettings: concat([
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsightsConnectionString }
        { name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING', value: 'Authorization=AAD' }
        { name: 'Telemetry__CorrelationKey', value: telemetryCorrelationKey }
        { name: 'HostWorkloadAuthorization__TenantId', value: tenantId }
        { name: 'HostWorkloadAuthorization__Audience', value: apiAudience }
        { name: 'HostWorkloadAuthorization__AllowedClientApplicationId', value: allowedHostClientApplicationId }
        { name: 'HostWorkloadAuthorization__RequiredRole', value: requiredHostRole }
        { name: 'FilePolicy__MaximumFileSizeBytes', value: string(maximumFileSizeBytes) }
        { name: 'RateLimits__RequestsPerIpPerWindow', value: string(requestsPerIpPerWindow) }
        { name: 'RateLimits__Window', value: rateLimitWindow }
        { name: 'Admission__Enabled', value: string(uploadsEnabled) }
        { name: 'Admission__MaximumConcurrentUploads', value: string(maximumConcurrentUploads) }
        { name: 'Admission__RequestsPerWindow', value: string(globalRequestsPerWindow) }
        { name: 'Admission__BytesPerWindow', value: string(globalBytesPerWindow) }
        { name: 'Admission__Window', value: rateLimitWindow }
        { name: 'Admission__DefenderMonthlyBytesCap', value: string(defenderMonthlyGbCap * 1073741824) }
        { name: 'Admission__MaximumStoreAttempts', value: '5' }
        { name: 'StatusPolling__RequestsPerMinute', value: string(pollingRequestsPerMinute) }
        { name: 'Storage__BlobServiceUri', value: dataBlobServiceUri }
        { name: 'Storage__TableServiceUri', value: dataTableServiceUri }
        { name: 'Storage__StatusTableName', value: statusTableName }
        { name: 'Storage__UploadAdmissionTableName', value: uploadAdmissionTableName }
        { name: 'Storage__PendingContainerName', value: pendingContainerName }
        { name: 'Storage__CleanContainerName', value: cleanContainerName }
        { name: 'Storage__QuarantineContainerName', value: quarantineContainerName }
      ], allowedOriginSettings, allowedExtensionSettings, allowedMediaTypeSettings)
    }
  }
}

resource functionApp 'Microsoft.Web/sites@2025-03-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    enabled: true
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    serverFarmId: plan.id
    virtualNetworkSubnetId: appIntegrationSubnetId
    siteConfig: {
      alwaysOn: true
      ftpsState: 'Disabled'
      http20Enabled: true
      linuxFxVersion: 'DOTNET-ISOLATED|10.0'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      remoteDebuggingEnabled: false
      use32BitWorkerProcess: false
      vnetRouteAllEnabled: true
      appSettings: [
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'WEBSITE_RUN_FROM_PACKAGE', value: '1' }
        { name: 'SCM_DO_BUILD_DURING_DEPLOYMENT', value: 'false' }
        { name: 'ENABLE_ORYX_BUILD', value: 'false' }
        { name: 'AzureWebJobsStorage__blobServiceUri', value: functionHostBlobServiceUri }
        { name: 'AzureWebJobsStorage__queueServiceUri', value: functionHostQueueServiceUri }
        { name: 'AzureWebJobsStorage__tableServiceUri', value: functionHostTableServiceUri }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsightsConnectionString }
        { name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING', value: 'Authorization=AAD' }
        { name: 'Telemetry__CorrelationKey', value: telemetryCorrelationKey }
        { name: 'SecureUpload__ExpectedTopic', value: eventGridTopicId }
        { name: 'SecureUpload__BlobServiceUri', value: dataBlobServiceUri }
        { name: 'SecureUpload__TableServiceUri', value: dataTableServiceUri }
        { name: 'SecureUpload__StorageAccountName', value: storageAccountName }
        { name: 'SecureUpload__StatusTableName', value: statusTableName }
        { name: 'SecureUpload__PendingContainerName', value: pendingContainerName }
        { name: 'SecureUpload__CleanContainerName', value: cleanContainerName }
        { name: 'SecureUpload__QuarantineContainerName', value: quarantineContainerName }
        { name: 'SecureUpload__ScanWatchdogThreshold', value: watchdogThreshold }
      ]
    }
  }
}

resource managementApp 'Microsoft.Web/sites@2025-03-01' = {
  name: managementAppName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    enabled: true
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    serverFarmId: plan.id
    virtualNetworkSubnetId: appIntegrationSubnetId
    siteConfig: {
      alwaysOn: true
      ftpsState: 'Disabled'
      http20Enabled: true
      linuxFxVersion: 'DOTNETCORE|10.0'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      remoteDebuggingEnabled: false
      use32BitWorkerProcess: false
      vnetRouteAllEnabled: true
      appSettings: [
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsightsConnectionString }
        { name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING', value: 'Authorization=AAD' }
        { name: 'Telemetry__CorrelationKey', value: telemetryCorrelationKey }
        { name: 'ManagementAuthorization__TenantId', value: managementTenantId }
        { name: 'ManagementAuthorization__ClientId', value: managementClientId }
        { name: 'ManagementAuthorization__Audience', value: managementAudience }
        { name: 'ManagementAuthorization__Issuer', value: managementIssuer }
        { name: 'ManagementAuthorization__RequiredRole', value: managementRequiredRole }
        { name: 'Inventory__Capacity', value: string(managementInventoryCapacity) }
        { name: 'Storage__BlobServiceUri', value: dataBlobServiceUri }
        { name: 'Storage__TableServiceUri', value: dataTableServiceUri }
        { name: 'Storage__StatusTableName', value: statusTableName }
        { name: 'Storage__PendingContainerName', value: pendingContainerName }
        { name: 'Storage__CleanContainerName', value: cleanContainerName }
        { name: 'Storage__QuarantineContainerName', value: quarantineContainerName }
        { name: managementAuthCredentialSettingName, value: '@Microsoft.KeyVault(SecretUri=${managementAuthClientSecretUri})' }
      ]
    }
  }
}

resource managementAuthSettings 'Microsoft.Web/sites/config@2022-09-01' = {
  name: 'authsettingsV2'
  parent: managementApp
  properties: {
    globalValidation: {
      excludedPaths: []
      redirectToProvider: 'azureactivedirectory'
      requireAuthentication: true
      unauthenticatedClientAction: 'RedirectToLoginPage'
    }
    httpSettings: {
      requireHttps: true
      routes: {
        apiPrefix: '/.auth'
      }
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        login: {
          disableWWWAuthenticate: false
          loginParameters: []
        }
        registration: {
          clientId: managementClientId
          clientSecretSettingName: managementAuthCredentialSettingName
          openIdIssuer: managementIssuer
        }
        validation: {
          allowedAudiences: [
            managementAudience
          ]
        }
      }
    }
    login: {
      allowedExternalRedirectUrls: []
      cookieExpiration: {
        convention: 'FixedTime'
        timeToExpiration: managementSessionLifetime
      }
      preserveUrlFragmentsForLogins: false
      routes: {
        logoutEndpoint: '/.auth/logout'
      }
      tokenStore: {
        enabled: false
      }
    }
    platform: {
      enabled: true
      runtimeVersion: '~1'
    }
  }
}

resource managementFtpPublishingCredentialsPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2025-03-01' = {
  parent: managementApp
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource managementScmPublishingCredentialsPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2025-03-01' = {
  parent: managementApp
  name: 'scm'
  properties: {
    allow: false
  }
}

resource managementAuthDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'authentication'
  scope: managementApp
  dependsOn: [
    managementAuthSettings
  ]
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [
      {
        category: 'AppServiceAuthenticationLogs'
        enabled: true
      }
    ]
  }
}

resource webMonitoringRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(applicationInsights.id, webApp.id, 'monitoring-metrics-publisher')
  scope: applicationInsights
  properties: {
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
  }
}

resource functionMonitoringRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(applicationInsights.id, functionApp.id, 'monitoring-metrics-publisher')
  scope: applicationInsights
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
  }
}

resource managementMonitoringRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(applicationInsights.id, managementApp.id, 'monitoring-metrics-publisher')
  scope: applicationInsights
  properties: {
    principalId: managementApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
  }
}

output webAppId string = webApp.id
output webAppPrincipalId string = webApp.identity.principalId
output webAppDefaultHostName string = webApp.properties.defaultHostName
output functionAppId string = functionApp.id
output functionAppPrincipalId string = functionApp.identity.principalId
output functionAppDefaultHostName string = functionApp.properties.defaultHostName
output managementAppId string = managementApp.id
output managementAppPrincipalId string = managementApp.identity.principalId
output managementAppDefaultHostName string = managementApp.properties.defaultHostName
output runtimePosture object = {
  webLinuxFxVersion: 'DOTNETCORE|10.0'
  functionLinuxFxVersion: 'DOTNET-ISOLATED|10.0'
  managementLinuxFxVersion: 'DOTNETCORE|10.0'
  functionsRuntime: '~4'
  identityBasedHostStorage: true
  appIntegrationSubnetId: appIntegrationSubnetId
  vnetRouteAllEnabled: true
  managementAuthSettingsV2Enabled: true
  managementBasicPublishingCredentialsDisabled: true
  managementAuthDiagnosticsToWorkspace: true
}
output managementRuntimePosture object = {
  managedIdentityType: 'SystemAssigned'
  httpsOnly: true
  publicNetworkAccess: 'Enabled'
  minTlsVersion: '1.2'
  scmMinTlsVersion: '1.2'
  vnetRouteAllEnabled: true
  appIntegrationSubnetId: appIntegrationSubnetId
  authSettingsV2Enabled: true
  authDiagnosticsToWorkspace: true
  ftpBasicAuthDisabled: true
  scmBasicAuthDisabled: true
  keyVaultReferenceEnabled: true
  monitoringMetricsPublisher: true
}
