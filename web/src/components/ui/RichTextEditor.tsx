import { useEffect, useRef, useState } from 'react';
import { Bold, Code2, Italic, Link2, List, ListOrdered, Minus, Quote, Heading2, Heading3, Pilcrow, Eraser } from 'lucide-react';
import { cn } from '@/lib/utils/cn';

/**
 * A lightweight, dependency-free WYSIWYG-ish editor for CMS page bodies. The visual mode is a
 * `contentEditable` region wrapped in `.cms-content` (so it previews exactly as the public page renders),
 * driven by `document.execCommand`; a "Source" toggle exposes the raw HTML in a textarea. Emits an HTML
 * string via {@link onChange}. (ADR-015: hand-rolled primitives, no new dependencies.)
 */
export function RichTextEditor({
  value,
  onChange,
  id,
}: {
  value: string;
  onChange: (html: string) => void;
  id?: string;
}) {
  const [mode, setMode] = useState<'rich' | 'source'>('rich');
  const ref = useRef<HTMLDivElement>(null);

  // Sync external value into the editable region when it changes and we're not actively typing in it.
  useEffect(() => {
    const el = ref.current;
    if (mode === 'rich' && el && document.activeElement !== el && el.innerHTML !== value) {
      el.innerHTML = value;
    }
  }, [value, mode]);

  function emit() {
    if (ref.current) onChange(ref.current.innerHTML);
  }

  function exec(command: string, arg?: string) {
    ref.current?.focus();
    // execCommand is deprecated but remains the only dependency-free rich-text mechanism (ADR-015).
    document.execCommand(command, false, arg);
    emit();
  }

  function addLink() {
    const url = window.prompt('Link URL (e.g. /pricing or https://…)');
    if (url) exec('createLink', url);
  }

  return (
    <div className="overflow-hidden rounded-md border border-ink-200 dark:border-ink-700">
      <div className="flex flex-wrap items-center gap-0.5 border-b border-ink-200 bg-ink-50 p-1 dark:border-ink-700 dark:bg-ink-900">
        <ToolBtn label="Heading 2" onClick={() => exec('formatBlock', 'H2')}><Heading2 className="size-4" /></ToolBtn>
        <ToolBtn label="Heading 3" onClick={() => exec('formatBlock', 'H3')}><Heading3 className="size-4" /></ToolBtn>
        <ToolBtn label="Paragraph" onClick={() => exec('formatBlock', 'P')}><Pilcrow className="size-4" /></ToolBtn>
        <Divider />
        <ToolBtn label="Bold" onClick={() => exec('bold')}><Bold className="size-4" /></ToolBtn>
        <ToolBtn label="Italic" onClick={() => exec('italic')}><Italic className="size-4" /></ToolBtn>
        <Divider />
        <ToolBtn label="Bulleted list" onClick={() => exec('insertUnorderedList')}><List className="size-4" /></ToolBtn>
        <ToolBtn label="Numbered list" onClick={() => exec('insertOrderedList')}><ListOrdered className="size-4" /></ToolBtn>
        <ToolBtn label="Quote" onClick={() => exec('formatBlock', 'BLOCKQUOTE')}><Quote className="size-4" /></ToolBtn>
        <ToolBtn label="Link" onClick={addLink}><Link2 className="size-4" /></ToolBtn>
        <ToolBtn label="Divider" onClick={() => exec('insertHorizontalRule')}><Minus className="size-4" /></ToolBtn>
        <ToolBtn label="Clear formatting" onClick={() => exec('removeFormat')}><Eraser className="size-4" /></ToolBtn>
        <div className="ml-auto">
          <button
            type="button"
            onClick={() => setMode((m) => (m === 'rich' ? 'source' : 'rich'))}
            className={cn(
              'inline-flex items-center gap-1.5 rounded px-2 py-1 text-xs font-medium transition-colors',
              mode === 'source'
                ? 'bg-gold-400/15 text-gold-700 dark:text-gold-300'
                : 'text-ink-500 hover:bg-ink-100 dark:hover:bg-ink-800',
            )}
            aria-pressed={mode === 'source'}
          >
            <Code2 className="size-4" /> {mode === 'source' ? 'Visual' : 'Source'}
          </button>
        </div>
      </div>

      {mode === 'rich' ? (
        <div
          id={id}
          ref={ref}
          contentEditable
          suppressContentEditableWarning
          onInput={emit}
          onBlur={emit}
          className="cms-content min-h-[24rem] max-w-none bg-ink-0 px-4 py-3 focus:outline-none dark:bg-ink-900"
        />
      ) : (
        <textarea
          id={id}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          spellCheck={false}
          className="min-h-[24rem] w-full bg-ink-0 px-4 py-3 font-mono text-sm text-ink-900 focus:outline-none dark:bg-ink-900 dark:text-ink-50"
        />
      )}
    </div>
  );
}

function ToolBtn({ label, onClick, children }: { label: string; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      title={label}
      aria-label={label}
      onClick={onClick}
      className="inline-flex size-8 items-center justify-center rounded text-ink-600 transition-colors hover:bg-ink-100 dark:text-ink-300 dark:hover:bg-ink-800"
    >
      {children}
    </button>
  );
}

function Divider() {
  return <span className="mx-1 h-5 w-px bg-ink-200 dark:bg-ink-700" />;
}
