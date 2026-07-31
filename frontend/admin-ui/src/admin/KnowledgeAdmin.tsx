import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, type KnowledgeCollection, type KnowledgeHit } from "@plenipo/ui";

const COLLECTIONS_KEY = ["admin", "knowledge"];

/** Editable key/value rows — the generic facet editor behind jurisdiction, practice area, and the rest. */
function MetadataEditor({
  value,
  onChange,
}: {
  value: Record<string, string>;
  onChange: (next: Record<string, string>) => void;
}) {
  const entries = Object.entries(value);

  return (
    <div className="space-y-2">
      {entries.map(([k, v]) => (
        <div key={k} className="flex items-center gap-2">
          <input
            value={k}
            aria-label={k ? `Facet key ${k}` : "New facet key"}
            onChange={(e) => {
              const next = { ...value };
              delete next[k];
              next[e.target.value] = v;
              onChange(next);
            }}
            className="focus-ring w-40 rounded border border-slate-300 px-2 py-1 text-sm dark:border-slate-600 dark:bg-slate-800"
          />
          <span className="text-slate-400">=</span>
          <input
            value={v}
            aria-label={k ? `Facet value for ${k}` : "New facet value"}
            onChange={(e) => onChange({ ...value, [k]: e.target.value })}
            className="focus-ring flex-1 rounded border border-slate-300 px-2 py-1 text-sm dark:border-slate-600 dark:bg-slate-800"
          />
          <button
            type="button"
            onClick={() => {
              const next = { ...value };
              delete next[k];
              onChange(next);
            }}
            className="focus-ring rounded px-2 py-1 text-sm text-slate-500 hover:text-red-600"
          >
            Remove
          </button>
        </div>
      ))}
      <button
        type="button"
        onClick={() => onChange({ ...value, "": "" })}
        className="focus-ring rounded border border-dashed border-slate-300 px-2 py-1 text-sm text-slate-500 dark:border-slate-600"
      >
        Add facet
      </button>
    </div>
  );
}

