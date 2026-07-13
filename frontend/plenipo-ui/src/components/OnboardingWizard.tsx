import { useEffect, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import {
  apiSend,
  uploadFile,
  type Module,
  type OnboardingStep,
  type TabEditorField,
} from "../lib/api";
import { FieldInput } from "./FieldInput";

/**
 * The first-run setup wizard, rendered entirely from a module's manifest-declared onboarding
 * steps — the module ships copy and endpoints, the shell ships the experience. UX ground rules:
 * one task per screen with a reason-why blurb; every step skippable (setup never traps); added
 * records echo back immediately so progress feels real; a step rail shows where you are; focus
 * moves to the step heading on navigation so screen readers follow along.
 */

interface WizardProps {
  module: Module;
  /** Called when the user finishes or leaves the wizard. */
  onDone: () => void;
}

/** Values the user typed for a step's fields, keyed by field name. */
type FieldValues = Record<string, string>;

function emptyValues(fields: TabEditorField[]): FieldValues {
  return Object.fromEntries(fields.map((f) => [f.field, ""]));
}

/** Build the POST body: typed fields (numerics converted, empties omitted) + the step's preset. */
function buildBody(step: OnboardingStep, values: FieldValues, extra?: Record<string, unknown>) {
  const body: Record<string, unknown> = { ...(step.preset ?? {}), ...(extra ?? {}) };
  for (const f of step.fields ?? []) {
    const raw = (values[f.field] ?? "").trim();
    if (raw === "") continue;
    body[f.field] = f.numeric ? Number(raw) : raw;
  }
  return body;
}

function missingRequired(step: OnboardingStep, values: FieldValues): boolean {
  return (step.fields ?? []).some(
    (f) =>
      ((f.required ?? true) && (values[f.field] ?? "").trim() === "") ||
      (f.numeric && (values[f.field] ?? "").trim() !== "" && Number.isNaN(Number(values[f.field]))),
  );
}

function StepFields({
  step,
  values,
  onChange,
}: {
  step: OnboardingStep;
  values: FieldValues;
  onChange: (field: string, value: string) => void;
}) {
  return (
    <div className="grid gap-3 sm:grid-cols-2">
      {(step.fields ?? []).map((f) => (
        <div key={f.field} className={f.multiline ? "sm:col-span-2" : undefined}>
          <label
            htmlFor={`ob-${step.id}-${f.field}`}
            className="mb-1 block text-sm font-medium text-slate-700 dark:text-slate-200"
          >
            {f.label}
            {!(f.required ?? true) && <span className="ml-1 font-normal text-slate-400">· optional</span>}
          </label>
          <FieldInput
            id={`ob-${step.id}-${f.field}`}
            field={f}
            value={values[f.field] ?? ""}
            onChange={(value) => onChange(f.field, value)}
          />
        </div>
      ))}
    </div>
  );
}

/** A form step: add as many entries as the user wants; each success echoes into the list. */
function FormStep({ step, onAdded }: { step: OnboardingStep; onAdded: (label: string) => void }) {
  const [values, setValues] = useState<FieldValues>(() => emptyValues(step.fields ?? []));
  const [added, setAdded] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function add() {
    setBusy(true);
    setError(null);
    try {
      await apiSend(step.endpoint!, "POST", buildBody(step, values));
      const label =
        (step.fields ?? [])
          .map((f) => values[f.field]?.trim())
          .filter(Boolean)
          .slice(0, 2)
          .join(" · ") || "Entry";
      setAdded((list) => [...list, label]);
      onAdded(label);
      setValues(emptyValues(step.fields ?? []));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-4">
      {added.length > 0 && (
        <ul className="space-y-1" data-testid="wizard-added">
          {added.map((label, i) => (
            <li key={i} className="flex items-center gap-2 text-sm text-slate-600 dark:text-slate-300">
              <span aria-hidden className="grid h-4 w-4 place-items-center rounded-full bg-emerald-100 text-[10px] text-emerald-700 dark:bg-emerald-900/50 dark:text-emerald-300">
                ✓
              </span>
              {label}
            </li>
          ))}
        </ul>
      )}

      <form
        onSubmit={(e) => {
          e.preventDefault();
          if (!missingRequired(step, values) && !busy) void add();
        }}
        className="space-y-3"
      >
        <StepFields step={step} values={values} onChange={(f, v) => setValues((s) => ({ ...s, [f]: v }))} />
        {error && <p className="text-sm text-red-600">{error}</p>}
        <button
          type="submit"
          disabled={busy || missingRequired(step, values)}
          className="focus-ring rounded bg-brand-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-brand-500 disabled:opacity-40"
        >
          {busy ? "Adding…" : added.length > 0 ? "Add another" : "Add"}
        </button>
      </form>
    </div>
  );
}

interface UploadedFile {
  name: string;
  status: "working" | "done" | "error";
  detail?: string;
}

/** An upload step: optional context fields, then a file picker; each file goes to the platform
 * file store and the stored id is handed to the step's follow-up endpoint. */
function UploadStep({ step, onAdded }: { step: OnboardingStep; onAdded: (label: string) => void }) {
  const [values, setValues] = useState<FieldValues>(() => emptyValues(step.fields ?? []));
  const [files, setFiles] = useState<UploadedFile[]>([]);
  const inputRef = useRef<HTMLInputElement>(null);

  async function handleFiles(selected: FileList | null) {
    if (!selected || selected.length === 0) return;
    for (const file of Array.from(selected)) {
      setFiles((list) => [...list, { name: file.name, status: "working" }]);
      try {
        const stored = await uploadFile(file);
        await apiSend(step.endpoint!, "POST", buildBody(step, values, { [step.fileIdField ?? "fileId"]: stored.id }));
        setFiles((list) => list.map((f) => (f.name === file.name ? { ...f, status: "done" } : f)));
        onAdded(file.name);
      } catch (e) {
        setFiles((list) =>
          list.map((f) => (f.name === file.name ? { ...f, status: "error", detail: (e as Error).message } : f)),
        );
      }
    }
    if (inputRef.current) inputRef.current.value = "";
  }

  const fieldsMissing = missingRequired(step, values);

  return (
    <div className="space-y-4">
      {(step.fields?.length ?? 0) > 0 && (
        <StepFields step={step} values={values} onChange={(f, v) => setValues((s) => ({ ...s, [f]: v }))} />
      )}

      <label
        className={`block cursor-pointer rounded-lg border-2 border-dashed p-8 text-center text-sm transition-colors ${
          fieldsMissing
            ? "cursor-not-allowed border-slate-200 text-slate-400 dark:border-slate-800"
            : "border-slate-300 text-slate-600 hover:border-brand-400 hover:bg-brand-50/50 dark:border-slate-600 dark:text-slate-300 dark:hover:bg-slate-800/60"
        }`}
        onDragOver={(e) => e.preventDefault()}
        onDrop={(e) => {
          e.preventDefault();
          if (!fieldsMissing) void handleFiles(e.dataTransfer.files);
        }}
      >
        <input
          ref={inputRef}
          type="file"
          multiple
          accept={step.accept ?? undefined}
          className="sr-only"
          disabled={fieldsMissing}
          onChange={(e) => void handleFiles(e.target.files)}
        />
        <span className="font-medium text-brand-700 dark:text-brand-300">Choose files</span> or drag them here
        {step.accept && <span className="mt-1 block text-xs text-slate-400">{step.accept}</span>}
        {fieldsMissing && <span className="mt-1 block text-xs">Fill in the fields above first.</span>}
      </label>

      {files.length > 0 && (
        <ul className="space-y-1" data-testid="wizard-uploads">
          {files.map((f, i) => (
            <li key={i} className="flex items-center gap-2 text-sm text-slate-600 dark:text-slate-300">
              <span aria-hidden>
                {f.status === "working" ? "⏳" : f.status === "done" ? "✅" : "⚠️"}
              </span>
              <span>{f.name}</span>
              {f.detail && <span className="text-xs text-red-600">{f.detail}</span>}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

export function OnboardingWizard({ module, onDone }: WizardProps) {
  const setup = module.onboarding!;
  const steps = setup.steps;
  const [index, setIndex] = useState(0);
  const [finished, setFinished] = useState(false);
  const [addedByStep, setAddedByStep] = useState<Record<string, number>>({});
  const headingRef = useRef<HTMLHeadingElement>(null);
  const qc = useQueryClient();

  const step = steps[index];
  const isLast = index === steps.length - 1;

  // Screen readers follow the wizard: focus the fresh step's heading after navigation.
  const firstRender = useRef(true);
  useEffect(() => {
    if (firstRender.current) {
      firstRender.current = false;
      return;
    }
    headingRef.current?.focus({ preventScroll: true });
  }, [index, finished]);

  function recordAdded(stepId: string) {
    setAddedByStep((counts) => ({ ...counts, [stepId]: (counts[stepId] ?? 0) + 1 }));
  }

  function finish() {
    // Everything the wizard touched flows into the tabs — refresh them all.
    void qc.invalidateQueries({ queryKey: ["tab-data"] });
    void qc.invalidateQueries({ queryKey: ["onboarding-probe"] });
    setFinished(true);
  }

  if (finished) {
    const totalAdded = Object.values(addedByStep).reduce((a, b) => a + b, 0);
    return (
      <div className="mx-auto max-w-xl py-10 text-center">
        <div aria-hidden className="mx-auto mb-4 grid h-14 w-14 place-items-center rounded-full bg-emerald-100 text-2xl dark:bg-emerald-900/50">
          🎉
        </div>
        <h1 ref={headingRef} tabIndex={-1} className="text-2xl font-semibold text-slate-900 focus:outline-none dark:text-slate-100">
          You're set up
        </h1>
        <p className="mt-2 text-sm text-slate-500 dark:text-slate-400">
          {totalAdded > 0
            ? `${totalAdded} thing(s) added. Uploaded statements are being extracted — review and approve their lines whenever you're ready.`
            : "You can add everything later — each tab has an Add button, and the assistant can do it with you in Chat."}
        </p>
        <div className="mt-6 flex flex-wrap justify-center gap-2">
          <button
            type="button"
            onClick={onDone}
            className="focus-ring rounded bg-brand-600 px-4 py-2 text-sm font-medium text-white hover:bg-brand-500"
          >
            Go to {module.displayName}
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="mx-auto flex max-w-4xl gap-8 py-4">
      {/* Step rail: where am I, what's done, what's left. */}
      <nav aria-label="Setup steps" className="hidden w-56 shrink-0 md:block">
        <ol className="space-y-1">
          {steps.map((s, i) => (
            <li key={s.id}>
              <div
                aria-current={i === index ? "step" : undefined}
                className={`flex items-center gap-3 rounded-md px-3 py-2 text-sm ${
                  i === index
                    ? "bg-brand-50 font-medium text-brand-800 dark:bg-brand-900/40 dark:text-brand-200"
                    : "text-slate-500 dark:text-slate-400"
                }`}
              >
                <span
                  aria-hidden
                  className={`grid h-6 w-6 shrink-0 place-items-center rounded-full text-xs font-semibold ${
                    i < index
                      ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/50 dark:text-emerald-300"
                      : i === index
                        ? "bg-brand-600 text-white"
                        : "bg-slate-100 text-slate-500 dark:bg-slate-800"
                  }`}
                >
                  {i < index ? "✓" : i + 1}
                </span>
                {s.title}
              </div>
            </li>
          ))}
        </ol>
      </nav>

      <section className="min-w-0 flex-1">
        <p className="text-xs font-medium uppercase tracking-wide text-slate-400 md:hidden">
          Step {index + 1} of {steps.length}
        </p>
        <h1
          ref={headingRef}
          tabIndex={-1}
          className="mt-1 text-2xl font-semibold text-slate-900 focus:outline-none dark:text-slate-100"
        >
          {step.title}
        </h1>
        <p className="mt-1 max-w-prose text-sm text-slate-500 dark:text-slate-400">{step.blurb}</p>

        <div className="mt-6">
          {step.kind === "form" && <FormStep key={step.id} step={step} onAdded={() => recordAdded(step.id)} />}
          {step.kind === "upload" && <UploadStep key={step.id} step={step} onAdded={() => recordAdded(step.id)} />}
        </div>

        <div className="mt-8 flex items-center gap-2 border-t border-slate-200 pt-4 dark:border-slate-800">
          {index > 0 && (
            <button
              type="button"
              onClick={() => setIndex(index - 1)}
              className="focus-ring rounded border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-600 dark:border-slate-600 dark:text-slate-300"
            >
              Back
            </button>
          )}
          <span className="flex-1" />
          {(step.optional ?? true) && step.kind !== "info" && (addedByStep[step.id] ?? 0) === 0 && (
            <button
              type="button"
              onClick={() => (isLast ? finish() : setIndex(index + 1))}
              className="focus-ring rounded px-3 py-1.5 text-sm font-medium text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200"
            >
              Skip for now
            </button>
          )}
          <button
            type="button"
            onClick={() => (isLast ? finish() : setIndex(index + 1))}
            className="focus-ring rounded bg-brand-600 px-4 py-1.5 text-sm font-medium text-white hover:bg-brand-500"
          >
            {isLast ? "Finish" : "Continue"}
          </button>
        </div>
      </section>
    </div>
  );
}
