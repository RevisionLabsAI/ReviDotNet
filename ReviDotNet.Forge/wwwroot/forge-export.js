// ===================================================================
//  Lightweight client-side export helper. Called from ExportService via
//  IJSRuntime to trigger a file download in the browser.
// ===================================================================

window.forgeExport = {
    downloadText: function (filename, content, mimeType) {
        const blob = new Blob([content], { type: mimeType || "text/plain" });
        const url = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    }
};
