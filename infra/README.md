# Secure Upload infrastructure

This directory deploys Unit 5 only: Azure hosting, private data-plane
authorization, Defender for Storage scanning, Event Grid delivery, retention, and
monitoring. It does not deploy application binaries, create Entra registrations,
or add application telemetry code.

## Prerequisites

- Registered providers: `Microsoft.Authorization`, `Microsoft.EventGrid`,
  `Microsoft.Insights`, `Microsoft.OperationalInsights`, `Microsoft.Security`,
  `Microsoft.Network`, `Microsoft.Storage`, and `Microsoft.Web`.
- Owner or User Access Administrator plus permissions to configure Defender for
  Storage and create a subscription-scoped custom role.
- Existing single-tenant API registration and host workload identity. Pass the
  host identity's **object ID** as `hostPrincipalId` and ARM resource ID as
  `hostIdentityResourceId`; no credential is accepted. For a service principal
  without an ARM resource, use the ARM resource ID of the workload that owns it
  for traceability while continuing to use the service principal object ID for
  RBAC.
- Existing virtual network with available address space for a delegated `/26`
  App Service integration subnet and a separate private-endpoint subnet. The
  deployment identity needs permission to create subnets in that VNet. If the
  VNet is in another subscription, register `Microsoft.Network` there and grant
  the deployment identity subnet-write and join permissions in that subscription.
- Existing centrally managed `privatelink.blob.core.windows.net`,
  `privatelink.queue.core.windows.net`, and
  `privatelink.table.core.windows.net` private DNS zones. Pass their full ARM
  resource IDs and ensure the existing VNet is linked to all three zones.
- Confirm that the selected region exposes App Service `.NET 10`, Azure Functions
  `.NET 10 isolated`, Defender on-upload scanning, and Event Grid custom topics.

## Entra objects that Bicep does not create

Resource-group Bicep cannot create or safely govern the tenant-level application
registrations and role assignments required by this solution. Provision the
following objects first.

### 1. Secure Upload API registration

Create a single-tenant app registration that represents the protected host status
API in `SecureUpload.Web`.

1. In **Microsoft Entra ID → App registrations → New registration**, enter
   `Secure Upload API`, select **Accounts in this organizational directory only**,
   and leave Redirect URI empty.
2. Record the **Application (client) ID** and **Directory (tenant) ID**.
3. Under **Expose an API**, set the Application ID URI, normally
   `api://<api-application-client-id>`.
4. Under **App roles**, create:
   - Display name: `Read secure upload status`
   - Allowed member types: `Applications`
   - Value: `SecureUpload.Status.Read`
   - Description: `Read secure upload status from the host API`
   - Enabled: Yes
5. Do not create delegated scopes. The host endpoint accepts application tokens,
   not user-delegated tokens.

Map the values to:

```bicep
param tenantId = '<directory-tenant-id>'
param apiAudience = 'api://<api-application-client-id>'
param requiredHostRole = 'SecureUpload.Status.Read'
```

### 2. Host workload identity

A user-assigned managed identity is recommended for the host backend:

```powershell
$hostIdentity = az identity create `
  --resource-group <host-resource-group> `
  --name secure-upload-host `
  --query '{clientId:clientId,principalId:principalId,id:id}' `
  | ConvertFrom-Json
```

The three returned identifiers have different purposes:

- `clientId` becomes `allowedHostClientApplicationId` and identifies the token
  caller.
- `principalId` becomes `hostPrincipalId` and receives the clean-container RBAC
  role.
- `id` becomes `hostIdentityResourceId` and records the Azure resource that owns
  the identity.

Assign the API application role to the managed identity:

```powershell
$apiClientId = '<api-application-client-id>'
$apiEnterpriseAppId = az ad sp show --id $apiClientId --query id -o tsv
$apiRoleId = az ad app show --id $apiClientId `
  --query "appRoles[?value=='SecureUpload.Status.Read'].id | [0]" -o tsv

