@description('Azure region. Storage, Defender scan-result topic, hosting, and monitoring are deployed together.')
param location string = resourceGroup().location

@minLength(2)
@maxLength(12)
param environmentName string

@minLength(3)
param resourceNamePrefix string

@minLength(3)
@maxLength(24)
param storageAccountName string

@minLength(3)
@maxLength(24)
param functionHostStorageAccountName string

param webAppName string
param functionAppName string
param managementAppName string

@description('Subscription containing the existing virtual network.')
param virtualNetworkSubscriptionId string = subscription().subscriptionId

@description('Resource group containing the existing virtual network.')
param virtualNetworkResourceGroupName string

param virtualNetworkName string
param appIntegrationSubnetName string = 'secure-upload-app-integration'
param appIntegrationSubnetAddressPrefix string
param privateEndpointSubnetName string = 'secure-upload-private-endpoints'
param privateEndpointSubnetAddressPrefix string

@description('ARM resource ID of the centrally managed privatelink.blob.core.windows.net zone.')
param blobPrivateDnsZoneId string

@description('ARM resource ID of the centrally managed privatelink.queue.core.windows.net zone.')
param queuePrivateDnsZoneId string

@description('ARM resource ID of the centrally managed privatelink.table.core.windows.net zone.')
param tablePrivateDnsZoneId string
param appServicePlanName string = '${resourceNamePrefix}-${environmentName}-plan'
param eventGridTopicName string = '${resourceNamePrefix}-${environmentName}-scan'
param logAnalyticsWorkspaceName string = '${resourceNamePrefix}-${environmentName}-law'
param applicationInsightsName string = '${resourceNamePrefix}-${environmentName}-appi'
param actionGroupName string = '${resourceNamePrefix}-${environmentName}-ag'

@allowed([
  'Standard_LRS'
  'Standard_GRS'
  'Standard_ZRS'
])
param storageSkuName string = 'Standard_LRS'

param appServicePlanSkuName string = 'P1v3'
param appServicePlanSkuTier string = 'PremiumV3'

@minValue(1)
param appServicePlanCapacity int = 1

@minLength(1)
param allowedOrigins array

@minLength(1)
param allowedExtensions array

@minLength(1)
param allowedMediaTypes array

@minValue(1)
@maxValue(104857600)
param maximumFileSizeBytes int = 104857600

@minValue(1)
param requestsPerIpPerWindow int = 10

param rateLimitWindow string = '00:01:00'

@minValue(1)
param maximumConcurrentUploads int = 4

@minValue(1)
param globalRequestsPerWindow int = 100

@minValue(1)
param globalBytesPerWindow int = 524288000

@minValue(1)
param pollingRequestsPerMinute int = 30

param watchdogThreshold string = '03:00:00'

@minValue(1)
@maxValue(365)
param quarantineRetentionDays int = 30

@minValue(1)
param defenderMonthlyGbCap int = 5000

param uploadsEnabled bool = true

param tenantId string
param apiAudience string
param allowedHostClientApplicationId string
param requiredHostRole string = 'SecureUpload.Status.Read'

param managementTenantId string
param managementClientId string

@minLength(1)
param managementAudience string

@minLength(1)
param managementIssuer string

@minLength(1)
param managementRequiredRole string = 'SecureUpload.Management'

@minValue(1)
@maxValue(10000)
param managementInventoryCapacity int = 10000

@description('Resource ID of the existing Key Vault that stores the management Easy Auth credential secret.')
param managementAuthKeyVaultResourceId string

@description('Secret URI for the existing Key Vault secret used by the management Easy Auth provider.')
@minLength(1)
param managementAuthClientSecretUri string

@description('App setting name that resolves the Key Vault reference for the management Easy Auth credential secret.')
@minLength(1)
param managementAuthCredentialSettingName string = 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET'

@description('Object ID of the existing host workload service principal or managed identity.')
param hostPrincipalId string

@description('ARM resource ID of the existing host managed identity or workload resource, retained for deployment traceability.')
param hostIdentityResourceId string

@minValue(1)
@maxValue(30)
param eventGridMaxDeliveryAttempts int = 10

@minValue(1)
@maxValue(1440)
param eventGridEventTimeToLiveInMinutes int = 360

@description('Enable only after ProcessScanResult has been deployed into the Function App.')
param enableEventSubscription bool = false

