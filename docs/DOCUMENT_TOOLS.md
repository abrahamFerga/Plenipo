# Files & document tools

Plenipo gives every module's agent a set of **platform document tools** and a **tenant-scoped file
store**, so flows like the lawyer scenario work out of the box:

> Attach a PDF in chat → *"Store this as part of the case of Julia Assange"* → the agent reads the
> brief with `read_document`, and a domain tool (e.g. the legal module's) associates the file id
> with the case.

Everything is open source with permissive licenses (a project requirement):

| Piece | Library | License |
|-------|---------|---------|
| PDF text extraction | [PdfPig](https://github.com/UglyToad/PdfPig) | Apache-2.0 |
| PDF generation | PdfPig's builder + standard-14 fonts (no font files, any OS) | Apache-2.0 |
| Blob storage (prod) | Azure.Storage.Blobs | MIT |
| OCR | none bundled — pluggable seam (`IOcrEngine`), see below | your choice |

## The file store

- `POST /api/files` (multipart) uploads a file; `GET /api/files/{id}` downloads; `GET /api/files/mine`
  lists the caller's files. Permissions: `files.upload` / `files.read` (granted to the `user` role
  baseline by default; `guest` has neither).
- Metadata is a tenant-owned row (`stored_files`) — the global tenant query filter applies, so a
  foreign tenant's file id behaves like a nonexistent one (404, no existence leak).
- Content goes to the configured backend (`Files` section):

```jsonc
"Files": {
  "Provider": "Local",          // default — zero setup, files under data/files
  // "Provider": "AzureBlob",   // production
  // "AzureBlobConnectionString": "<via Key Vault / user-secrets>",
  // "AzureBlobContainer": "plenipo-files",
  "MaxUploadBytes": 20971520
}
```

## The document tools (`tools.documents.*`)

Registered as an `IPlatformToolSource`, so they're appended to **every** module's agent — and like
all Plenipo tools, each is permission-gated *before the model sees its schema*:

| Tool | Permission | What it does |
|------|-----------|--------------|
| `read_document` | `tools.documents.read_document` | Extracts the text of a stored PDF (PdfPig) or plain-text file; falls back to OCR when an engine is configured |
| `generate_pdf` | `tools.documents.generate_pdf` | Generates a PDF (title + word-wrapped paragraphs), stores it, returns the file id + download link |
| `list_documents` | `tools.documents.list_documents` | Lists the caller's stored files with ids, sizes, provenance |
| `ocr_document` | `tools.documents.ocr_document` | OCRs a scanned document — **only offered when an `IOcrEngine` is registered** |

The `user` role baseline grants all four; the admin console's Security view lists them under
**Files & documents**.

## Turning the tools on and off

Three gates, from coarsest to finest — all configuration, no code:

1. **Per deployment** — the whole pack is a config switch (on by default, since it has no external
   dependencies). Disabling removes the agent-facing tools entirely; the file store and the
   module-facing seams (`IDocumentReader`/`IPdfRenderer`) stay, because module code builds on them:

   ```jsonc
   "Documents": { "Enabled": false }
   ```

2. **Per tenant** — a tenant admin edits the role baselines in the admin console's Roles editor:
   remove the `tools.documents.*` permissions from a role and that role's users lose the tools at
   the next turn (runtime-editable, audited, no restart).

3. **Per user** — the ordinary permission model: individual grants/revokes, checked before the
   model ever sees a tool schema.

The same layering applies to the other platform tool packs: knowledge search is `Rag:Enabled` +
`tools.knowledge.*`, and connector tools are the Integrations page (per-tenant enable) +
`tools.connectors.*`.

## How attachments reach the agent

The chat composer uploads the file first, then appends a plain-text reference to the message:

```
Store this as part of the case of Julia Assange

[Attached files]
- brief.pdf (file id: 01890a5c-…)
```

Plain text on purpose: the convention survives **every** channel (web UI, AG-UI, SignalR, WhatsApp)
without protocol changes, and the document tools take the file id directly. The WhatsApp channel
already does this natively: an inbound document/image is downloaded from the Meta media API
(`IWhatsAppMediaClient`, faked in tests), stored through `IFileStore` with `whatsapp` provenance,
and the sender's caption + the same reference become the agent turn — so *"send a PDF on WhatsApp,
say 'store this as part of the case of Julia Assange'"* works end to end.

## Plugging in OCR

The platform deliberately ships no OCR engine — native OCR stacks (Tesseract) and cloud OCR (Azure
AI Document Intelligence) are deployment choices. Implement one interface and register it; the
`ocr_document` tool appears automatically:

```csharp
public sealed class TesseractOcrEngine : IOcrEngine
{
    public string Name => "tesseract";
    public Task<string?> ExtractTextAsync(Stream content, string contentType, CancellationToken ct)
        => /* shell out to tesseract / use a wrapper — your deployment's choice */;
}

// host Program.cs
builder.Services.AddSingleton<IOcrEngine, TesseractOcrEngine>();
```

`read_document` also uses the engine as a fallback when a PDF has no text layer (a scan).

## Domain tools on top (the "store this as part of the case" pattern)

A module tool that takes a `fileId` parameter composes naturally with the platform tools — the model
chains them in one turn. Sketch for a legal module:

```csharp
[Description("Attach a stored file to a legal matter by name.")]
public async Task<string> AttachDocumentToMatter(
    [Description("The stored file id.")] string fileId,
    [Description("The matter/case name.")] string matterName) { … }
```

Mark it `RequiresApproval = true` in the manifest if the association is side-effecting enough to
warrant the human-in-the-loop gate.

## Testing

`samples/Plenipo.Sample.Host.IntegrationTests/FileAndDocumentToolTests.cs` covers everything keyless:
upload/download round-trip, RBAC (`guest` denied), tenant isolation, and a full
`generate_pdf` → `read_document` round-trip through the same permission-filtered tool surface the
agent uses — PdfPig writes the PDF and reads it back, no API keys, no native dependencies.
