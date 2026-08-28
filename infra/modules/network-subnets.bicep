param virtualNetworkName string
param appIntegrationSubnetName string
param appIntegrationSubnetAddressPrefix string
param privateEndpointSubnetName string
param privateEndpointSubnetAddressPrefix string

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2024-05-01' existing = {
  name: virtualNetworkName
}

resource appIntegrationSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' = {
  parent: virtualNetwork
  name: appIntegrationSubnetName
  properties: {
    addressPrefix: appIntegrationSubnetAddressPrefix
    delegations: [
      {
        name: 'app-service'
        properties: {
          serviceName: 'Microsoft.Web/serverFarms'
        }
      }
    ]
    privateEndpointNetworkPolicies: 'Enabled'
  }
}

resource privateEndpointSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' = {
  parent: virtualNetwork
  name: privateEndpointSubnetName
  properties: {
    addressPrefix: privateEndpointSubnetAddressPrefix
    privateEndpointNetworkPolicies: 'Disabled'
  }
}

output appIntegrationSubnetId string = appIntegrationSubnet.id
output privateEndpointSubnetId string = privateEndpointSubnet.id
