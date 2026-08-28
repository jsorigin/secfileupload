param location string
param resourceNamePrefix string
param environmentName string
param privateEndpointSubnetId string
param dataStorageAccountId string
param functionHostStorageAccountId string
param blobPrivateDnsZoneId string
param queuePrivateDnsZoneId string
param tablePrivateDnsZoneId string
param tags object = {}

var endpointPrefix = '${resourceNamePrefix}-${environmentName}'

resource dataBlobEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: '${endpointPrefix}-data-blob-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'data-blob'
        properties: {
          privateLinkServiceId: dataStorageAccountId
          groupIds: [
            'blob'
          ]
        }
      }
    ]
  }
}

resource dataBlobDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: dataBlobEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'blob'
        properties: {
          privateDnsZoneId: blobPrivateDnsZoneId
        }
      }
    ]
  }
}

resource dataTableEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: '${endpointPrefix}-data-table-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'data-table'
        properties: {
          privateLinkServiceId: dataStorageAccountId
          groupIds: [
            'table'
          ]
        }
      }
    ]
  }
}

resource dataTableDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: dataTableEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'table'
        properties: {
          privateDnsZoneId: tablePrivateDnsZoneId
        }
      }
    ]
  }
}

resource hostBlobEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: '${endpointPrefix}-host-blob-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'host-blob'
        properties: {
          privateLinkServiceId: functionHostStorageAccountId
          groupIds: [
            'blob'
          ]
        }
      }
    ]
  }
}

resource hostBlobDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: hostBlobEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'blob'
        properties: {
          privateDnsZoneId: blobPrivateDnsZoneId
        }
      }
    ]
  }
}

resource hostQueueEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: '${endpointPrefix}-host-queue-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'host-queue'
        properties: {
          privateLinkServiceId: functionHostStorageAccountId
          groupIds: [
            'queue'
          ]
        }
      }
    ]
  }
}

resource hostQueueDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: hostQueueEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'queue'
        properties: {
          privateDnsZoneId: queuePrivateDnsZoneId
        }
      }
    ]
  }
}

resource hostTableEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: '${endpointPrefix}-host-table-pe'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'host-table'
        properties: {
          privateLinkServiceId: functionHostStorageAccountId
          groupIds: [
            'table'
          ]
        }
      }
    ]
  }
}

resource hostTableDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: hostTableEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'table'
        properties: {
          privateDnsZoneId: tablePrivateDnsZoneId
        }
      }
    ]
  }
}

output privateEndpointIds array = [
  dataBlobEndpoint.id
  dataTableEndpoint.id
  hostBlobEndpoint.id
  hostQueueEndpoint.id
  hostTableEndpoint.id
]
