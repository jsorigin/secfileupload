# Secure Upload

Secure Upload is a .NET 10 iframe uploader that streams anonymous files to private
Azure Blob Storage, keeps them inaccessible while Microsoft Defender for Storage
scans them, and publishes only verified-clean files. An isolated Azure Function
processes Defender Event Grid results with optimistic concurrency and resumable
copy/delete recovery.

## Solution

- `src/SecureUpload.Web` — iframe UI, anonymous `POST /api/uploads`, capability
  polling, and Entra-protected host status.
- `src/SecureUpload.Management` — Entra-protected operator inventory, exact-ID
  lookup, clean download, and deletion requests.
- `src/SecureUpload.Processor` — Event Grid scan processing and stale-pending
  watchdog plus pending-deletion cleanup reconciliation.
- `src/SecureUpload.Core` — lifecycle and Azure Blob/Table adapters.
- `infra` — modular Bicep for hosting, Storage, Defender, Event Grid, identities,
  monitoring, and retention.
- `tests/SecureUpload.EndToEnd.Tests` — deterministic cross-component lifecycle,
  authorization-boundary, management-flow, race, and recovery tests.
- `tests/SecureUpload.Browser.Tests` — Playwright Chromium iframe tests.

## Build and test

```powershell
dotnet build .\SecureUpload.slnx
dotnet test .\SecureUpload.slnx

Set-Location .\tests\SecureUpload.Browser.Tests
npm install
npx playwright install chromium
npm test
```

Azurite-backed tests run when `AZURITE_BLOB_CONNECTION_STRING` and/or
`AZURITE_TABLE_CONNECTION_STRING` are set. Tests without those variables use
deterministic in-process stores. Azure remains required to prove Defender delivery,
managed identity, RBAC, lifecycle retention, and App Service proxy behavior.

## Microsoft Entra setup

The deployment does not create tenant-level Microsoft Entra objects. Create these
before running the Bicep deployment:

1. A **Secure Upload API** single-tenant app registration. It represents the
   protected host status API exposed by `SecureUpload.Web`.
2. A host workload identity. A user-assigned managed identity is recommended.
   Assign that identity the API application role `SecureUpload.Status.Read`.
3. A separate **Secure Upload Management** single-tenant app registration for
   interactive operator sign-in, with ID-token issuance enabled for App Service
   Easy Auth and access-token implicit issuance disabled.
4. One Entra security group assigned to the management application role
   `SecureUpload.Management`.
5. A client credential for the management registration, stored as a versioned
   secret in an existing RBAC-enabled Azure Key Vault.

Do not use the same app registration for the host API and management site. They
have different callers and trust boundaries:

| Registration | Caller | Role member type | Role value |
|---|---|---|---|
| Secure Upload API | Host backend workload identity | Applications | `SecureUpload.Status.Read` |
| Secure Upload Management | Approved operator security group | Users/Groups | `SecureUpload.Management` |

The Bicep parameters map to these objects as follows:

| Parameter | Entra value |
|---|---|
| `tenantId` | Tenant ID containing the Secure Upload API registration |
| `apiAudience` | Application ID URI of the Secure Upload API |
| `allowedHostClientApplicationId` | Client ID of the host workload identity |
| `hostPrincipalId` | Object/principal ID of the host workload identity |
| `hostIdentityResourceId` | ARM resource ID of the host managed identity |
| `managementTenantId` | Tenant ID containing the management registration |
| `managementClientId` | Client ID of the management registration |
| `managementAudience` | Application ID URI of the management registration |
| `managementIssuer` | `https://login.microsoftonline.com/<tenant-id>/v2.0` |

Follow the detailed provisioning instructions before deployment:

- [Host API registration and workload role assignment](docs/integration/host-backend-guide.md#provision-the-entra-api-and-host-workload)
- [Management registration, group assignment, and Key Vault credential](docs/integration/management-app-guide.md#create-the-management-entra-application)

## Integration and operations

- [Iframe host guide](docs/integration/iframe-host-guide.md)
- [Host backend guide](docs/integration/host-backend-guide.md)
- [Management app guide](docs/integration/management-app-guide.md)
- [Azure deployment guide](docs/deployment/azure-deployment-guide.md)
- [Operator runbook](docs/operations/secure-upload-runbook.md)
- [Alerts](docs/operations/secure-upload-alerts.md)
- [Infrastructure reference](infra/README.md)

Only a validated clean scan event can make a file available. Never manually copy a
pending or quarantined blob into `clean`. Routine operator actions belong in the
management app, not direct Storage tooling.
