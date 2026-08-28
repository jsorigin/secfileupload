# Secure upload operations runbook

## Safety rules

1. **Never manually copy, rename, or promote a pending or quarantined blob into
   `clean`.** Only a validated Defender event processed by `ProcessScanResult`
   may make a file available.
2. Use the management app for routine inventory, exact file-ID lookup, clean
   download, and deletion. **Do not use direct Blob/Table access for routine
   operations.**
3. Do not paste stable IDs, blob paths, filenames, event bodies, malware names,
   tokens, keys, or connection strings into tickets or chat. Use the
   `secure_upload.operation_id` keyed correlation shown in telemetry, or the
   management app's exact-ID lookup inside an approved operator session.
4. Keep status and scan processing running during an incident. Disable new
   ingress first with `Admission__Enabled=false`; do not disable the Function,
   Event Grid subscription, deletion timer, or watchdog unless incident command
   explicitly accepts the resulting backlog.
5. Preserve the pending source and status row until the processor reaches a
   durable terminal state or an approved retention action removes it.

## Routine management workflow

1. Sign in with a tenant user from the single approved management security group.
2. Browse the inventory only while the page is healthy. If the site reports
   **Inventory browsing is paused**, stop global browsing and switch to exact-ID
   lookup.
3. For a known file, use **Look up an exact file ID** in the management app.
   That point query remains available during over-cap incidents.
4. Download only the clean copy exposed by the management app. Do not fetch a
   blob directly and do not request or create SAS URLs.
5. For deletion, request the operation in the management app and let the
   processor complete `Deleting -> Deleted`. Do not manually delete status rows.

## Triage

1. Acknowledge the alert and record environment, alert name, UTC window, and
   privacy-safe operation ID when available.
2. Query `AppTraces`, `AppMetrics`, `AppExceptions`, `AppRequests`,
   `AppServiceAuthenticationLogs`, and `StorageMalwareScanningResults` for the
   same time window. Use the management app's exact-ID lookup for file-specific
   checks instead of putting file IDs into shared queries.
3. Confirm the current status and blob placement using least-privilege,
   time-bound access. `available` must have a verified clean target; `rejected`
   must not have a clean target; `scan-error`, `deleting`, and `deleted` remain
   inaccessible through public and host paths.
4. Check Storage and Event Grid health, Defender configuration, scanner RBAC,
   monthly cap, Function health, dead-letter count, App Service plan pressure,
   and recent deployment or policy changes.

## Management-specific signals, owners, and rollback triggers

| Scenario | Primary owner | Validation window | Healthy signal | Failure signal | Mitigation | Rollback trigger |
|---|---|---|---|---|---|---|
| Inventory capacity exceeded | Service owner + cost owner | Two 15-minute alert windows | No `secure_upload.management.inventory_capacity_exceeded` hits; operators can browse inventory normally | Any capacity-exceeded hit; inventory page shows **Inventory browsing is paused** | Stop routine browsing, use exact-ID lookup only, assess retained-row growth and tombstones, plan approved retention work before the cap is hit again | Browsing is still blocked after the approved mitigation window and operations cannot continue with exact-ID lookup alone |
| Stuck `Deleting` / cleanup backlog | Service on-call | Two 5-minute alert windows | No cleanup failures and fewer than five retries per 15 minutes | Any `secure_upload.scan.deletion_cleanup_failure` hit, or repeated cleanup retries, or a details page stuck in `Deleting` beyond the bounded refresh window | Keep processor running, fix transient Storage issues, retry through the processor, and confirm the exact-ID page converges to `Deleted` | Backlog persists beyond 15 minutes, or dead letters start accumulating |
| Auth, role, or malformed-principal rejection | Tenant admin + service on-call | One successful smoke pass plus two 15-minute quiet windows | Approved-group user reaches `/`; outside-group user gets `403`; forged header gets `302/401/403`; denial alert stays quiet afterward | Approved user denied, no-role user unexpectedly admitted, forged header reaches protected content, or sustained `AppServiceAuthenticationLogs` / app 403 spikes | Re-check assignment-required, exact-one-group assignment, redirect/logout URIs, session lifetime, and current secret version; redeploy auth config if needed | Any fix would require bypassing Easy Auth or broadening app-role assignments |
| Management Storage RBAC failure | Service on-call + platform team | One successful smoke pass plus one 15-minute quiet window | No management `RequestFailedException` auth failures; clean download and deletion work from the management app | `AuthorizationPermissionMismatch`, `AuthorizationFailure`, `AuthorizationResourceTypeMismatch`, or `AuthenticationFailed` from the management app | Re-apply least-privilege RBAC and Key Vault secret-read access. Do **not** grant broader roles as a shortcut | Upload, scan, or host smoke flows regress, or the proposed fix would grant pending/quarantine or write/delete rights to management |
| Event Grid dead letters | Service on-call + security operations | Immediate, then until `DeadLetteredCount` returns to zero | `DeadLetteredCount` is zero and replay backlog is empty or accounted for | Any dead-letter alert or non-empty replay backlog | Restore downstream health, obtain replay approval, replay original events in bounded batches, and confirm one durable outcome per event | Replays cannot proceed without bypassing validation or the backlog continues growing |
| App Service plan pressure | Platform team + service owner | 30 minutes after deploy/scale change | Shared plan CPU, memory, and HTTP queue metrics stay within the environment baseline | Sustained high CPU/memory or growing HTTP queue on the shared plan after the management rollout | Scale the plan, reduce concurrent operational activity, or defer non-urgent management actions until pressure clears | The third app causes sustained plan pressure that degrades existing uploader or processor smoke flows |