param alertEmailReceivers array = []
param tags object = {}

param pendingContainerName string = 'pending'
param cleanContainerName string = 'clean'
param quarantineContainerName string = 'quarantine'
param deadLetterContainerName string = 'eventgrid-deadletter'
param statusTableName string = 'filestatus'
param uploadAdmissionTableName string = 'uploadadmission'

var validatedStorageAccountName = storageAccountName == toLower(storageAccountName) ? storageAccountName : fail('storageAccountName must be lowercase.')
var validatedFunctionHostStorageAccountName = functionHostStorageAccountName == toLower(functionHostStorageAccountName) ? functionHostStorageAccountName : fail('functionHostStorageAccountName must be lowercase.')
var validatedOrigins = reduce(allowedOrigins, true, (valid, origin) => valid && startsWith(origin, 'https://') && !endsWith(origin, '/')) ? allowedOrigins : fail('Every allowed origin must be an HTTPS origin without a trailing slash.')
var validatedExtensions = reduce(allowedExtensions, true, (valid, extension) => valid && startsWith(extension, '.') && extension == toLower(extension)) ? allowedExtensions : fail('Every extension must be lowercase and start with a dot.')
var validatedMediaTypes = reduce(allowedMediaTypes, true, (valid, mediaType) => valid && contains(mediaType, '/')) ? allowedMediaTypes : fail('Every media type must be nonempty and contain a slash.')
var validatedDefenderCap = defenderMonthlyGbCap * 1073741824 >= maximumFileSizeBytes ? defenderMonthlyGbCap : fail('The Defender cap must cover at least one maximum-size upload.')
var validatedGlobalByteBudget = globalBytesPerWindow >= maximumFileSizeBytes ? globalBytesPerWindow : fail('The global byte budget must cover at least one maximum-size upload.')
var validatedManagementIssuer = startsWith(managementIssuer, 'https://') ? managementIssuer : fail('managementIssuer must be an HTTPS issuer URI.')
var validatedManagementAuthKeyVaultResourceId = startsWith(managementAuthKeyVaultResourceId, '/subscriptions/') && contains(managementAuthKeyVaultResourceId, '/providers/Microsoft.KeyVault/vaults/') ? managementAuthKeyVaultResourceId : fail('managementAuthKeyVaultResourceId must be a Key Vault resource ID.')
var validatedManagementAuthClientSecretUri = startsWith(managementAuthClientSecretUri, 'https://') && contains(managementAuthClientSecretUri, '/secrets/') ? managementAuthClientSecretUri : fail('managementAuthClientSecretUri must be an HTTPS Key Vault secret URI.')
var managementAuthKeyVaultSubscriptionId = split(validatedManagementAuthKeyVaultResourceId, '/')[2]
var managementAuthKeyVaultResourceGroupName = split(validatedManagementAuthKeyVaultResourceId, '/')[4]
var managementAuthKeyVaultName = last(split(validatedManagementAuthKeyVaultResourceId, '/'))

module storage './modules/storage.bicep' = {
  name: 'storage'
  params: {
    location: location
    storageAccountName: validatedStorageAccountName
    functionHostStorageAccountName: validatedFunctionHostStorageAccountName
    storageSkuName: storageSkuName
    pendingContainerName: pendingContainerName
    cleanContainerName: cleanContainerName
    quarantineContainerName: quarantineContainerName
    deadLetterContainerName: deadLetterContainerName
    statusTableName: statusTableName
    uploadAdmissionTableName: uploadAdmissionTableName
    quarantineRetentionDays: quarantineRetentionDays
    tags: tags
  }
}

module monitoring './modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location: location
    logAnalyticsWorkspaceName: logAnalyticsWorkspaceName
    applicationInsightsName: applicationInsightsName
    actionGroupName: actionGroupName
    alertEmailReceivers: alertEmailReceivers
    globalBytesPerWindow: validatedGlobalByteBudget
    defenderMonthlyBytesCap: validatedDefenderCap * 1073741824
    managementAppName: managementAppName
    processorAppName: functionAppName
    tags: tags
  }
}

module networkSubnets './modules/network-subnets.bicep' = {
  name: 'network-subnets'
  scope: resourceGroup(virtualNetworkSubscriptionId, virtualNetworkResourceGroupName)
  params: {
    virtualNetworkName: virtualNetworkName
    appIntegrationSubnetName: appIntegrationSubnetName
    appIntegrationSubnetAddressPrefix: appIntegrationSubnetAddressPrefix
    privateEndpointSubnetName: privateEndpointSubnetName
    privateEndpointSubnetAddressPrefix: privateEndpointSubnetAddressPrefix
  }
}

