(() => {
    "use strict";

    const MAX_POLL_ATTEMPTS = 2160;
    const POLL_INTERVAL_MS = 5000;
    const config = JSON.parse(document.getElementById("upload-config").textContent);
    const targetOrigin = config.targetOrigin || null;
    let fileId = null;
    let pollAttempts = 0;
    let pollTimer = null;

    const form = document.getElementById("upload-form");
    const file = document.getElementById("file");
    const submit = document.getElementById("submit");
    const retry = document.getElementById("retry");
    const panel = document.getElementById("status-panel");
    const title = document.getElementById("status-title");
    const detail = document.getElementById("status-detail");

    function announce(state, message) {
        title.textContent = message.title;
        detail.textContent = message.detail;
        panel.dataset.state = state;
    }

    function notify(status) {
        if (targetOrigin && fileId) {
            window.parent.postMessage({ version: 1, type: "secure-upload", fileId, status }, targetOrigin);
        }
    }

    function showRetry() {
        retry.hidden = false;
        submit.disabled = false;
        file.disabled = false;
    }

    function stopPolling() {
        if (pollTimer) {
            clearTimeout(pollTimer);
            pollTimer = null;
        }
    }

    async function poll() {
        if (!fileId || pollAttempts >= MAX_POLL_ATTEMPTS) {
            stopPolling();
            if (fileId) {
                announce("pending", {
                    title: "Security check is still pending",
                    detail: "The host can continue tracking this file."
                });
                notify("pending");
            }
            return;
        }

        if (document.visibilityState === "hidden") {
            pollTimer = setTimeout(poll, POLL_INTERVAL_MS);
            return;
        }

        pollAttempts += 1;
        try {
            const response = await fetch(`/api/uploads/${encodeURIComponent(fileId)}/status`, {
                credentials: "same-origin",
                headers: { "Accept": "application/json" }
            });
            if (response.ok) {
                const result = await response.json();
                notify(result.status);
                if (result.status === "available") {
                    announce("available", { title: "File is available", detail: "The security check passed." });
                    return;
                }
                if (result.status === "rejected") {
                    announce("rejected", { title: "File was rejected", detail: "Choose another file." });
                    showRetry();
                    return;
                }
                if (result.status === "scan-error") {
                    announce("scan-error", {
                        title: "Security check could not finish",
                        detail: "Choose another file or contact support."
                    });
                    showRetry();
                    return;
                }
            }
        } catch {
            // A later bounded poll retries transient network failures.
        }
        pollTimer = setTimeout(poll, POLL_INTERVAL_MS);
    }

    form.addEventListener("submit", async event => {
        event.preventDefault();
        if (!file.files.length) {
            announce("upload-error", { title: "Choose a file", detail: "One file is required." });
            return;
        }

        submit.disabled = true;
        file.disabled = true;
        retry.hidden = true;
        announce("validating", { title: "Validating file", detail: "Checking the selected file." });

        const body = new FormData();
        body.append("file", file.files[0], file.files[0].name);
        announce("uploading", { title: "Uploading file", detail: "Keep this window open." });
        try {
            const response = await fetch("/api/uploads", { method: "POST", body });
            const result = await response.json();
            if (!response.ok) {
                throw new Error(result.title || "The upload could not be accepted.");
            }

            fileId = result.fileId;
            pollAttempts = 0;
            announce("pending", {
                title: "Upload complete",
                detail: "The file is pending a security check."
            });
            notify("accepted");
            notify("pending");
            pollTimer = setTimeout(poll, POLL_INTERVAL_MS);
        } catch (error) {
            announce("upload-error", {
                title: "Upload failed",
                detail: error.message || "Choose the file and try again."
            });
            showRetry();
        }
    });

    retry.addEventListener("click", () => {
        stopPolling();
        fileId = null;
        pollAttempts = 0;
        form.reset();
        retry.hidden = true;
        submit.disabled = false;
        file.disabled = false;
        announce("idle", { title: "Ready to upload", detail: "Select one supported document or image." });
        file.focus();
    });

    window.addEventListener("message", event => {
        if (!targetOrigin || event.origin !== targetOrigin || event.source !== window.parent) {
            return;
        }
        if (event.data?.type === "secure-upload-theme" && (event.data.theme === "light" || event.data.theme === "dark")) {
            document.documentElement.dataset.theme = event.data.theme;
        }
    });

    window.addEventListener("beforeunload", stopPolling);
})();
