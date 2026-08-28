# Azure deployment guide

## Prerequisites

1. An Azure subscription and region supporting Linux App Service .NET 10,
   Functions .NET 10 isolated, Storage, Event Grid custom topics, Defender for
   Storage on-upload scanning, Log Analytics, and Application Insights.
2. Registered providers listed in [`infra/README.md`](../../infra/README.md).
3. Permission to configure Defender, create role assignments, create the
   subscription-scoped custom clean-container role, and grant Key Vault secret
   read access to the management site identity.
4. An existing single-tenant Entra API registration for the host workload, plus
   a host workload service principal or managed identity assigned the host role.
5. An existing single-tenant Entra app registration and enterprise application
   for the management site:
   - client ID mapped to `managementClientId`;
   - Application ID URI mapped to `managementAudience`;
   - issuer `https://login.microsoftonline.com/<managementTenantId>/v2.0`;
   - app role value `SecureUpload.Management` (or the configured
     `managementRequiredRole`);
   - **User assignment required = Yes**;
   - **exactly one approved security group** assigned to the role;
   - **no direct user** assignment and **no direct service-principal** assignment;
   - redirect URI `https://<management-host>/.auth/login/aad/callback`;
   - front-channel logout URL `https://<management-host>/.auth/logout`;
   - **ID token issuance enabled** and **access token issuance disabled** under
     **Authentication → Implicit grant and hybrid flows**, as required by the
     App Service Easy Auth login request.
6. The management app's client credential stored only in the existing Key Vault
   referenced by `managementAuthKeyVaultResourceId`, with the
   **versioned** secret URI copied into `managementAuthClientSecretUri`.
7. A Defender cost decision: confirm regional availability, expected scan volume,
   current pricing, and `defenderMonthlyGbCap` before enabling anonymous ingress.
8. An existing VNet with two unused CIDR ranges: `/26` or larger for App Service
   integration and a separate range for five Storage private endpoints.
9. Centrally managed Blob, Queue, and Table private DNS zones already linked to
   the VNet. The deployment identity needs permission to create zone groups that
   reference those zones.
10. `Microsoft.Network` registered in the deployment subscription and, for a
    cross-subscription VNet, in the VNet subscription. Grant the deployment
    identity subnet write/join access in the VNet subscription and private DNS
    zone-group access at the central DNS zone scope.

See the [management app guide](../integration/management-app-guide.md) for the
full Entra setup, assignment validation, Key Vault rotation, and forged-header
smoke procedure.

## Configure parameters

Copy `infra/main.bicepparam` and set environment-specific values. Important groups:

- naming/location/SKUs: `location`, `environmentName`, `resourceNamePrefix`,
  both Storage account names, app names, plan SKU, and plan capacity;
- browser and upload policy: `allowedOrigins`, `allowedExtensions`,
  `allowedMediaTypes`, `maximumFileSizeBytes`;
- admission: per-IP rate/window, concurrency, global request/byte budgets,
  Defender cap, polling, watchdog, and `uploadsEnabled`;
- host identity: `tenantId`, `apiAudience`, `allowedHostClientApplicationId`,
  `requiredHostRole`, `hostPrincipalId`, and `hostIdentityResourceId`;
- management identity: `managementTenantId`, `managementClientId`,
  `managementAudience`, `managementIssuer`, `managementRequiredRole`,
  `managementInventoryCapacity`, `managementAuthKeyVaultResourceId`,
  `managementAuthClientSecretUri`, and `managementAuthCredentialSettingName`;
- networking: existing VNet subscription/resource group/name, both subnet names
  and CIDRs, and the full Blob/Queue/Table private DNS zone resource IDs;
- operations: quarantine retention, Event Grid retry/TTL, alert recipients, and
  container/table names, including the separate `uploadAdmissionTableName`.

Parameters and outputs contain identifiers, not client secrets or storage keys.
`managementAuthClientSecretUri` must stay versioned so credential rotation is an
explicit deployment event.

## Validate locally

```powershell
az bicep build --file .\infra\main.bicep
az bicep build --file .\infra\tests\main.test.bicep
az bicep build --file .\infra\tests\security.test.bicep

az deployment group validate `
  --resource-group <resource-group> `
  --parameters .\infra\main.bicepparam
```

ARM validation requires Azure access. The test templates use
`infra/tests/bicepconfig.json` and Bicep assertions for static invariants.

## Required two-pass deployment

An Event Grid Azure Function destination cannot be created until the deployed
Function artifact exposes `ProcessScanResult`.

1. Set `enableEventSubscription=false` and deploy `infra/main.bicepparam`.
2. Publish the uploader, management site, and processor:

