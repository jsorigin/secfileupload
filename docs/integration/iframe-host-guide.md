# Iframe host integration

## Embed the uploader

Configure every exact host origin in `AllowedOrigins:Origins` (environment form:
`AllowedOrigins__Origins__0`, `AllowedOrigins__Origins__1`, and so on). Origins
include scheme, host, and optional port, with no path. Azure parameters require
HTTPS origins without trailing slashes; local browser tests use HTTP loopback.

```html
<iframe
  title="Secure file upload"
  src="https://UPLOAD-APP.example/upload?parentOrigin=https%3A%2F%2FHOST.example"
  width="720"
  height="430"
  referrerpolicy="no-referrer">
</iframe>
```

The response sets `Content-Security-Policy: frame-ancestors ...` from that list.
Set `parentOrigin` to the host page's exact origin (scheme, host, and optional
port), URL-encoded as a query value. The server validates it against the same
allowlist and serializes only that exact origin as the `postMessage` target.
Missing or unapproved values do not prevent the upload UI from operating, but the
iframe sends no parent messages. Messaging does not depend on
`document.referrer`, so hosts may suppress referrers.
Use a responsive width such as `width: min(100%, 720px)` and allow at least
`430px` height. The uploader itself supports narrow viewports down to 320px.

Presentation defaults come from `Presentation:Title`, `Presentation:HelpText`,
`Presentation:Theme` (`light` or `dark`), and a six-digit
`Presentation:AccentColor`. These are application settings, not URL parameters;
arbitrary host CSS is not supported.

## Receive status messages

Validate both `event.origin` and `event.source`. The iframe uses only the validated
`parentOrigin` target and never uses `*`.

```js
const uploader = document.querySelector("#secure-upload");
const uploadOrigin = new URL(uploader.src).origin;

window.addEventListener("message", event => {
  if (event.origin !== uploadOrigin || event.source !== uploader.contentWindow) {
    return;
  }

  const message = event.data;
  if (message?.version !== 1 || message?.type !== "secure-upload") {
    return;
  }

  // Persist message.fileId. The host owns tracking after iframe reload/closure.
  console.log(message.fileId, message.status);
});
```

The exact version 1 payload is:

```json
{
  "version": 1,
  "type": "secure-upload",
  "fileId": "64-lowercase-hexadecimal-characters",
  "status": "accepted"
}
```

`status` is one of `accepted`, `pending`, `available`, `rejected`, or
`scan-error`. The iframe sends `accepted` and `pending` immediately after a
successful `202` upload, then sends polled public states. Repeated `pending`
messages are possible. Internal states such as `uploading`, `promoting`, and
`quarantining` are never messages.

## Change theme

The approved parent may switch the current iframe between light and dark:

```js
uploader.contentWindow.postMessage(
  { type: "secure-upload-theme", theme: "dark" },
  uploadOrigin
);
```

The iframe accepts this only from `window.parent` at an exact configured origin.
The theme message has no version field in the implemented contract.

## UX and ownership

- One file is accepted per submission.
- The iframe exposes a labeled file control, keyboard-operable buttons, visible
  focus, and an atomic polite live region.
- Upload errors, rejection, and scan errors expose **Choose another file** and
  return focus to the file control.
- Polling occurs only while the iframe is open, pauses while hidden, and is
  bounded. Reloading does not restore a prior file.
- Persist the stable ID on `accepted`; use the authenticated backend status API
  for tracking after reload or closure.