module privateEndpoints './modules/private-endpoints.bicep' = {
  name: 'private-endpoints'
  params: {
    location: location
    resourceNamePrefix: resourceNamePrefix
    environmentName: environmentName
    privateEndpointSubnetId: networkSubnets.outputs.privateEndpointSubnetId
    dataStorageAccountId: storage.outputs.storageAccountId
    functionHostStorageAccountId: storage.outputs.functionHostStorageAccountId
    blobPrivateDnsZoneId: blobPrivateDnsZoneId
    queuePrivateDnsZoneId: queuePrivateDnsZoneId
    tablePrivateDnsZoneId: tablePrivateDnsZoneId
    tags: tags
  }
}

module defender './modules/defender.bicep' = {
  name: 'defender'
  params: {
    location: location
    storageAccountName: storage.outputs.storageAccountName
    eventGridTopicName: eventGridTopicName
    defenderMonthlyGbCap: validatedDefenderCap
    cleanContainerName: cleanContainerName
    quarantineContainerName: quarantineContainerName
    workspaceId: monitoring.outputs.workspaceId
    tags: tags
  }
}

module hosting './modules/hosting.bicep' = {
  name: 'hosting'
  dependsOn: [
    privateEndpoints
  ]
  params: {
    location: location
    appServicePlanName: appServicePlanName
    appServicePlanSkuName: appServicePlanSkuName
    appServicePlanSkuTier: appServicePlanSkuTier
    appServicePlanCapacity: appServicePlanCapacity
    webAppName: webAppName
    functionAppName: functionAppName
    managementAppName: managementAppName
    appIntegrationSubnetId: networkSubnets.outputs.appIntegrationSubnetId
    functionHostBlobServiceUri: storage.outputs.functionHostBlobServiceUri
    functionHostQueueServiceUri: storage.outputs.functionHostQueueServiceUri
    functionHostTableServiceUri: storage.outputs.functionHostTableServiceUri
    dataBlobServiceUri: storage.outputs.blobServiceUri
    dataTableServiceUri: storage.outputs.tableServiceUri
    logAnalyticsWorkspaceId: monitoring.outputs.workspaceId
    storageAccountName: storage.outputs.storageAccountName
    pendingContainerName: pendingContainerName
    cleanContainerName: cleanContainerName
    quarantineContainerName: quarantineContainerName
    statusTableName: statusTableName
    uploadAdmissionTableName: uploadAdmissionTableName
    eventGridTopicId: defender.outputs.topicId
    applicationInsightsId: monitoring.outputs.applicationInsightsId
    applicationInsightsConnectionString: monitoring.outputs.applicationInsightsConnectionString
    tenantId: tenantId
    apiAudience: apiAudience
    allowedHostClientApplicationId: allowedHostClientApplicationId
    requiredHostRole: requiredHostRole
    allowedOrigins: validatedOrigins
    allowedExtensions: validatedExtensions
    allowedMediaTypes: validatedMediaTypes
    maximumFileSizeBytes: maximumFileSizeBytes
    requestsPerIpPerWindow: requestsPerIpPerWindow
    rateLimitWindow: rateLimitWindow
    maximumConcurrentUploads: maximumConcurrentUploads
    globalRequestsPerWindow: globalRequestsPerWindow
    globalBytesPerWindow: validatedGlobalByteBudget
    pollingRequestsPerMinute: pollingRequestsPerMinute
    watchdogThreshold: watchdogThreshold
    defenderMonthlyGbCap: validatedDefenderCap
    uploadsEnabled: uploadsEnabled
    managementTenantId: managementTenantId
    managementClientId: managementClientId
    managementAudience: managementAudience
    managementIssuer: validatedManagementIssuer
    managementRequiredRole: managementRequiredRole
    managementInventoryCapacity: managementInventoryCapacity
    managementAuthClientSecretUri: validatedManagementAuthClientSecretUri
    managementAuthCredentialSettingName: managementAuthCredentialSettingName
    tags: tags
  }
}