## Privacy-safe investigation queries and search terms

Replace placeholders such as `<management-app-name>` and `<processor-app-name>`
before use.

### Inventory capacity

Healthy: zero hits. Failure: any hit means global browsing stopped and operators
should switch to exact-ID lookup.

```kusto
AppMetrics
| where TimeGenerated > ago(24h)
| where AppRoleName == "<management-app-name>"
| where Name == "secure_upload.management.inventory_capacity_exceeded"
| summarize Hits=sum(Sum) by bin(TimeGenerated, 15m)
```

### Stuck `Deleting` and cleanup failure

Healthy: no failures and retry volume below the alert threshold. Failure: any
cleanup failure or repeated retries. For a specific file, use the management app's
exact-ID lookup instead of querying Logs by file ID.

```kusto
AppMetrics
| where TimeGenerated > ago(4h)
| where AppRoleName == "<processor-app-name>"
| where Name in ("secure_upload.scan.deletion_cleanup_retry", "secure_upload.scan.deletion_cleanup_failure")
| summarize Total=sum(Sum) by Name, bin(TimeGenerated, 15m)
```

### Auth, role, and malformed-principal rejection

Healthy: approved user sign-in succeeds, outside-group sign-in denies, and forged
header attempts never produce a protected response body. Search terms:
`AppServiceAuthenticationLogs`, `401`, `403`, `Malformed App Service client principal header`.

```kusto
let Site = "<management-app-name>";
let EasyAuth =
    AppServiceAuthenticationLogs
    | where TimeGenerated > ago(4h)
    | where SiteName == Site and StatusCode in (401, 403)
    | project TimeGenerated, Source="EasyAuth", Status=tostring(StatusCode);
let AppDenials =
    AppRequests
    | where TimeGenerated > ago(4h)
    | where AppRoleName == Site and toint(ResultCode) == 403
    | project TimeGenerated, Source="AppRequests", Status=tostring(ResultCode);
union isfuzzy=true EasyAuth, AppDenials
| summarize Count=count() by Source, Status, bin(TimeGenerated, 15m)
```

### Management Storage RBAC failure

Healthy: zero authorization failures. Failure: any management-site Storage auth
exception.

```kusto
AppExceptions
| where TimeGenerated > ago(4h)
| where AppRoleName == "<management-app-name>"
| where ExceptionType == "RequestFailedException"
| where Message has_any ("AuthorizationPermissionMismatch", "AuthorizationFailure", "AuthorizationResourceTypeMismatch", "AuthenticationFailed")
   or InnermostMessage has_any ("AuthorizationPermissionMismatch", "AuthorizationFailure", "AuthorizationResourceTypeMismatch", "AuthenticationFailed")
| summarize Failures=count() by bin(TimeGenerated, 15m)
```

### Dead letters

Search terms: `DeadLetteredCount`, `defender-scan-results`, `eventgrid-deadletter`.
Healthy: zero dead-lettered events. Failure: any dead-lettered event is severity 1.
Use Azure Monitor Metrics on the Event Grid topic if `AzureMetrics` is not routed
to the workspace.

```kusto
AzureMetrics
| where TimeGenerated > ago(4h)
| where MetricName == "DeadLetteredCount"
| summarize Total=sum(Total) by Resource, bin(TimeGenerated, 15m)
```

### App Service plan pressure

Search terms: `CpuPercentage`, `MemoryPercentage`, `HttpQueueLength`. Healthy:
shared-plan metrics stay inside the environment baseline after the management
deployment. Failure: sustained high CPU/memory or queue growth.

```kusto
AzureMetrics
| where TimeGenerated > ago(4h)
| where MetricName in ("CpuPercentage", "MemoryPercentage", "HttpQueueLength")
| summarize Maximum=max(Maximum) by Resource, MetricName, bin(TimeGenerated, 15m)
```

If `AzureMetrics` is unavailable in Logs, inspect the same metric names in Azure
Monitor Metrics on the shared App Service plan.

## SAM result classes and recovery

Use the sanitized `sam-NNNNNN` failure class, not the full Defender reason or
malware name.

