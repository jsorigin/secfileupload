targetScope = 'resourceGroup'

var defenderMonthlyGbCap = 100
var quarantineRetentionDays = 30
var cleanContainerName = 'clean'
var quarantineContainerName = 'quarantine'
var allowedOrigins = [
  'https://host.example'
]
var statusTableName = 'filestatus'
var uploadAdmissionTableName = 'uploadadmission'
var managementTenantId = '00000000-0000-0000-0000-000000000004'
var managementInventoryCapacity = 10000
var managementIssuer = '${environment().authentication.loginEndpoint}${managementTenantId}/v2.0'
var managementSecretUri = 'https://management-secrets.${environment().suffixes.keyvaultDns}/secrets/secure-upload-management-auth/00000000000000000000000000000000'

module sut '../main.bicep' = {
  name: 'security-posture'
  params: {
    location: 'eastus2'
    environmentName: 'test'
    resourceNamePrefix: 'sec'
    storageAccountName: 'secsecureupload001'
    functionHostStorageAccountName: 'secsecurehost001'
    webAppName: 'sec-secure-upload-web'
    functionAppName: 'sec-secure-upload-func'
    managementAppName: 'sec-secure-upload-management'
    virtualNetworkResourceGroupName: 'network'
    virtualNetworkName: 'shared-vnet'
    appIntegrationSubnetAddressPrefix: '10.20.10.0/26'
    privateEndpointSubnetAddressPrefix: '10.20.11.0/27'
    blobPrivateDnsZoneId: '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/dns/providers/Microsoft.Network/privateDnsZones/privatelink.blob.example'
    queuePrivateDnsZoneId: '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/dns/providers/Microsoft.Network/privateDnsZones/privatelink.queue.example'
    tablePrivateDnsZoneId: '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/dns/providers/Microsoft.Network/privateDnsZones/privatelink.table.example'
    allowedOrigins: allowedOrigins
    allowedExtensions: [
      '.pdf'
    ]
    allowedMediaTypes: [
      'application/pdf'
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
    defenderMonthlyGbCap: defenderMonthlyGbCap
    quarantineRetentionDays: quarantineRetentionDays
    cleanContainerName: cleanContainerName
    quarantineContainerName: quarantineContainerName
    enableEventSubscription: false
    statusTableName: statusTableName
    uploadAdmissionTableName: uploadAdmissionTableName
    alertEmailReceivers: []
  }
}

assert originsAreHttps = reduce(allowedOrigins, true, (valid, origin) => valid && startsWith(origin, 'https://'))
assert cleanAndQuarantineDiffer = cleanContainerName != quarantineContainerName
assert defenderCapCoversMaximumUpload = defenderMonthlyGbCap * 1073741824 >= 104857600
assert quarantineRetentionIsBounded = quarantineRetentionDays >= 1 && quarantineRetentionDays <= 365
assert eventDeliveryDefaultsAreBounded = 10 <= 30 && 360 <= 1440
assert admissionTableIsSeparate = uploadAdmissionTableName != statusTableName
assert managementInventoryCapacityIsBounded = managementInventoryCapacity >= 1 && managementInventoryCapacity <= 10000
assert managementRoleIsDedicated = 'SecureUpload.Management' != 'SecureUpload.Status.Read'
assert managementIssuerUsesHttps = startsWith(managementIssuer, 'https://')
assert managementSecretUriTargetsKeyVault = startsWith(managementSecretUri, 'https://') && contains(managementSecretUri, '/secrets/')
