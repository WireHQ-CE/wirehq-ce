import { useRef, useState } from 'react';
import { Download, FileUp, Upload } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Dialog } from '@/components/ui/dialog';
import { useToast } from '@/components/ui/toast';
import { api, ApiError } from '@/lib/api/client';
import { enrollmentPackageUrl, useExecuteEnrollment, useValidateEnrollment } from './api';
import { download, downloadText } from './download';
import type { EnrollmentOutcome, EnrollmentPreviewResult, EnrollmentResult } from './types';

const TEMPLATE = `Name,Email,Department,DeviceType,AssignedAddress,AllowedIPs
Ada Lovelace,ada@example.com,Engineering,Laptop,,
Grace Hopper,grace@example.com,Research,Mobile,,
`;

const outcomeTone: Record<EnrollmentOutcome, 'success' | 'warning' | 'danger'> = {
  Create: 'success',
  Skip: 'warning',
  Error: 'danger',
};

/**
 * Bulk Enrollment Wizard: Upload CSV → Preview (per-row Create/Skip/Error, no writes) → Import →
 * Download a ZIP of every new peer's config. Completes the config-only WireGuard management UI.
 */
export function BulkEnrollmentWizard({ instanceId, onClose }: { instanceId: string; onClose: () => void }) {
  const toast = useToast();
  const validate = useValidateEnrollment(instanceId);
  const execute = useExecuteEnrollment(instanceId);

  const [file, setFile] = useState<File | null>(null);
  const [preview, setPreview] = useState<EnrollmentPreviewResult | null>(null);
  const [result, setResult] = useState<EnrollmentResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  function onValidate() {
    if (!file) return;
    setError(null);
    validate.mutate(file, {
      onSuccess: setPreview,
      onError: (err) => setError(err instanceof ApiError ? err.message : 'Could not read the CSV.'),
    });
  }

  function onExecute() {
    if (!file) return;
    setError(null);
    execute.mutate(file, {
      onSuccess: (res) => {
        setResult(res);
        toast(`Imported ${res.created} peer${res.created === 1 ? '' : 's'}.`);
      },
      onError: (err) => setError(err instanceof ApiError ? err.message : 'Could not import the peers.'),
    });
  }

  async function downloadPackage(batchId: string) {
    try {
      const blob = await api.blob(enrollmentPackageUrl(batchId));
      download(`enrollment-${batchId.slice(0, 8)}.zip`, blob);
    } catch {
      toast('Could not download the config package.', 'error');
    }
  }

  // ---- Result step ----
  if (result) {
    return (
      <Dialog
        open
        onClose={onClose}
        title="Enrollment complete"
        description={`${result.created} created · ${result.skipped} skipped · ${result.failed} failed`}
        className="max-w-2xl"
        footer={<Button onClick={onClose}>Done</Button>}
      >
        <div className="space-y-4">
          {result.created > 0 && (
            <div className="flex items-center justify-between rounded-md border border-gold-200 bg-gold-50 px-4 py-3 dark:border-gold-400/20 dark:bg-gold-400/10">
              <p className="text-sm text-ink-700 dark:text-ink-200">
                Download every new peer's <code>.conf</code> + QR code as a ZIP. Keys are shown only here — store them now.
              </p>
              <Button size="sm" onClick={() => downloadPackage(result.batchId)}><Download /> Download .zip</Button>
            </div>
          )}
          <OutcomeTable
            rows={result.results.map((r) => ({
              rowNumber: r.rowNumber,
              name: r.name,
              email: r.email,
              assignedAddress: r.assignedAddress,
              outcome: r.outcome as EnrollmentOutcome,
              reason: r.reason,
            }))}
          />
        </div>
      </Dialog>
    );
  }

  // ---- Preview step ----
  if (preview) {
    return (
      <Dialog
        open
        onClose={onClose}
        title="Review enrollment"
        description={`${preview.totalRows} rows · ${preview.createRows} to create · ${preview.skipRows} skipped · ${preview.errorRows} errors`}
        className="max-w-2xl"
        footer={
          <>
            <Button variant="secondary" onClick={() => { setPreview(null); setError(null); }}>Back</Button>
            <Button onClick={onExecute} disabled={preview.createRows === 0 || execute.isPending}>
              {execute.isPending ? 'Importing…' : `Import ${preview.createRows} peer${preview.createRows === 1 ? '' : 's'}`}
            </Button>
          </>
        }
      >
        <div className="space-y-3">
          <OutcomeTable rows={preview.rows} />
          {error && <p className="text-sm text-danger-600 dark:text-danger-500">{error}</p>}
        </div>
      </Dialog>
    );
  }

  // ---- Upload step ----
  return (
    <Dialog
      open
      onClose={onClose}
      title="Bulk enroll peers"
      description="Upload a CSV to create many peers at once. You'll preview the outcome before anything is created."
      className="max-w-xl"
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button onClick={onValidate} disabled={!file || validate.isPending}>
            {validate.isPending ? 'Validating…' : 'Validate'}
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <button
          type="button"
          onClick={() => fileInput.current?.click()}
          className="flex w-full flex-col items-center gap-2 rounded-lg border border-dashed px-4 py-8 text-center transition-colors hover:border-gold-400 hover:bg-gold-50/50 dark:border-ink-700 dark:hover:bg-gold-400/5"
        >
          <FileUp className="size-7 text-ink-400" />
          <span className="text-sm font-medium text-ink-700 dark:text-ink-200">
            {file ? file.name : 'Choose a CSV file'}
          </span>
          <span className="text-xs text-ink-500">{file ? 'Click to choose a different file' : 'or click to browse'}</span>
        </button>
        <input
          ref={fileInput}
          type="file"
          accept=".csv,text/csv"
          className="hidden"
          onChange={(e) => { setFile(e.target.files?.[0] ?? null); setError(null); }}
        />

        <div className="rounded-md border px-3 py-2.5 text-xs text-ink-500 dark:border-ink-800">
          <p className="mb-1 font-medium text-ink-700 dark:text-ink-200">Columns (header row required, case-insensitive)</p>
          <p>
            <span className="font-mono text-ink-700 dark:text-ink-200">Name</span>*,{' '}
            <span className="font-mono text-ink-700 dark:text-ink-200">Email</span>*, Department, DeviceType,
            AssignedAddress, AllowedIPs. Keys, addresses and configs are generated for you.
          </p>
          <Button variant="ghost" size="sm" className="mt-2 px-0 text-gold-600 hover:text-gold-700 dark:text-gold-400" onClick={() => downloadText('wirehq-enrollment-template.csv', TEMPLATE)}>
            <Upload className="rotate-180" /> Download template
          </Button>
        </div>

        {error && <p className="text-sm text-danger-600 dark:text-danger-500">{error}</p>}
      </div>
    </Dialog>
  );
}

