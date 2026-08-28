# Secure upload alerts

## Ownership and response

| Alert | Severity | Primary owner | Required response |
|---|---:|---|---|
| Scan errors | 1 | Security operations | Triage immediately; apply SAM-class recovery and keep file inaccessible. |
| Scan integrity (stale, invalid event, terminal conflict) | 1 | Security operations + service on-call | Validate source/event boundary and blob placement; never override state. |
| Event Grid dead letter | 1 | Service on-call | Restore processing, obtain replay authorization, replay bounded original events. |
| Missing scan activity | 1 | Service on-call | Check Defender, Event Grid, Function, and export health within 15 minutes. |
| Repeated processor retries | 2 | Service on-call | Mitigate dependency failure before delivery budget exhaustion. |
| Scan lag / oldest pending age | 2 | Service on-call | Check Defender and Storage capacity; prepare controlled rescan/re-upload. |
| Upload safety controls | 2 | Service owner + cost owner | Investigate abuse/capacity/cost; keep limits or kill switch fail-closed. |
| Byte budget / Defender cap proximity | 2 | Service owner + cost owner | Review rolling accepted bytes and authoritative Defender billed usage before changing limits. |
| Platform failures | 2 | Service on-call | Restore Web/Function dependencies without enabling shared keys or public access. |

The action group must route severity 1 to a continuously monitored security and
service-on-call channel. Severity 2 routes to service on-call; Defender-cap
changes also require cost-owner approval. Each environment owner validates routing
quarterly and after recipient changes.

## Application metric names

- `secure_upload.upload.accepted`
- `secure_upload.upload.rejected`
- `secure_upload.upload.bytes`
- `secure_upload.upload.rate_limited`
- `secure_upload.upload.failure`
- `secure_upload.upload.cleanup_failure`
- `secure_upload.upload.kill_switch`
- `secure_upload.scan.outcome`
- `secure_upload.scan.latency`
- `secure_upload.scan.invalid_event`
- `secure_upload.scan.processing_retry`
- `secure_upload.scan.stale_pending`
- `secure_upload.scan.oldest_pending_age`
- `secure_upload.scan.blob_operation_failure`
- `secure_upload.scan.terminal_conflict`

Dimensions are bounded enums plus `secure_upload.operation_id`. The operation ID
is a keyed hash for stable-file correlation and is not authorization. Do not add
raw stable IDs, capability URL paths, filenames, event bodies, tokens, malware
names, credentials, or exception messages as dimensions.

## Investigation queries

Adjust the window and environment filters before use.

```kusto
AppMetrics
| where Name startswith "secure_upload."
| summarize Total=sum(Sum), Maximum=max(Max) by Name, bin(TimeGenerated, 5m)
```

```kusto
AppTraces
| where Message has "OperationId="
| project TimeGenerated, SeverityLevel, Message
```

```kusto
StorageMalwareScanningResults
| summarize Results=count() by tostring(ResultType), bin(TimeGenerated, 15m)
```

Do not project blob URI, filename, malware name, event body, or credential fields
into shared workbooks or incident exports.

## Threshold guidance

- Scan error, stale pending, invalid event, terminal conflict, and dead letter:
  alert on the first occurrence.
- Repeated retries: five within 15 minutes; tune below the Event Grid delivery
  attempt budget.
- Scan lag/oldest pending: three hours by default, matching the watchdog.
- Missing scan activity: accepted uploads with no outcome over four hours. Silence
  only for a documented maintenance window with ingress disabled.
- Admission safety: immediate for kill switch; ten budget/cap/concurrency
  rejections in 15 minutes. Also monitor accepted bytes and Defender billed usage
  at 70%, 85%, and 95% of approved capacity in the cost-management workbook.
- Upload cleanup/copy/delete failure: investigate any occurrence; page when
  repeated or accompanied by scan-error/retry/dead-letter signals.

## Alert maintenance

Test action-group delivery and a synthetic nonproduction signal after deployment.
Review thresholds monthly against traffic and Defender latency. Changes require
service-owner approval; weakening scan-integrity or dead-letter alerts also
requires security approval. Never suppress an alert by manually promoting,
deleting, or editing a file status.
