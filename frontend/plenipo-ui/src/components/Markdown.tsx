import { useRef, useState, type ReactNode } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";

/** A code block with a copy button — reads the rendered text so it copies exactly what's shown. */
function CodeBlock({ children }: { children: ReactNode }) {
  const ref = useRef<HTMLPreElement>(null);
  const [copied, setCopied] = useState(false);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(ref.current?.textContent ?? "");
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard access can be blocked (insecure context / permissions) — fail quietly.
    }
  };

  return (
    <div className="group/code relative mb-2 last:mb-0">
      <pre
        ref={ref}
        className="overflow-x-auto rounded bg-slate-900 p-2 text-xs text-slate-100 [&_code]:bg-transparent [&_code]:p-0 [&_code]:text-slate-100"
      >
        {children}
      </pre>
      <button
        type="button"
        onClick={copy}
        aria-label="Copy code"
        className="focus-ring absolute right-1 top-1 rounded bg-white/10 px-1.5 py-0.5 text-[11px] font-medium text-slate-200 opacity-0 transition hover:bg-white/20 focus:opacity-100 group-hover/code:opacity-100"
      >
        {copied ? "Copied!" : "Copy"}
      </button>
    </div>
  );
}

/**
 * Renders assistant markdown (what LLMs actually emit) with chat-appropriate styling. Uses react-markdown,
 * which parses to React elements and never renders raw HTML — so it's XSS-safe for model output by default.
 * GitHub-flavored markdown (tables, strikethrough, task lists) is enabled via remark-gfm.
 */
export function Markdown({ children }: { children: string }) {
  return (
    <ReactMarkdown
      remarkPlugins={[remarkGfm]}
      components={{
        p: ({ children }) => <p className="mb-2 last:mb-0">{children}</p>,
        ul: ({ children }) => <ul className="mb-2 list-disc pl-5 last:mb-0">{children}</ul>,
        ol: ({ children }) => <ol className="mb-2 list-decimal pl-5 last:mb-0">{children}</ol>,
        li: ({ children }) => <li className="mb-0.5">{children}</li>,
        a: ({ children, href }) => (
          <a
            className="focus-ring rounded text-brand-600 underline underline-offset-2 hover:text-brand-700 dark:text-brand-400 dark:hover:text-brand-300"
            href={href}
            target="_blank"
            rel="noreferrer noopener"
          >
            {children}
          </a>
        ),
        pre: ({ children }) => <CodeBlock>{children}</CodeBlock>,
        code: ({ children, className }) => (
          <code className={`rounded bg-black/10 px-1 py-0.5 font-mono text-[0.85em] dark:bg-white/15 ${className ?? ""}`}>
            {children}
          </code>
        ),
        strong: ({ children }) => <strong className="font-semibold">{children}</strong>,
        blockquote: ({ children }) => (
          <blockquote className="mb-2 border-l-2 border-slate-300 pl-3 text-slate-600 dark:border-slate-600 dark:text-slate-400 last:mb-0">
            {children}
          </blockquote>
        ),
        h1: ({ children }) => <h3 className="mb-1 mt-1 text-base font-semibold">{children}</h3>,
        h2: ({ children }) => <h3 className="mb-1 mt-1 text-base font-semibold">{children}</h3>,
        h3: ({ children }) => <h3 className="mb-1 mt-1 font-semibold">{children}</h3>,
      }}
    >
      {children}
    </ReactMarkdown>
  );
}