$assignment = @{
  principalId = $hostIdentity.principalId
  resourceId = $apiEnterpriseAppId
  appRoleId = $apiRoleId
} | ConvertTo-Json -Compress

az rest --method POST `
  --url "https://graph.microsoft.com/v1.0/servicePrincipals/$($hostIdentity.principalId)/appRoleAssignments" `
  --headers 'Content-Type=application/json' `
  --body $assignment
```

The identity that calls this command needs permission to assign application
roles in Entra. Verify the assignment before deploying:

```powershell
az rest --method GET `
  --url "https://graph.microsoft.com/v1.0/servicePrincipals/$($hostIdentity.principalId)/appRoleAssignments" `
  --query "value[?resourceId=='$apiEnterpriseAppId' && appRoleId=='$apiRoleId']"
```

### 3. Management registration and operator group

Create a separate single-tenant app registration for interactive management-site
sign-in. It requires:

- Application ID URI `api://<management-client-id>`;
- app role `SecureUpload.Management` with member type `Users/Groups`;
- redirect URI
  `https://<management-app-name>.azurewebsites.net/.auth/login/aad/callback`;
- front-channel logout URL
  `https://<management-app-name>.azurewebsites.net/.auth/logout`;
- ID-token issuance enabled and access-token issuance disabled under
  **Authentication → Implicit grant and hybrid flows**;
- **Assignment required = Yes** on its enterprise application;
- exactly one approved security group assigned to the management role;
- no direct user or service-principal role assignments.

The management registration also needs a client secret. Store its value in an
existing Key Vault and pass only the versioned secret URI to Bicep. See the
[management app guide](../docs/integration/management-app-guide.md) for the full
portal and Azure CLI procedure.

### 4. Existing Key Vault

The template deliberately does not create the Key Vault or client secret. The
vault must:

- use Azure RBAC authorization;
- contain the management app's client secret;
- be reachable by the management App Service;
- allow the deployment identity to create the management identity's
  `Key Vault Secrets User` role assignment.

Bicep grants the deployed management site's managed identity secret-read access.
It does not grant that access to the uploader, processor, host, or operators.

## Deployment order

Copy the committed example before configuring an environment:

```powershell
Copy-Item .\infra\main.example.bicepparam .\infra\main.bicepparam
```

`main.bicepparam` is intentionally ignored by Git because it contains tenant,
subscription, identity, network, Key Vault, and notification values. Commit only
`main.example.bicepparam`, which contains placeholders and safe defaults. The
parameter file must never contain a client-secret value; it contains only the
versioned Key Vault secret URI.

The template creates Storage and monitoring, the Defender result topic/settings,
the App Service plan/apps and managed identities, RBAC, and then event delivery.
Azure RBAC can take up to 10 minutes to propagate. A 403 immediately after
deployment must be retried with backoff and must not be worked around by enabling
shared-key access.

An Azure Function destination must already contain the named function. Therefore
a fresh environment is deployed in two automated passes:

1. Deploy `main.bicepparam` with `enableEventSubscription=false`.
2. Publish `SecureUpload.Processor` so `ProcessScanResult` exists.
3. Redeploy with `enableEventSubscription=true`.
4. Wait for RBAC propagation, restart the apps if they started before propagation,
   and run identity/data-plane smoke checks.

This is a runtime artifact dependency, not manual Azure resource creation.
Leaving `enableEventSubscription=false` prevents scan results from reaching the
processor and is not production-ready.

```powershell
az bicep build --file .\infra\main.bicep
az deployment group validate `
  --resource-group <resource-group> `
  --parameters .\infra\main.bicepparam
az deployment group create `
  --resource-group <resource-group> `
  --parameters .\infra\main.bicepparam
```

## Security and access

- The Storage account rejects anonymous Blob access, shared-key authorization,
  HTTP, cross-tenant replication, NFS, SFTP, and TLS below 1.2.
