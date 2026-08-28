# Management app tenant and deployment guide

The management app is the routine operator surface for inventory, exact file-ID
lookup, clean download, and deletion requests. It is not a substitute for direct
Blob or Table access. Routine operations stay in the app. Direct Storage access is
incident-only, time-bound, and separately approved.

## Create the management Entra application

Provision the registration before deploying the management App Service. The
management registration must be separate from the host API registration because
it represents an interactive browser application rather than an app-only API.

### Choose the final management hostname

Determine `managementAppName` first. The default App Service hostname is:

```text
https://<managementAppName>.azurewebsites.net
```

Use that hostname when configuring redirect and logout URLs. Add custom-domain
URLs later if applicable; do not replace the default callback until the custom
domain and certificate are active.

### Create the registration in the Entra portal

1. Open the Azure portal and go to
   **Microsoft Entra ID → App registrations → New registration**.
2. Enter a recognizable name such as `Secure Upload Management`.
3. Under **Supported account types**, select
   **Accounts in this organizational directory only**.
4. Under **Redirect URI**:
   - Platform: `Web`
   - URI:
     `https://<managementAppName>.azurewebsites.net/.auth/login/aad/callback`
5. Select **Register**.
6. On **Overview**, record:
   - **Application (client) ID** as `<management-client-id>`;
   - **Directory (tenant) ID** as `<management-tenant-id>`.

### Set the Application ID URI

1. Open **Expose an API**.
2. Select **Add** next to Application ID URI.
3. Accept `api://<management-client-id>` unless your tenant requires a verified
   custom URI.
4. Record the exact URI as `<management-audience>`.

No delegated scope is required. Easy Auth uses this URI as an allowed audience;
the application authorizes operators using the app role below.

### Create the management app role

1. Open **App roles → Create app role**.
2. Enter:
   - Display name: `Secure Upload Management`
   - Allowed member types: `Users/Groups`
   - Value: `SecureUpload.Management`
   - Description: `Manage secure uploads and request file deletion`
   - Enabled: Yes
3. Select **Apply**.

The role **value**, not its display name, must exactly match
`managementRequiredRole`. Do not add `Applications` as an allowed member type.

### Configure authentication URLs

1. Open **Authentication**.
2. Under **Web → Redirect URIs**, confirm:
   `https://<managementAppName>.azurewebsites.net/.auth/login/aad/callback`.
3. Add the equivalent callback for each active custom hostname:
   `https://<management-custom-domain>/.auth/login/aad/callback`.
4. Set **Front-channel logout URL** to:
   `https://<managementAppName>.azurewebsites.net/.auth/logout`.
5. Under **Implicit grant and hybrid flows**, select **ID tokens**. Leave
   **Access tokens** cleared. The App Service Easy Auth runtime requests
   `response_type=id_token`; without ID-token issuance, Entra rejects sign-in
   with `AADSTS700054` before the callback can establish a session.
6. Select **Save**.

### Require assignment on the enterprise application

Creating the app registration also creates an enterprise application:

1. Open **Microsoft Entra ID → Enterprise applications**.
2. Search for `Secure Upload Management` and open it.
3. Open **Properties**.
4. Set **Assignment required?** to **Yes**.
5. Select **Save**.

This platform gate prevents unassigned tenant users from completing sign-in. The
application independently checks tenant, user object ID, and role claims.

### Create and assign the operator security group

Use a dedicated security group rather than a Microsoft 365 group:

1. Open **Microsoft Entra ID → Groups → New group**.
2. Group type: `Security`.
3. Enter a name such as `Secure Upload Administrators`.
4. Choose assigned or dynamic membership according to your tenant policy.
5. Add the approved operators and create the group.
6. Return to **Enterprise applications → Secure Upload Management**.
7. Open **Users and groups → Add user/group**.
8. Select exactly that security group.
9. Select the `Secure Upload Management` role.
10. Select **Assign**.

Do not assign individual users, additional groups, or service principals. Group
membership should be the only routine access-management surface.

### Create the confidential-client credential

1. Return to **App registrations → Secure Upload Management**.
2. Open **Certificates & secrets → Client secrets → New client secret**.
3. Use a descriptive name such as `app-service-easy-auth-2026-08`.
4. Select the shortest expiration period allowed by your policy.
5. Select **Add**.
6. Copy the secret **Value** immediately. Do not copy the secret ID.
7. Store the value in Key Vault as described below, then clear it from your
   clipboard and secure shell history.