module managementKeyVaultAccess './modules/key-vault-access.bicep' = {
  name: 'management-key-vault-access'
  scope: resourceGroup(managementAuthKeyVaultSubscriptionId, managementAuthKeyVaultResourceGroupName)
  params: {
    keyVaultName: managementAuthKeyVaultName
    principalId: hosting.outputs.managementAppPrincipalId
  }
}

module identity './modules/identity.bicep' = {
  name: 'identity'
  params: {
    storageAccountName: storage.outputs.storageAccountName
    functionHostStorageAccountName: storage.outputs.functionHostStorageAccountName
    pendingContainerName: pendingContainerName
    cleanContainerName: cleanContainerName
    quarantineContainerName: quarantineContainerName
    statusTableName: statusTableName
    uploadAdmissionTableName: uploadAdmissionTableName
    webPrincipalId: hosting.outputs.webAppPrincipalId
    processorPrincipalId: hosting.outputs.functionAppPrincipalId
    managementPrincipalId: hosting.outputs.managementAppPrincipalId
    hostPrincipalId: hostPrincipalId
    eventGridTopicPrincipalId: defender.outputs.topicPrincipalId
  }
}

module eventProcessing './modules/event-processing.bicep' = {
  name: 'event-processing'
  dependsOn: [
    identity
  ]
  params: {
    topicName: eventGridTopicName
    functionAppName: functionAppName
    functionName: 'ProcessScanResult'
    storageAccountName: storage.outputs.storageAccountName
    deadLetterContainerName: deadLetterContainerName
    maxDeliveryAttempts: eventGridMaxDeliveryAttempts
    eventTimeToLiveInMinutes: eventGridEventTimeToLiveInMinutes
    enableEventSubscription: enableEventSubscription
    actionGroupId: monitoring.outputs.actionGroupId
  }
}

var managementIdentityPostureOutput = {
  principalId: hosting.outputs.managementAppPrincipalId
  managedIdentityType: hosting.outputs.managementRuntimePosture.managedIdentityType
  statusTableReadUpdateOnly: identity.outputs.managementAccessPosture.statusTableReadUpdateOnly
  cleanBlobReadOnly: identity.outputs.managementAccessPosture.cleanBlobReadOnly
  keyVaultSecretReadOnly: managementKeyVaultAccess.outputs.secretReadOnly
  keyVaultResourceId: managementKeyVaultAccess.outputs.keyVaultId
  pendingBlobAccess: identity.outputs.managementAccessPosture.pendingBlobAccess
  quarantineBlobAccess: identity.outputs.managementAccessPosture.quarantineBlobAccess
  uploadAdmissionTableAccess: identity.outputs.managementAccessPosture.uploadAdmissionTableAccess
  functionHostStorageAccess: identity.outputs.managementAccessPosture.functionHostStorageAccess
  eventGridAccess: identity.outputs.managementAccessPosture.eventGridAccess
  vnetRouteAllEnabled: hosting.outputs.managementRuntimePosture.vnetRouteAllEnabled
  authDiagnosticsToWorkspace: hosting.outputs.managementRuntimePosture.authDiagnosticsToWorkspace
  monitoringMetricsPublisher: hosting.outputs.managementRuntimePosture.monitoringMetricsPublisher
}

output webAppName string = webAppName
output webAppUrl string = 'https://${hosting.outputs.webAppDefaultHostName}'
output functionAppName string = functionAppName
output managementAppName string = managementAppName
output managementAppUrl string = 'https://${hosting.outputs.managementAppDefaultHostName}'
output storageAccountName string = storage.outputs.storageAccountName
output cleanContainerName string = cleanContainerName
output eventGridTopicId string = defender.outputs.topicId
output eventSubscriptionId string = eventProcessing.outputs.eventSubscriptionId
output applicationInsightsName string = applicationInsightsName
output hostCleanRoleDefinitionId string = identity.outputs.hostCleanRoleDefinitionId
output hostIdentityResourceId string = hostIdentityResourceId
output managementIdentityPosture object = managementIdentityPostureOutput
output securityPosture object = {
  storage: storage.outputs.securityPosture
  rbac: identity.outputs.rbacPosture
  defender: defender.outputs.defenderPosture
  eventDelivery: eventProcessing.outputs.eventDeliveryPosture
  runtime: hosting.outputs.runtimePosture
  managementIdentity: managementIdentityPostureOutput
  monitoring: monitoring.outputs.managementMonitoringPosture
}
