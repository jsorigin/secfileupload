# Host backend integration

## Provision the Entra API and host workload

The host status endpoint uses OAuth 2.0 client credentials. Create one app
registration for the API and use a separate workload identity as its caller.
A user-assigned managed identity is preferred because it avoids another client
secret.

### Create the API registration in the portal

1. Open **Microsoft Entra ID → App registrations → New registration**.
2. Name it `Secure Upload API`.
3. Select **Accounts in this organizational directory only**.
4. Leave Redirect URI empty and select **Register**.
5. On **Overview**, record:
   - **Application (client) ID** as `<api-client-id>`;
   - **Directory (tenant) ID** as `<tenant-id>`.
6. Open **Expose an API → Add** beside Application ID URI.
7. Accept `api://<api-client-id>` or enter another unique URI that you will use
   consistently as `apiAudience`.
8. Open **App roles → Create app role** and enter:
   - Display name: `Read secure upload status`
   - Allowed member types: `Applications`
   - Value: `SecureUpload.Status.Read`
   - Description: `Read secure upload status from the host API`
   - Enabled: Yes

Do not add a delegated scope for this integration. The endpoint rejects delegated
tokens containing `scp`.

### Create or identify the host managed identity

For a new user-assigned managed identity:

```powershell
$hostIdentity = az identity create `
  --resource-group <host-resource-group> `
  --name secure-upload-host `
  --query '{clientId:clientId,principalId:principalId,id:id}' `
  | ConvertFrom-Json

$hostIdentity
```

For an existing identity:

```powershell
$hostIdentity = az identity show `
  --resource-group <host-resource-group> `
  --name <host-identity-name> `
  --query '{clientId:clientId,principalId:principalId,id:id}' `
  | ConvertFrom-Json
```

Assign the `SecureUpload.Status.Read` app role:

```powershell
$apiClientId = '<api-client-id>'
$apiEnterpriseAppId = az ad sp show --id $apiClientId --query id -o tsv
$apiRoleId = az ad app show --id $apiClientId `
  --query "appRoles[?value=='SecureUpload.Status.Read'].id | [0]" -o tsv

if ([string]::IsNullOrWhiteSpace($apiEnterpriseAppId) -or
    [string]::IsNullOrWhiteSpace($apiRoleId)) {
  throw 'The API enterprise application or app role could not be resolved.'
}

$assignmentBody = @{
  principalId = $hostIdentity.principalId
  resourceId = $apiEnterpriseAppId
  appRoleId = $apiRoleId
} | ConvertTo-Json -Compress

az rest --method POST `
  --url "https://graph.microsoft.com/v1.0/servicePrincipals/$($hostIdentity.principalId)/appRoleAssignments" `
  --headers 'Content-Type=application/json' `
  --body $assignmentBody
```

If the assignment already exists, Azure returns a conflict; verify the existing
assignment rather than creating a duplicate:

```powershell
$assignments = az rest --method GET `
  --url "https://graph.microsoft.com/v1.0/servicePrincipals/$($hostIdentity.principalId)/appRoleAssignments" `
  | ConvertFrom-Json

$assignments.value |
  Where-Object {
    $_.resourceId -eq $apiEnterpriseAppId -and $_.appRoleId -eq $apiRoleId
  } |
  Format-Table principalId, resourceId, appRoleId
```

Set the Bicep parameters:

```bicep
param tenantId = '<tenant-id>'
param apiAudience = 'api://<api-client-id>'
param allowedHostClientApplicationId = '<host-identity-client-id>'
param requiredHostRole = 'SecureUpload.Status.Read'
param hostPrincipalId = '<host-identity-principal-id>'
param hostIdentityResourceId = '/subscriptions/<subscription-id>/resourceGroups/<host-resource-group>/providers/Microsoft.ManagedIdentity/userAssignedIdentities/<host-identity-name>'
```

## Authentication

Call the status API with a Microsoft Entra **application token**, not a delegated
user token. The deployment expects:

- one tenant: `HostWorkloadAuthorization:TenantId`;
- API audience: `HostWorkloadAuthorization:Audience`;
- allowed client application ID:
  `HostWorkloadAuthorization:AllowedClientApplicationId`;
- application role: `HostWorkloadAuthorization:RequiredRole` (default
  `SecureUpload.Status.Read`).

Acquire a client-credentials token for the configured API audience using the host
workload identity. The API accepts v2 app tokens with `azp`, or v1 app tokens with
`appid`, only when `tid`, `iss`, `aud`, `idtyp=app`, client ID, and `roles` all
match. Tokens containing `scp`, missing the role, or representing another client,
tenant, or audience are denied.

For a managed identity, request a token for the API Application ID URI:

```powershell
az login --identity --client-id <host-identity-client-id>
az account get-access-token `
  --resource <api-application-id-uri> `
  --query accessToken -o tsv
```

The exact token acquisition mechanism depends on the host runtime. Use the Azure
Identity SDK in application code and request the API's `/.default` scope when the
SDK expects scopes:

```text
api://<api-client-id>/.default
```

## Query status

```http
GET /api/host/files/{fileId}/status
Authorization: Bearer <app-only-token>
Accept: application/json
```

`fileId` is exactly 64 lowercase hexadecimal characters. Responses use
`Cache-Control: no-store`. A known accepted file returns:

```json
{
  "fileId": "…",
  "status": "available",
  "fileName": "report.pdf",
  "mediaType": "application/pdf",
  "sizeBytes": 12345,
  "createdAt": "2026-08-13T12:00:00+00:00",
  "updatedAt": "2026-08-13T12:02:00+00:00",
  "uploadedAt": "2026-08-13T12:00:10+00:00",
  "scanCompletedAt": "2026-08-13T12:02:00+00:00"
}
```

Public `status` values are `pending`, `available`, `rejected`, and `scan-error`.
Internal processing states map to `pending`. Unknown, malformed, and failed-upload
IDs return `404` without querying by filename or exposing storage details.

## Read and delete clean content

The blob name in the configured clean container is the stable `fileId`. Use the
host Azure identity and the deployment outputs `storageAccountName` and
`cleanContainerName`; do not request a SAS or storage key.

The Bicep custom role is scoped to the clean container and permits blob read and
delete while explicitly excluding write/add/move. It provides no pending or
quarantine access. Treat `available` as status metadata: a later host deletion
correctly leaves the status row available, so handle a missing clean blob as
already consumed/deleted rather than retrying a write.

Clean files have no automatic expiry. The host owns deletion after successful
consumption. Never upload, replace, or manually promote content in `clean`.