Never put the secret value in `main.bicepparam`, source control, deployment
outputs, tickets, or chat.

### Optional Azure CLI creation

The portal flow is recommended because app-role and enterprise-application
settings are easy to inspect. The following creates the base registration; role,
redirect, assignment-required, and group assignment still need verification:

```powershell
$managementAppName = '<managementAppName>'
$managementHost = "https://$managementAppName.azurewebsites.net"

$managementApp = az ad app create `
  --display-name 'Secure Upload Management' `
  --sign-in-audience AzureADMyOrg `
  --web-redirect-uris "$managementHost/.auth/login/aad/callback" `
  --query '{appId:appId,id:id}' `
  | ConvertFrom-Json

az ad sp create --id $managementApp.appId | Out-Null
az ad app update --id $managementApp.appId --enable-id-token-issuance true

$managementApp
```

Use the portal or Microsoft Graph to add the role without overwriting any existing
`appRoles`. After configuration, verify all settings using the commands below.

The app role assignment is an Entra prerequisite, not a Bicep-created resource.

## Parameter mapping

Map the Entra and Key Vault values into `infra/main.bicepparam`:

| Parameter | Required value |
|---|---|
| `managementTenantId` | Tenant GUID that owns the app registration and enterprise app |
| `managementClientId` | Management app registration Application (client) ID |
| `managementAudience` | Approved Application ID URI for the management registration |
| `managementIssuer` | `https://login.microsoftonline.com/<managementTenantId>/v2.0` |
| `managementRequiredRole` | App role value, normally `SecureUpload.Management` |
| `managementInventoryCapacity` | Browsing cap, maximum `10000` |
| `managementAuthKeyVaultResourceId` | Existing Key Vault ARM resource ID |
| `managementAuthClientSecretUri` | **Versioned** Key Vault secret URI for the current client credential |
| `managementAuthCredentialSettingName` | App setting name consumed by Easy Auth, normally `MICROSOFT_PROVIDER_AUTHENTICATION_SECRET` |

`managementAudience` should match the Application ID URI accepted by
`authsettingsV2.validation.allowedAudiences`. Do not put the client secret value in
the parameter file, deployment command line, or deployment outputs.

## Validate the role assignment shape

Before every initial deployment and every role-assignment change, verify that the
management role is assigned to one approved group and nothing else.

```powershell
$managementClientId = '<managementClientId>'
$approvedGroupObjectId = '<approved-group-object-id>'
$enterpriseAppObjectId = az ad sp show --id $managementClientId --query id -o tsv
$roleId = az ad app show --id $managementClientId `
  --query "appRoles[?value=='SecureUpload.Management'].id | [0]" -o tsv
$assignments = az rest --method GET `
  --url "https://graph.microsoft.com/v1.0/servicePrincipals/$enterpriseAppObjectId/appRoleAssignedTo?`$select=principalId,principalType,appRoleId" `
  | ConvertFrom-Json
$managementAssignments = $assignments.value | Where-Object { $_.appRoleId -eq $roleId }

$managementAssignments | Format-Table principalType, principalId
```

Required result:

- exactly **one** row;
- `principalType` is `Group`;
- `principalId` equals the approved security-group object ID.

Any direct `User` or `ServicePrincipal` assignment, or more than one group
assignment, is a deployment blocker.

Also verify the registration and enterprise application settings:

```powershell
$managementApp = az ad app show --id $managementClientId | ConvertFrom-Json
$managementSp = az ad sp show --id $managementClientId | ConvertFrom-Json

[pscustomobject]@{
  ClientId = $managementApp.appId
  TenantAudience = $managementApp.signInAudience
  IdentifierUri = ($managementApp.identifierUris -join ', ')
  RedirectUris = ($managementApp.web.redirectUris -join ', ')
  IdTokenIssuanceEnabled = $managementApp.web.implicitGrantSettings.enableIdTokenIssuance
  AccessTokenIssuanceEnabled = $managementApp.web.implicitGrantSettings.enableAccessTokenIssuance
  AppRoleAssignmentRequired = $managementSp.appRoleAssignmentRequired
}

$managementApp.appRoles |
  Where-Object { $_.value -eq 'SecureUpload.Management' } |
  Select-Object displayName, value, allowedMemberTypes, isEnabled
