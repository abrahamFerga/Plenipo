import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { apiGet, type TabEditorField } from "../lib/api";

/**
 * One form field of a server-declared shape, shared by the generic tab editor and the setup
 * wizard. Fields whose valid values are KNOWN render a select — a fixed vocabulary (`options`)
 * or live data (`optionsEndpoint` + `optionsField`, e.g. the household's account names) — so
 * "No account named X exists" can't happen from a form. Everything else stays a text/number
 * input; multiline gets a textarea.
 */

export const fieldInputClass =
  "w-full rounded border border-slate-300 bg-white px-2 py-1.5 text-sm focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 disabled:opacity-60 dark:border-slate-600 dark:bg-slate-800";

interface FieldInputProps {
  id: string;
  field: TabEditorField;
  value: string;
  disabled?: boolean;
  onChange: (value: string) => void;
}

export function FieldInput({ id, field, value, disabled, onChange }: FieldInputProps) {
  const [revealed, setRevealed] = useState(false);
  const dynamic = useQuery({
    queryKey: ["field-options", field.optionsEndpoint, field.optionsField],
    queryFn: () => apiGet<Record<string, unknown>[]>(field.optionsEndpoint!),
    enabled: Boolean(field.optionsEndpoint),
    staleTime: 30_000,
  });

  const options =
    field.options ??
    (field.optionsEndpoint
      ? (dynamic.data ?? [])
          .map((row) => row[field.optionsField ?? "name"])
          .filter((v): v is string => typeof v === "string" && v.length > 0)
      : null);

  if (options !== null) {
    const empty = options.length === 0 && Boolean(field.optionsEndpoint);
    return (
      <>
        <select
          id={id}
          value={value}
          disabled={disabled || empty}
          onChange={(e) => onChange(e.target.value)}
          className={fieldInputClass}
        >
          {/* A deliberate blank first entry: never silently pre-pick on the user's behalf. */}
          <option value="">{empty ? "Nothing to choose yet" : "Choose…"}</option>
          {options.map((option) => (
            <option key={option} value={option}>
              {option}
            </option>
          ))}
        </select>
        {empty && (
          <p className="mt-1 text-xs text-slate-400">
            Nothing exists yet to pick from — add it first (or skip this for now).
          </p>
        )}
      </>
    );
  }

  if (field.multiline) {
    return (
      <textarea id={id} rows={3} value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)} className={fieldInputClass} />
    );
  }

  // Masked (PII-grade) fields type password-style behind an explicit reveal — same intent as the
  // table's masked columns, applied while the value is being entered.
  if (field.masked && !field.numeric) {
    return (
      <div className="flex gap-1">
        <input
          id={id}
          type={revealed ? "text" : "password"}
          value={value}
          disabled={disabled}
          onChange={(e) => onChange(e.target.value)}
          className={fieldInputClass}
        />
        <button
          type="button"
          onClick={() => setRevealed((v) => !v)}
          aria-pressed={revealed}
          aria-label={`${revealed ? "Hide" : "Reveal"} ${field.label}`}
          className="focus-ring shrink-0 rounded border border-slate-300 px-2 text-xs font-medium text-slate-600 dark:border-slate-600 dark:text-slate-300"
        >
          {revealed ? "Hide" : "Show"}
        </button>
      </div>
    );
  }

  return (
    <input
      id={id}
      type={field.numeric ? "number" : "text"}
      inputMode={field.numeric ? "decimal" : undefined}
      step={field.numeric ? "any" : undefined}
      value={value}
      disabled={disabled}
      onChange={(e) => onChange(e.target.value)}
      className={fieldInputClass}
    />
  );
}
