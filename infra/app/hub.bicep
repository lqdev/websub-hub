param name string
@description('Primary location for all resources and the Flex Consumption Function App.')
param location string = resourceGroup().location
param tags object = {}
param applicationInsightsName string
param appServicePlanId string
param appSettings object = {}
param storageAccountName string
param deploymentStorageContainerName string
param identityId string
param identityClientId string
param instanceMemoryMB int = 2048
param maximumInstanceCount int = 10

var applicationInsightsIdentity = 'ClientId=${identityClientId};Authorization=AAD'
var kind = 'functionapp,linux'

var baseAppSettings = {
  FUNCTIONS_WORKER_RUNTIME: 'dotnet-isolated'
  FUNCTIONS_EXTENSION_VERSION: '~4'
  AzureWebJobsStorage__credential: 'managedidentity'
  AzureWebJobsStorage__clientId: identityClientId
  APPLICATIONINSIGHTS_AUTHENTICATION_STRING: applicationInsightsIdentity
  APPLICATIONINSIGHTS_CONNECTION_STRING: applicationInsights.properties.ConnectionString
}

var storageSettings = {
  AzureWebJobsStorage__blobServiceUri: storage.properties.primaryEndpoints.blob
  AzureWebJobsStorage__queueServiceUri: storage.properties.primaryEndpoints.queue
  AzureWebJobsStorage__tableServiceUri: storage.properties.primaryEndpoints.table
}

var allAppSettings = union(appSettings, storageSettings, baseAppSettings)

resource storage 'Microsoft.Storage/storageAccounts@2022-09-01' existing = {
  name: storageAccountName
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: applicationInsightsName
}

module app 'br/public:avm/res/web/site:0.15.1' = {
  name: 'hub-flex-consumption-app'
  params: {
    kind: kind
    name: name
    location: location
    tags: union(tags, {
      'azd-service-name': 'hub'
    })
    serverFarmResourceId: appServicePlanId
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [
        identityId
      ]
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentStorageContainerName}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: identityId
          }
        }
      }
      scaleAndConcurrency: {
        instanceMemoryMB: instanceMemoryMB
        maximumInstanceCount: maximumInstanceCount
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
    siteConfig: {
      alwaysOn: false
    }
    appSettingsKeyValuePairs: allAppSettings
  }
}

output SERVICE_API_NAME string = app.outputs.name