- Both Storage accounts disable public network access. The web and Function apps
  route through a delegated VNet integration subnet to private endpoints for the
  data account's Blob/Table services and the Function host account's
  Blob/Queue/Table services.
- Web: custom create/commit/delete-without-read access on `pending`, plus Table
  Data Contributor scoped separately to `filestatus` and `uploadadmission`.
- Processor: Blob Data Contributor on pending/clean/quarantine, Table Data
  Contributor on status, plus the documented Function-host Blob Data Owner and
  Table Data Contributor roles on the host Storage account.
- Host: a custom role scoped only to `clean`, with blob read/list semantics and
  delete, and explicit write/add/move exclusions.
- Event Grid topic identity: Blob Data Contributor on the data Storage account.
  Event Grid validates dead-letter authorization at account scope even though the
  configured dead-letter destination is the dedicated container.

Function host storage uses `AzureWebJobsStorage__*ServiceUri` and
`AzureWebJobsStorage__credential=managedidentity`; there is no storage connection
string. Application Insights local authentication is disabled and both apps use
managed-identity ingestion.

## Defender and Event Grid

`Microsoft.Security/defenderForStorageSettings@2025-06-01` is the newest stable
documented API that includes on-upload filters, the monthly cap, Blob scan-result
behavior, and a custom result topic. Clean and quarantine are excluded with
container prefixes. Scan results are not written to blob index tags because the
processor trusts only the validated Event Grid event. The custom topic is
same-region, uses the default Event Grid schema, and must allow public network
access for Defender.

Every scan result is also exported through the service-owned Defender diagnostic
surface to `StorageMalwareScanningResults` in Log Analytics. The template does not
declare, rename, assign roles to, or delete Defender-managed scanner resources or
system topics.

The Function subscription accepts only
`Microsoft.Security.MalwareScanningResult`, delivers one event per batch, uses a
bounded retry/TTL policy, and dead-letters with the topic's managed identity.

## Retention and monitoring

Only `quarantine/` has a lifecycle delete rule (30 days by default). Clean blobs
have no expiry. Workspace-based Application Insights, Log Analytics, an action
group, a scan-error query alert, and a platform-failure query alert are created.
Email receivers are parameterized and may be empty for nonproduction validation.

## API versions

- Storage account/services/containers/table/lifecycle: `2025-01-01`
- App Service plan and sites: `2025-03-01`
- Event Grid topic and subscription: `2025-02-15`
- Defender for Storage: `2025-06-01`
- Defender diagnostics: `2021-05-01-preview` (only published diagnostic-settings
  API supporting the documented nested Defender `ScanResults` destination)
- Log Analytics: `2025-07-01`
- Application Insights: `2020-02-02`
- Action group: `2023-01-01`
- Scheduled query rules: `2025-01-01-preview`
- Role assignments: `2022-04-01`
- Custom role definition: `2022-05-01-preview`

## Validation and smoke checks

The files under `tests` compile the full module graph and assert sample input and
security invariants. They use Bicep's assertions experimental feature through a
test-local `bicepconfig.json`; production templates do not enable experimental
features. ARM doesn't permit `reference()` (module outputs) inside template
assertions, so deployed resource properties are additionally covered by ARM
validation and the post-deployment authorization smoke checks below.
`main.bicep` uses the stable `fail()` flow-control function for origin, extension,
media type, budget, and cap validation that parameter decorators cannot express.
After deployment, verify:

1. shared-key and anonymous Blob requests fail;
2. web can create/delete pending blobs, update status, and atomically reserve
   distributed admission budgets in `uploadadmission`, but cannot read clean;
3. processor host starts without a connection string and can process Timer/Event
   Grid triggers;
4. host can list/read/delete clean but receives authorization failure on create,
   overwrite, pending, and quarantine;
5. a benign pending upload produces one default-schema event and Log Analytics
   row; clean/quarantine copies do not cause actionable rescan loops;
6. an intentionally failing handler exhausts the configured budget and writes to
   the dead-letter container;
7. redeployment produces no replacement of Storage or broader role assignments.
