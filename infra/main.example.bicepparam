using './main.bicep'

param location = 'centralus'
param environmentName = 'dev'
param resourceNamePrefix = 'secureupload'
param storageAccountName = '<globally-unique-data-storage-name>'
param functionHostStorageAccountName = '<globally-unique-function-storage-name>'
param webAppName = '<globally-unique-web-app-name>'
param functionAppName = '<globally-unique-function-app-name>'
param managementAppName = '<globally-unique-management-app-name>'

param virtualNetworkSubscriptionId = '<network-subscription-id>'
param virtualNetworkResourceGroupName = '<network-resource-group>'
param virtualNetworkName = '<virtual-network-name>'
param appIntegrationSubnetAddressPrefix = '<unused-/26-cidr>'
param privateEndpointSubnetAddressPrefix = '<unused-/27-cidr>'
param blobPrivateDnsZoneId = '/subscriptions/<network-subscription-id>/resourceGroups/<dns-resource-group>/providers/Microsoft.Network/privateDnsZones/privatelink.blob.core.windows.net'
param queuePrivateDnsZoneId = '/subscriptions/<network-subscription-id>/resourceGroups/<dns-resource-group>/providers/Microsoft.Network/privateDnsZones/privatelink.queue.core.windows.net'
param tablePrivateDnsZoneId = '/subscriptions/<network-subscription-id>/resourceGroups/<dns-resource-group>/providers/Microsoft.Network/privateDnsZones/privatelink.table.core.windows.net'

param allowedOrigins = [
  'https://host.example'
]
param allowedExtensions = [
  '.doc'
  '.docx'
  '.jpeg'
  '.jpg'
  '.pdf'
  '.png'
]
param allowedMediaTypes = [
  'application/msword'
  'application/pdf'
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document'
  'image/jpeg'
  'image/png'
]

param tenantId = '<entra-tenant-id>'
param apiAudience = 'api://<secure-upload-api-client-id>'
param allowedHostClientApplicationId = '<host-managed-identity-client-id>'

param managementTenantId = '<entra-tenant-id>'
param managementClientId = '<management-app-client-id>'
param managementAudience = 'api://<management-app-client-id>'
param managementIssuer = 'https://login.microsoftonline.com/<entra-tenant-id>/v2.0'
param managementRequiredRole = 'SecureUpload.Management'
param managementInventoryCapacity = 10000
param managementAuthKeyVaultResourceId = '/subscriptions/<subscription-id>/resourceGroups/<key-vault-resource-group>/providers/Microsoft.KeyVault/vaults/<key-vault-name>'
param managementAuthClientSecretUri = 'https://<key-vault-name>.vault.azure.net/secrets/secure-upload-management-auth/<secret-version>'
param managementAuthCredentialSettingName = 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET'

param hostPrincipalId = '<host-managed-identity-principal-id>'
param hostIdentityResourceId = '/subscriptions/<subscription-id>/resourceGroups/<host-resource-group>/providers/Microsoft.ManagedIdentity/userAssignedIdentities/<host-identity-name>'

// Set false for the first deployment of a new environment. Deploy the Function
// package, then set true and redeploy so Event Grid can validate the destination.
param enableEventSubscription = false

param alertEmailReceivers = [
  {
    name: 'operations'
    emailAddress: 'operations@example.com'
  }
]
param tags = {
  application: 'secure-upload'
  environment: 'dev'
}