| Class | Meaning | Controlled action |
|---|---|---|
| `sam-259201` | Defender internal service error | Treat as transient. After service health recovers, request a supported on-demand scan or require re-upload. |
| `sam-259203` | Scanner could not access the blob | Inspect Activity Log and scanner permissions/policies, restore the supported Defender configuration, then rescan or re-upload. Never grant public/shared-key access. |
| `sam-259206`, `sam-259208`, `sam-259209` | Unsupported size, archive tier, or customer-provided encryption | Do not retry unchanged input. Require a policy-compliant re-upload. |
| `sam-259207` | Scan timeout | Check blob complexity and Storage latency. Retry through supported rescan or re-upload; keep inaccessible meanwhile. |
| `sam-259210`, `sam-259211` | Password protection or archive nesting limit | Treat as unsafe/unsupported. Require an unprotected, policy-compliant replacement. |
| `sam-259213`, `sam-259215`, `sam-259221` | Defender delay/throttle or busy Storage | Reduce load, wait for recovery, and monitor. `sam-259215` remains pending until a later result or watchdog; other durable errors require supported rescan/re-upload. |
| `sam-259220` | Immutability/LAT policy conflict | Correct the conflicting Storage policy after security review, then rescan or re-upload. |
| unknown / malformed | Unsupported or invalid result | Verify Event Grid topic/schema and Defender configuration. Do not edit the event or status manually. |

A later result may change `scan-error` only when the normal processor validates
topic, account, container, stable ID, source ETag, and state transition.

## Stale pending and missing activity

- Verify accepted-upload telemetry exists and scan events are reaching the custom
  topic and Function.
- Check Defender result export and the pending blob's exact ETag using approved
  tooling. Do not disclose the blob path.
- The watchdog marks stale durable records `scan-error` once. Repeated watchdog
  runs must not create repeated recovery actions.
- After correcting the cause, use a Defender-supported on-demand scan if
  available for the unchanged blob; otherwise require a fresh upload. Never
  synthesize an Event Grid event.

## Quarantine and false positives

- Quarantine is dangerous-content storage. The web, host, and management
  identities have no access. Grant exceptional operator access only through the
  incident-access process, time-bound and read-minimized; do not download to
  unmanaged devices.
- Submit suspected false positives through the approved Microsoft Security
  Intelligence sample-submission process using isolated security tooling.
- A false-positive determination does **not** authorize manual promotion. Require
  updated Defender intelligence and a new validated scan, or a new upload that
  receives a clean result.
- Quarantine expires through the configured lifecycle policy (30 days by
  default). Preserve evidence elsewhere only under an approved incident/legal
  retention process.

## Defender cap or upload-budget exhaustion

1. Confirm whether the signal is `defender-cap`, `byte-budget`,
   `request-budget`, `concurrency`, `admission-store-unavailable`, or
   `secure_upload.management.inventory_capacity_exceeded`.
2. Keep rejected requests out of Storage; do not bypass admission controls and
   do not serve a partial management inventory.
3. For Defender cap proximity/exhaustion, confirm billed scan usage and approved
   budget. Increase the cap only with service owner and cost-owner approval.
4. If abuse or unexpected cost is suspected, activate the kill switch. Existing
   pending files continue through scanning and status processing.
5. Re-enable ingress or normal browsing only after capacity, cost, and security
   owners agree on the limit and the alert has cleared.
6. For `admission-store-unavailable`, restore the web identity's table-scoped
   access and Table availability rather than bypassing admission. Failed uploads
   release Defender reservations when the store is healthy; request/byte work
   remains charged.

## Dead letters and retry exhaustion

1. Every dead-lettered result is severity 1 because the file remains
   inaccessible. Confirm the Function and downstream Storage/Table dependencies
   are healthy before replay.
2. Replay requires authorization from both the service owner and security/on-call
   incident owner. Record the dead-letter object version, time window, reason,
   approvers, and expected count without copying event contents into the ticket.
3. Use a dedicated least-privilege replay identity. Replay the original event
   unchanged through the configured Event Grid path in a bounded batch.
4. The processor must still validate the source ETag and monotonic state. A
   permanent rejection or terminal conflict is investigated, not force-applied.
5. Verify one durable outcome per event, then remove or retain the dead-letter
   object according to audit policy. Stop replay if retries, conflicts, or
   invalid events rise.

## Kill switch, rollback, and retention

- **Kill switch:** set `Admission__Enabled=false`, restart/refresh the web app if
  required, and verify new uploads receive `503` before status/blob creation.
- **Rollback:** disable ingress first; roll back application binaries or Bicep
  configuration without deleting Storage, status rows, Event Grid delivery,
  Defender settings, or processor identities. Validate processing, management
  sign-in, role denial, and forged-header rejection before re-enabling uploads.
- **Clean retention:** no automatic expiry. The host owns deletion after
  successful consumption.
- **Quarantine retention:** lifecycle deletion, 30 days by default.
- **Pending/dead-letter/status telemetry:** retain according to the environment's
  incident, privacy, and audit policy. Teardown/export is a separate approved
  action; rollback is not data deletion.

## Closure

Confirm alerts have recovered for the documented validation window, pending age is
falling, dead letters are zero or accounted for, scan outcomes resumed, the
management site admits only the approved group, forged headers still fail closed,
no unsafe blob placement occurred, and ingress state matches the incident
decision. Attach only privacy-safe operation IDs, aggregate counts, and approved
owner sign-off to the incident record.