interface OutcomeRow {
  rowNumber: number;
  name: string | null;
  email: string | null;
  assignedAddress: string | null;
  outcome: EnrollmentOutcome;
  reason: string | null;
}

function OutcomeTable({ rows }: { rows: OutcomeRow[] }) {
  return (
    <div className="max-h-[50vh] overflow-y-auto rounded-md border dark:border-ink-800">
      <table className="w-full text-sm">
        <thead className="sticky top-0 bg-ink-50 dark:bg-ink-900">
          <tr className="text-left text-xs uppercase tracking-wide text-ink-500">
            <th className="px-3 py-2 font-medium">#</th>
            <th className="px-3 py-2 font-medium">Name</th>
            <th className="px-3 py-2 font-medium">Address</th>
            <th className="px-3 py-2 font-medium">Outcome</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.rowNumber} className="border-t align-top dark:border-ink-800">
              <td className="px-3 py-2 text-ink-400">{r.rowNumber}</td>
              <td className="px-3 py-2">
                <div className="font-medium text-ink-800 dark:text-ink-100">{r.name ?? '—'}</div>
                {r.email && <div className="text-xs text-ink-400">{r.email}</div>}
              </td>
              <td className="px-3 py-2 font-mono text-ink-500">{r.assignedAddress ?? '—'}</td>
              <td className="px-3 py-2">
                <Badge tone={outcomeTone[r.outcome] ?? 'neutral'}>{r.outcome}</Badge>
                {r.reason && <div className="mt-0.5 text-xs text-ink-400">{r.reason}</div>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
