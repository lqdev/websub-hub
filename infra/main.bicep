targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Short azd environment name used to derive unique resource names.')
param environmentName string

@minLength(1)
@metadata({
  azd: {
    type: 'location'
  }
})
param location string

@description('Comma-separated list of allowed topic URL prefixes.')
param allowedTopicPrefixes string = 'https://lqdev.me/,https://www.lqdev.me/,https://luisquintanilla.me/,https://www.luisquintanilla.me/'

@allowed([
  'true'
  'false'
])
@description('Allow topics outside the prefix allowlist. Keep false for a personal hub.')
param openMode string = 'false'

@secure()
@minLength(32)
@description('Random secret required on hub.mode=publish requests via X-WebSub-Publish-Token.')
param publishToken string

@description('Optional public hub URL override. Leave empty to use the Function App default hostname.')
param hubPublicUrl string = ''

param resourceGroupName string = ''
param hubAppName string = ''

var abbrs = loadJsonContent('./abbreviations.json')
var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))
var tags = {
  'azd-env-name': environmentName
}
var functionAppName = !empty(hubAppName) ? hubAppName : '${abbrs.webSitesFunctions}websub-${resourceToken}'
var deploymentStorageContainerName = 'app-package-${take(functionAppName, 32)}-${take(toLower(uniqueString(functionAppName, resourceToken)), 7)}'
var publicHubUrl = !empty(hubPublicUrl) ? hubPublicUrl : 'https://${functionAppName}.azurewebsites.net/'

resource rg 'Microsoft.Resources/resourceGroups@2021-04-01' = {
  name: !empty(resourceGroupName) ? resourceGroupName : '${abbrs.resourcesResourceGroups}${environmentName}'
  location: location
  tags: tags
}

module identity 'br/public:avm/res/managed-identity/user-assigned-identity:0.4.1' = {
  name: 'hub-identity'
  scope: rg
  params: {
    name: '${abbrs.managedIdentityUserAssignedIdentities}websub-${resourceToken}'
    location: location
    tags: tags
  }
}

module plan 'br/public:avm/res/web/serverfarm:0.1.1' = {
  name: 'hub-flex-plan'
  scope: rg
  params: {
    name: '${abbrs.webServerFarms}${resourceToken}'
    sku: {
      name: 'FC1'
      tier: 'FlexConsumption'
    }
    reserved: true
    location: location
    tags: tags
  }
}

module storage 'br/public:avm/res/storage/storage-account:0.8.3' = {
  name: 'hub-storage'
  scope: rg
  params: {
    name: '${abbrs.storageStorageAccounts}${resourceToken}'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    dnsEndpointType: 'Standard'
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
    blobServices: {
      containers: [
        {
          name: deploymentStorageContainerName
        }
        {
          name: 'websub-payloads'
        }
      ]
    }
    minimumTlsVersion: 'TLS1_2'
    location: location
    tags: tags
  }
}

module logAnalytics 'br/public:avm/res/operational-insights/workspace:0.11.1' = {
  name: 'hub-log-analytics'
  scope: rg
  params: {
    name: '${abbrs.operationalInsightsWorkspaces}${resourceToken}'
    location: location
    tags: tags
    dataRetention: 30
  }
}

module monitoring 'br/public:avm/res/insights/component:0.6.0' = {
  name: 'hub-app-insights'
  scope: rg
  params: {
    name: '${abbrs.insightsComponents}${resourceToken}'
    location: location
    tags: tags
    workspaceResourceId: logAnalytics.outputs.resourceId
    disableLocalAuth: true
  }
}

module hub './app/hub.bicep' = {
  name: 'hub-function-app'
  scope: rg
  params: {
    name: functionAppName
    location: location
    tags: tags
    applicationInsightsName: monitoring.outputs.name
    appServicePlanId: plan.outputs.resourceId
    storageAccountName: storage.outputs.name
    deploymentStorageContainerName: deploymentStorageContainerName
    identityId: identity.outputs.resourceId
    identityClientId: identity.outputs.clientId
    appSettings: {
      WEBSUB_HUB_URL: publicHubUrl
      WEBSUB_ALLOWED_TOPIC_PREFIXES: allowedTopicPrefixes
      WEBSUB_OPEN_MODE: openMode
      WEBSUB_PUBLISH_TOKEN: publishToken
      WEBSUB_ALLOW_LOCAL_URLS: 'false'
      WEBSUB_DEFAULT_LEASE_SECONDS: '864000'
      WEBSUB_MIN_LEASE_SECONDS: '3600'
      WEBSUB_MAX_LEASE_SECONDS: '2592000'
      WEBSUB_MAX_TOPIC_BYTES: '5242880'
    }
  }
}

module rbac './app/rbac.bicep' = {
  name: 'hub-rbac'
  scope: rg
  params: {
    storageAccountName: storage.outputs.name
    appInsightsName: monitoring.outputs.name
    managedIdentityPrincipalId: identity.outputs.principalId
  }
}

output AZURE_LOCATION string = location
output SERVICE_HUB_NAME string = hub.outputs.SERVICE_API_NAME
output HUB_URL string = publicHubUrl
output AZURE_RESOURCE_GROUP string = rg.name