```

Required values:

- `TenantAudience` is `AzureADMyOrg`;
- the identifier URI exactly equals `managementAudience`;
- the callback URL exactly matches the deployed hostname;
- `IdTokenIssuanceEnabled` is `True`;
- `AccessTokenIssuanceEnabled` is `False`;
- `AppRoleAssignmentRequired` is `True`;
- the role is enabled and allows only `User`.

## Store the Easy Auth client credential in Key Vault

The current Bicep expects an existing Key Vault with Azure RBAC authorization.
If your platform team does not provide one, create it before deployment:

```powershell
$keyVault = az keyvault create `
  --resource-group <key-vault-resource-group> `
  --name <globally-unique-key-vault-name> `
  --location <azure-region> `
  --enable-rbac-authorization true `
  --query '{id:id,name:name}' `
  | ConvertFrom-Json
```

The operator running `az keyvault secret set` needs a Key Vault data-plane role
such as `Key Vault Secrets Officer`. Follow your organization's privileged-access
process rather than granting broad permanent access.

Store the credential under a dedicated name:

```powershell
$secretUri = az keyvault secret set `
  --vault-name $keyVault.name `
  --name secure-upload-management-auth `
  --value '<management-client-secret-value>' `
  --query id -o tsv

$secretUri
```

Copy the returned **versioned** URI into `managementAuthClientSecretUri` and the
vault resource ID into `managementAuthKeyVaultResourceId`.
Only the management site identity receives secret-read access. Do not grant web,
processor, host, or operator identities routine read access to the secret value.

After Bicep deploys the management site, it grants that site's system-assigned
identity the `Key Vault Secrets User` role. RBAC propagation can take several
minutes; restart the management App Service if it started before access propagated.

## Publish and deploy the management site

Follow the shared two-pass infrastructure flow in the
[Azure deployment guide](../deployment/azure-deployment-guide.md). The management
site is published separately from the uploader and processor:

```powershell
dotnet publish .\src\SecureUpload.Management -c Release -o .\artifacts\management
Compress-Archive -Path .\artifacts\management\* -DestinationPath .\artifacts\management.zip -Force

az webapp deploy `
  --resource-group <resource-group> `
  --name <managementAppName> `
  --src-path .\artifacts\management.zip `
  --type zip
```

Deploy the management artifact after the first infrastructure pass and before the
final smoke validation.

## Rotate the client credential

Rotate without exposing the value outside a secure operator shell:

```powershell
az ad app credential reset `
  --id <managementClientId> `
  --append `
  --display-name secure-upload-management-<yyyyMMdd>
```

Immediately store the returned `password` value in Key Vault, update
`managementAuthClientSecretUri` to the new versioned secret URI, redeploy the
resource group, restart the management site if needed, and run the smoke tests
below. After the new credential is verified, remove the prior credential:

```powershell
az ad app credential list --id <managementClientId> `
  --query "[].{keyId:keyId,displayName:displayName,endDateTime:endDateTime}" -o table

az ad app credential delete --id <managementClientId> --key-id <old-key-id>
```

Never delete the old credential until the new deployment passes sign-in, role, and
forged-header smoke validation.

## Smoke validation

Run these checks after RBAC propagation and every identity, role, or credential
change:

1. **Approved-group user** signs in and reaches `/`.
2. The same user can:
   - browse inventory;
   - filter results;
   - open a known file by exact ID;
   - download a clean file;
   - request deletion and observe `Deleting` then `Deleted`.
3. **Tenant user outside the approved group** receives `403` and no inventory body.
4. **Direct forged-header attempt** is rejected by App Service Authentication:

```powershell
curl.exe -i https://<management-host>/ `
  -H "X-MS-CLIENT-PRINCIPAL: eyJmb3JnZWQiOiJ0cnVlIn0="
```

Expected result: redirect to `/.auth/login/aad`, or an Easy Auth `401/403`, and
never `200 OK` with `Secure file inventory`.

5. Query `AppServiceAuthenticationLogs` for the management site and confirm the
   sign-in or denial event reached the Log Analytics workspace.
6. Re-run the uploader and processor smoke checks from the Azure deployment guide
   to confirm the management rollout did not broaden uploader/processor behavior.

## Operator guardrails

- Use the management app for routine exact-ID lookup, clean download, and deletion.
- Do not give operators routine direct Blob or Table access.
- During an over-cap incident, use exact-ID lookup only. Do not serve partial
  inventory results from another tool.
- If emergency access is approved, keep it time-bound, scoped, and documented in
  the runbook incident record.