function CollectionCard({ collection }: { collection: KnowledgeCollection }) {
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);

  const documents = useQuery({
    queryKey: [...COLLECTIONS_KEY, collection.id, "documents"],
    queryFn: () => api.knowledge.documents(collection.id),
    enabled: open,
  });

  const invalidate = () => {
    void qc.invalidateQueries({ queryKey: COLLECTIONS_KEY });
  };

  const reindex = useMutation({ mutationFn: () => api.knowledge.reindex(collection.id), onSuccess: invalidate });
  const remove = useMutation({ mutationFn: () => api.knowledge.remove(collection.id), onSuccess: invalidate });
  const removeDocument = useMutation({
    mutationFn: (fileId: string) => api.knowledge.removeDocument(collection.id, fileId),
    onSuccess: invalidate,
  });

  return (
    <div className="rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900">
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0">
          <p className="flex flex-wrap items-center gap-2 font-medium text-slate-900 dark:text-slate-100">
            {collection.name}
            <span className="rounded-full bg-slate-100 px-2 py-0.5 font-mono text-xs text-slate-500 dark:bg-slate-800">
              {collection.language}
            </span>
            {!collection.isEditable && (
              <span
                className="rounded-full bg-amber-50 px-2 py-0.5 text-xs text-amber-700 dark:bg-amber-900/40 dark:text-amber-300"
                title={`Owned by the ${collection.moduleId} module and bound to a ${collection.resourceType}. Its lifecycle follows that resource.`}
              >
                {collection.moduleId} · {collection.resourceType}
              </span>
            )}
          </p>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
            {collection.documentCount} document{collection.documentCount === 1 ? "" : "s"} ·{" "}
            {collection.chunkCount} passage{collection.chunkCount === 1 ? "" : "s"} · embedded with{" "}
            <span className="font-mono text-xs">{collection.embeddingModel}</span>
          </p>
          {collection.filterKeys.length > 0 && (
            <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
              Filter keys: {collection.filterKeys.map((k) => <code key={k} className="mr-1 font-mono">{k}</code>)}
            </p>
          )}
        </div>
        <div className="flex shrink-0 items-center gap-2">
          <button
            type="button"
            onClick={() => setOpen((o) => !o)}
            className="focus-ring rounded border border-slate-300 px-2 py-1 text-sm dark:border-slate-600"
          >
            {open ? "Hide" : "Documents"}
          </button>
          <button
            type="button"
            disabled={reindex.isPending || collection.documentCount === 0}
            onClick={() => reindex.mutate()}
            title="Re-extract, re-chunk and re-embed every document — needed after changing the language or the embedding model."
            className="focus-ring rounded border border-slate-300 px-2 py-1 text-sm disabled:opacity-50 dark:border-slate-600"
          >
            {reindex.isPending ? "Queuing…" : "Re-index"}
          </button>
          {collection.isEditable && (
            <button
              type="button"
              disabled={remove.isPending}
              onClick={() => {
                if (window.confirm(`Delete "${collection.name}" and all ${collection.chunkCount} indexed passages? The source files stay in the file store.`)) {
                  remove.mutate();
                }
              }}
              className="focus-ring rounded border border-red-200 px-2 py-1 text-sm text-red-600 disabled:opacity-50 dark:border-red-900"
            >
              Delete
            </button>
          )}
        </div>
      </div>

      {reindex.isSuccess && (
        <p className="mt-2 text-sm text-emerald-700 dark:text-emerald-300">
          Re-indexing {reindex.data.files} document(s) in the background — job {reindex.data.jobId}.
        </p>
      )}
      {(reindex.isError || remove.isError) && (
        <p className="mt-2 text-sm text-red-600">
          {((reindex.error ?? remove.error) as Error).message}
        </p>
      )}

      {open && (
        <div className="mt-3 border-t border-slate-100 pt-3 dark:border-slate-800">
          {documents.isLoading && <p className="text-sm text-slate-500">Loading documents…</p>}
          {documents.isError && <p className="text-sm text-red-600">{(documents.error as Error).message}</p>}
          {documents.data?.length === 0 && (
            <p className="text-sm text-slate-500">
              Nothing indexed yet. Documents reach a collection from the module that owns it, from a connector
              sync, or via <code className="font-mono text-xs">POST /api/knowledge/{"{id}"}/documents</code>.
            </p>
          )}
          <ul className="space-y-1">
            {documents.data?.map((d) => (
              <li key={d.fileId} className="flex items-center justify-between gap-3 text-sm">
                <span className="min-w-0 truncate text-slate-700 dark:text-slate-300">{d.fileName}</span>
                <span className="shrink-0 text-xs text-slate-400">
                  {d.chunkCount} passage{d.chunkCount === 1 ? "" : "s"} · {d.language}
                </span>
                <button
                  type="button"
                  disabled={removeDocument.isPending}
                  onClick={() => removeDocument.mutate(d.fileId)}
                  className="focus-ring shrink-0 rounded px-2 py-0.5 text-xs text-slate-500 hover:text-red-600"
                >
                  Remove
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

/** Create an unbound, curated collection — a statute library, a policy handbook, a playbook. */
function CreateCollection() {
  const qc = useQueryClient();
  const [name, setName] = useState("");
  const [language, setLanguage] = useState("simple");
  const [metadata, setMetadata] = useState<Record<string, string>>({});

  const languages = useQuery({ queryKey: [...COLLECTIONS_KEY, "languages"], queryFn: api.knowledge.languages });

  const create = useMutation({
    mutationFn: () =>
      api.knowledge.create({
        name,
        language,
        metadata: Object.fromEntries(Object.entries(metadata).filter(([k]) => k.trim().length > 0)),
      }),
    onSuccess: () => {
      setName("");
      setMetadata({});
      void qc.invalidateQueries({ queryKey: COLLECTIONS_KEY });
    },
  });

  return (
    <form
      className="space-y-3 rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900"
      onSubmit={(e) => {
        e.preventDefault();
        if (name.trim().length > 0) create.mutate();
      }}
    >
      <h2 className="font-medium text-slate-900 dark:text-slate-100">New collection</h2>
      <div className="flex flex-wrap gap-3">
        <label className="min-w-64 flex-1">
          <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Name</span>
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="e.g. Spanish employment law"
            className="focus-ring mt-1 w-full rounded border border-slate-300 px-2 py-1.5 text-sm dark:border-slate-600 dark:bg-slate-800"
          />
        </label>
        <label>
          <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Language</span>
          <select
            value={language}
            onChange={(e) => setLanguage(e.target.value)}
            className="focus-ring mt-1 block rounded border border-slate-300 px-2 py-1.5 text-sm dark:border-slate-600 dark:bg-slate-800"
          >
            {(languages.data ?? ["simple"]).map((l) => (
              <option key={l} value={l}>
                {l}
              </option>
            ))}
          </select>
        </label>
      </div>
      <p className="text-xs text-slate-500 dark:text-slate-400">
        The language picks the stemmer and stop-words used for keyword matching. Documents are detected
        individually at index time, so a mixed-language corpus still works — this is the fallback.
        <strong> simple</strong> stems nothing: safe anywhere, slightly weaker recall.
      </p>

      <div>
        <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Facets (optional)</span>
        <p className="mb-2 text-xs text-slate-500 dark:text-slate-400">
          Free-form key/value pairs describing the corpus, e.g. <code className="font-mono">jurisdiction=ES</code>.
          Agents can filter retrieval on the facets stamped onto passages.
        </p>
        <MetadataEditor value={metadata} onChange={setMetadata} />
      </div>

      {create.isError && <p className="text-sm text-red-600">{(create.error as Error).message}</p>}
      <button
        type="submit"
        disabled={create.isPending || name.trim().length === 0}
        className="focus-ring rounded bg-brand-600 px-3 py-1.5 text-sm font-medium text-white disabled:opacity-50"
      >
        {create.isPending ? "Creating…" : "Create collection"}
      </button>
    </form>
  );
}

/**
 * A retrieval preview. The most useful thing on this page when curating a corpus: it runs the exact
 * query path the agent runs — same gates, same chunk ACLs, same hybrid ranking — so what you see
 * here is what the assistant will see.
 */
function SearchPreview({ collections }: { collections: KnowledgeCollection[] }) {
  const [query, setQuery] = useState("");
  const [collection, setCollection] = useState("");
  const [filters, setFilters] = useState("");

  const search = useMutation({
    mutationFn: () =>
      api.knowledge.search({
        query,
        collection: collection || null,
        filters: parseFilters(filters),
      }),
  });

  return (
    <form
      className="space-y-3 rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900"
      onSubmit={(e) => {
        e.preventDefault();
        if (query.trim().length > 0) search.mutate();
      }}
    >
      <h2 className="font-medium text-slate-900 dark:text-slate-100">Test retrieval</h2>
      <div className="flex flex-wrap gap-3">
        <label className="min-w-64 flex-1">
          <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Query</span>
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="What would a user ask?"
            className="focus-ring mt-1 w-full rounded border border-slate-300 px-2 py-1.5 text-sm dark:border-slate-600 dark:bg-slate-800"
          />
        </label>
        <label>
          <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Collection</span>
          <select
            value={collection}
            onChange={(e) => setCollection(e.target.value)}
            className="focus-ring mt-1 block rounded border border-slate-300 px-2 py-1.5 text-sm dark:border-slate-600 dark:bg-slate-800"
          >
            <option value="">All I can access</option>
            {collections.map((c) => (
              <option key={c.id} value={c.name}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Filters</span>
          <input
            value={filters}
            onChange={(e) => setFilters(e.target.value)}
            placeholder="jurisdiction=ES;year=2024"
            className="focus-ring mt-1 block rounded border border-slate-300 px-2 py-1.5 text-sm dark:border-slate-600 dark:bg-slate-800"
          />
        </label>
      </div>

      <button
        type="submit"
        disabled={search.isPending || query.trim().length === 0}
        className="focus-ring rounded bg-brand-600 px-3 py-1.5 text-sm font-medium text-white disabled:opacity-50"
      >
        {search.isPending ? "Searching…" : "Search"}
      </button>

      {search.isError && <p className="text-sm text-red-600">{(search.error as Error).message}</p>}
      {search.isSuccess && <Hits hits={search.data} />}
    </form>
  );
}

function Hits({ hits }: { hits: KnowledgeHit[] }) {
  if (hits.length === 0) {
    return (
      <p className="text-sm text-slate-500 dark:text-slate-400">
        No passages matched. The corpus may be empty, the filter may exclude everything, or the wording may be
        too far from the source text.
      </p>
    );
  }

  return (
    <ol className="space-y-2">
      {hits.map((h) => (
        <li key={h.chunkId} className="rounded border border-slate-100 p-3 text-sm dark:border-slate-800">
          <p className="text-slate-700 dark:text-slate-300">{h.text}</p>
          <p className="mt-1 text-xs text-slate-400">
            {h.fileName} · passage {h.ordinal + 1} · {h.collectionName} · score {h.score.toFixed(4)}
          </p>
        </li>
      ))}
    </ol>
  );
}

function parseFilters(raw: string): Record<string, string> | null {
  const parsed: Record<string, string> = {};
  for (const segment of raw.split(/[;,]/)) {
    const at = segment.indexOf("=");
    if (at <= 0) continue;
    const key = segment.slice(0, at).trim();
    const value = segment.slice(at + 1).trim();
    if (key && value) parsed[key] = value;
  }
  return Object.keys(parsed).length > 0 ? parsed : null;
}

/**
 * Knowledge collections: the corpora agents retrieve from. A collection is a scope — a matter, a
 * property, a statute library — and retrieval is scope-first, so what an agent can find is decided
 * here and by the collection's gate, never by the model.
 *
 * Module-owned collections (bound to a matter, a case, a property) are listed read-only: they are
 * created and refreshed by their module's own tools, and their access follows that resource.
 */
export function KnowledgeAdmin() {
  const collections = useQuery({ queryKey: COLLECTIONS_KEY, queryFn: api.knowledge.list });

  if (collections.isLoading) {
    return <p className="text-sm text-slate-500">Loading collections…</p>;
  }
  if (collections.isError) {
    return <p className="text-sm text-red-600">{(collections.error as Error).message}</p>;
  }

  const rows = collections.data ?? [];

  return (
    <div className="space-y-4">
      <header>
        <h1 className="text-xl font-semibold text-slate-900 dark:text-slate-100">Knowledge</h1>
        <p className="text-sm text-slate-500 dark:text-slate-400">
          The corpora this tenant's assistants can retrieve from, with citations. Each collection is its own
          small index — per case, per project, or a curated library — so a question never searches material the
          asker cannot see. You only see collections you could search yourself.
        </p>
      </header>

      <CreateCollection />

      {rows.length === 0 ? (
        <div className="space-y-1 rounded-lg border border-dashed border-slate-300 p-8 text-center text-sm text-slate-400 dark:border-slate-700">
          <p className="font-medium text-slate-500 dark:text-slate-300">No collections yet.</p>
          <p>Create a curated one above, or let a module create its own when it indexes a resource.</p>
        </div>
      ) : (
        <>
          <div className="space-y-2">
            {rows.map((c) => (
              <CollectionCard key={c.id} collection={c} />
            ))}
          </div>
          <SearchPreview collections={rows} />
        </>
      )}
    </div>
  );
}
