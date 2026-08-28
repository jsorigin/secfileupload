import http from "node:http";

function page(label, parentOrigin) {
  const iframeSource = `http://127.0.0.1:5080/upload?parentOrigin=${encodeURIComponent(parentOrigin)}`;
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>${label} secure upload host</title>
  <style>
    body { font-family: system-ui, sans-serif; margin: 1rem; }
    iframe { border: 1px solid #64748b; width: min(100%, 720px); height: 430px; }
  </style>
</head>
<body>
  <h1>${label} host</h1>
  <button id="light">Light theme</button>
  <button id="dark">Dark theme</button>
  <iframe id="uploader" title="Secure file upload" src="${iframeSource}" referrerpolicy="no-referrer"></iframe>
  <script>
    window.receivedMessages = [];
    const uploader = document.getElementById("uploader");
    window.addEventListener("message", event => {
      if (event.origin === "http://127.0.0.1:5080") {
        window.receivedMessages.push(event.data);
      }
    });
    document.getElementById("light").addEventListener("click", () =>
      uploader.contentWindow.postMessage({ type: "secure-upload-theme", theme: "light" }, "http://127.0.0.1:5080"));
    document.getElementById("dark").addEventListener("click", () =>
      uploader.contentWindow.postMessage({ type: "secure-upload-theme", theme: "dark" }, "http://127.0.0.1:5080"));
  </script>
</body>
</html>`;
}

for (const [port, label] of [[4173, "Approved"], [4174, "Unapproved"]]) {
  http.createServer((request, response) => {
    const parentOrigin = request.url === "/unapproved-parameter"
      ? "http://127.0.0.1:4174"
      : `http://127.0.0.1:${port}`;
    response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    response.end(page(label, parentOrigin));
  }).listen(port, "127.0.0.1");
}