```powershell
dotnet publish .\src\SecureUpload.Web -c Release -o .\artifacts\web
dotnet publish .\src\SecureUpload.Management -c Release -o .\artifacts\management
dotnet publish .\src\SecureUpload.Processor -c Release -o .\artifacts\processor

Compress-Archive -Path .\artifacts\web\* -DestinationPath .\artifacts\web.zip -Force
Compress-Archive -Path .\artifacts\management\* -DestinationPath .\artifacts\management.zip -Force
Compress-Archive -Path .\artifacts\processor\* -DestinationPath .\artifacts\processor.zip -Force
```

3. Deploy the published artifacts to the app names from the parameter file:

```powershell
az webapp deploy `
  --resource-group <resource-group> `
  --name <webAppName> `
  --src-path .\artifacts\web.zip `
  --type zip

az webapp deploy `
  --resource-group <resource-group> `
  --name <managementAppName> `
  --src-path .\artifacts\management.zip `
  --type zip

az functionapp deployment source config-zip `
  --resource-group <resource-group> `
  --name <functionAppName> `
  --src .\artifacts\processor.zip
```

4. Verify the Function App exposes `ProcessScanResult`,
   `DetectStalePendingFiles`, and `ProcessPendingDeletions`.
5. Set `enableEventSubscription=true` and redeploy the same template/parameters.
6. Wait for Azure RBAC propagation (commonly up to 10 minutes), restart apps if
   they initialized before propagation, then run smoke checks.

```powershell
az deployment group create `
  --resource-group <resource-group> `
  --parameters .\infra\main.bicepparam
```

`enableEventSubscription=false` is not production-ready because Defender results
cannot reach the processor.

## Smoke validation

After RBAC propagation:

1. Verify anonymous Blob and shared-key requests fail and all containers are
   private.
2. Verify web can create/delete pending, update status, and update only the
   dedicated upload-admission table for shared request/byte/Defender budgets, but
   cannot read clean.
3. Verify processor starts using identity-based Function host Storage and can
   access pending, clean, quarantine, and status.
4. Verify host can list/read/delete clean but cannot create/overwrite it or access
   pending/quarantine. The Event Grid topic identity has account-scoped Blob Data
   Contributor because Event Grid requires account-scope authorization validation
   for dead-lettering.
5. Verify the management site can sign in an approved-group user, browse the
   bounded inventory, open an exact file ID, download a clean file, request
   deletion, and observe the `Deleted` tombstone. The management site must not
   receive pending/quarantine access or status-row create/delete rights.
6. Verify a tenant user outside the approved group receives `403` and no
   inventory body.
7. Verify forged caller-supplied identity headers are rejected at the public
   management endpoint:

```powershell
curl.exe -i https://<management-host>/ `
  -H "X-MS-CLIENT-PRINCIPAL: eyJmb3JnZWQiOiJ0cnVlIn0="
```

Expected result: redirect to `/.auth/login/aad`, or an Easy Auth `401/403`, and
never `200 OK` with `Secure file inventory`.

8. Query `AppServiceAuthenticationLogs` for the management site and confirm the
   sign-in or denial signal reached the workspace.
9. Upload a benign fixture; confirm one
   `Microsoft.Security.MalwareScanningResult` default-schema event, an Event Grid
   delivery, an available status, a clean blob, and a
   `StorageMalwareScanningResults` row.
10. In an isolated nonproduction environment, upload the standard EICAR test
    file; confirm rejection/quarantine and no host or management clean download.
11. Exercise a not-scanned result and stale watchdog; confirm `scan-error` and
    alerts while content remains inaccessible.
12. Confirm clean/quarantine prefixes do not create actionable rescan loops and a
    forced delivery failure reaches dead-letter storage within the configured
    retry/TTL budget.

## Rollback and teardown

Rollback disables new ingress first with `Admission__Enabled=false` while keeping
the Function, Event Grid subscription, deletion timer, watchdog, blobs, and status
rows running. Roll back app artifacts or configuration, validate processing and
management sign-in, then re-enable ingress.

Do not treat rollback as teardown. Storage contains retained clean, pending,
quarantine, dead-letter, and audit/status data. Export or retain it according to
policy; destructive resource-group or data deletion requires a separate explicit
approval. Host-owned clean retention and quarantine lifecycle behavior must be
accounted for before teardown.

The request and byte windows and monthly admitted Defender bytes are stored in
`Storage__UploadAdmissionTableName` with optimistic concurrency. Every scaled-out
web instance shares that table; only active-upload concurrency remains local.
Storage or retry exhaustion fails admission closed with
`admission-store-unavailable`. Failed uploads release their Defender-byte
reservation, while request and byte work remains charged.
