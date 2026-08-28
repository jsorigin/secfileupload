targetScope = 'resourceGroup'

var expectedCleanContainerName = 'clean'
var enableEventSubscription = false
var webAppName = 'sut-secure-upload-web'
var functionAppName = 'sut-secure-upload-func'
var managementAppName = 'sut-secure-upload-management'
var statusTableName = 'filestatus'
var uploadAdmissionTableName = 'uploadadmission'
var managementTenantId = '00000000-0000-0000-0000-000000000004'
var managementInventoryCapacity = 10000
var managementIssuer = '${environment().authentication.loginEndpoint}${managementTenantId}/v2.0'
var managementSecretUri = 'https://management-secrets.${environment().suffixes.keyvaultDns}/secrets/secure-upload-management-auth/00000000000000000000000000000000'

module sut '../main.bicep' = {
  name: 'main-happy-path'
  params: {
    location: 'eastus2'
    environmentName: 'test'
    resourceNamePrefix: 'sut'
    storageAccountName: 'sutsecureupload001'
    functionHostStorageAccountName: 'sutsecurehost001'
    webAppName: webAppName
    functionAppName: functionAppName
    managementAppName: managementAppName
    virtualNetworkResourceGroupName: 'network'
    virtualNetworkName: 'shared-vnet'
    appIntegrationSubnetAddressPrefix: '10.20.10.0/26'
    privateEndpointSubnetAddressPrefix: '10.20.11.0/27'
    blobPrivateDnsZoneId: '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/dns/providers/Microsoft.Network/privateDnsZones/privatelink.blob.example'
    queuePrivateDnsZoneId: '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/dns/providers/Microsoft.Network/privateDnsZones/privatelink.queue.example'
    tablePrivateDnsZoneId: '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/dns/providers/Microsoft.Network/privateDnsZones/privatelink.table.example'
    allowedOrigins: [
      'https://host.example'
    ]
    allowedExtensions: [
      '.pdf'
      '.png'
    ]
    allowedMediaTypes: [
      'application/pdf'
      'image/png'
    ]
    tenantId: '00000000-0000-0000-0000-000000000001'
    apiAudience: 'api://secure-upload'
    allowedHostClientApplicationId: '00000000-0000-0000-0000-000000000002'
    managementTenantId: managementTenantId
    managementClientId: '00000000-0000-0000-0000-000000000005'
    managementAudience: 'api://secure-upload-management'
    managementIssuer: managementIssuer
    managementRequiredRole: 'SecureUpload.Management'
    managementInventoryCapacity: managementInventoryCapacity
    managementAuthKeyVaultResourceId: '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/shared-security/providers/Microsoft.KeyVault/vaults/management-secrets'
    managementAuthClientSecretUri: managementSecretUri
    managementAuthCredentialSettingName: 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET'
    hostPrincipalId: '00000000-0000-0000-0000-000000000003'
    hostIdentityResourceId: '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/host/providers/Microsoft.ManagedIdentity/userAssignedIdentities/host-workload'
    cleanContainerName: expectedCleanContainerName
    enableEventSubscription: enableEventSubscription
    statusTableName: statusTableName
    uploadAdmissionTableName: uploadAdmissionTableName
    alertEmailReceivers: []
  }
}

assert cleanContainerIsStable = expectedCleanContainerName == 'clean'
assert eventSubscriptionWaitsForCode = enableEventSubscription == false
assert appNameIsEnvironmentSpecific = contains(webAppName, 'sut-')
assert managementAppIsDistinct = managementAppName != webAppName && managementAppName != functionAppName
assert admissionTableIsSeparate = uploadAdmissionTableName != statusTableName
assert managementRoleIsDedicated = 'SecureUpload.Management' != 'SecureUpload.Status.Read'
assert managementIssuerMatchesTenant = managementIssuer == '${environment().authentication.loginEndpoint}${managementTenantId}/v2.0'
assert managementInventoryCapacityMatchesReleaseCap = managementInventoryCapacity == 10000
assert managementSecretUriLooksVersioned = startsWith(managementSecretUri, 'https://') && contains(managementSecretUri, '/secrets/')
