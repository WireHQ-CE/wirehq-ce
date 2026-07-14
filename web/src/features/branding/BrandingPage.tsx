import { useEffect, useRef, useState } from 'react';
import { ImageUp, Palette, RotateCcw, Save, Trash2 } from 'lucide-react';
import { PageHeader } from '@/components/layout/AppShell';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Field, Input } from '@/components/ui/input';
import { useToast } from '@/components/ui/toast';
import { ApiError } from '@/lib/api/client';
import { config } from '@/app/config';
import {
  useBrandingSettings,
  useUpdateBranding,
  useUploadBrandAsset,
  useRemoveBrandAsset,
  type BrandAssetKind,
} from './branding-api';

// The Settings → Branding console (docs/34) — the install-global brand: product name, accent colour, and logo/favicon
// images. Super-Admin only (the endpoints are platform-scoped). Every change re-applies to the running shell live.

const DEFAULT_COLOR = '#f5b301';

export function BrandingPage() {
  const toast = useToast();
  const settings = useBrandingSettings();
  const update = useUpdateBranding();

  const [productName, setProductName] = useState('');
  const [color, setColor] = useState<string | null>(null);

  // Seed the form from the server once loaded.
  useEffect(() => {
    if (settings.data) {
      setProductName(settings.data.productName ?? '');
      setColor(settings.data.brandColor);
    }
  }, [settings.data]);

  const fail = (e: unknown, fallback: string) => toast(e instanceof ApiError ? e.message : fallback, 'error');

  function onSave() {
    update.mutate(
      { productName: productName.trim() || null, brandColor: color },
      {
        onSuccess: () => toast('Branding saved.'),
        onError: (e) => fail(e, 'Could not save branding.'),
      },
    );
  }

  return (
    <>
      <PageHeader
        title="Branding"
        subtitle="Make this instance your own — your product name, colour and logo across the whole app."
      />

      {/* Identity */}
      <Card>
        <CardHeader>
          <CardTitle>Identity</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-5">
          <Field label="Product name" htmlFor="product-name">
            <Input
              id="product-name"
              placeholder="WireHQ"
              value={productName}
              maxLength={64}
              onChange={(e) => setProductName(e.target.value)}
            />
            <p className="text-xs text-ink-500 dark:text-ink-400">
              Replaces “WireHQ” in the tab title and wherever the name is shown as text.
            </p>
          </Field>

          <div>
            <label className="text-sm font-medium text-ink-700 dark:text-ink-200" htmlFor="brand-color">
              Accent colour
            </label>
            <p className="mt-0.5 text-xs text-ink-500 dark:text-ink-400">
              Tints the interface accents. Leave as default to keep the WireHQ gold.
            </p>
            <div className="mt-2 flex items-center gap-3">
              <input
                id="brand-color"
                type="color"
                className="size-9 cursor-pointer rounded-lg border border-ink-200 bg-transparent p-0.5 dark:border-ink-700"
                value={color ?? DEFAULT_COLOR}
                onChange={(e) => setColor(e.target.value)}
              />
              <span className="font-mono text-sm text-ink-600 dark:text-ink-300">{color ?? 'default'}</span>
              {color && (
                <Button variant="ghost" size="sm" onClick={() => setColor(null)}>
                  <RotateCcw className="size-3.5" /> Use default
                </Button>
              )}
            </div>
          </div>

          <div>
            <Button onClick={onSave} disabled={update.isPending || settings.isLoading}>
              <Save className="size-4" /> {update.isPending ? 'Saving…' : 'Save'}
            </Button>
          </div>
        </CardContent>
      </Card>

      {/* Logos */}
      <Card className="mt-6">
        <CardHeader>
          <CardTitle>Logo &amp; favicon</CardTitle>
        </CardHeader>
        <CardContent className="grid grid-cols-1 gap-6 sm:grid-cols-3">
          <AssetUpload
            kind="LogoLight"
            label="Logo (light theme)"
            accept="image/png"
            assetId={settings.data?.logoLightAssetId ?? null}
            hint="PNG, ≤ 256 KB"
          />
          <AssetUpload
            kind="LogoDark"
            label="Logo (dark theme)"
            accept="image/png"
            assetId={settings.data?.logoDarkAssetId ?? null}
            hint="PNG, ≤ 256 KB"
          />
          <AssetUpload
            kind="Favicon"
            label="Favicon"
            accept="image/png,image/x-icon,.ico"
            assetId={settings.data?.faviconAssetId ?? null}
            hint="PNG or ICO, ≤ 256 KB"
          />
        </CardContent>
      </Card>

      <p className="mt-4 flex items-center gap-2 text-xs text-ink-400">
        <Palette className="size-3.5" /> Changes apply to everyone immediately. Logos fall back to the WireHQ mark when
        cleared.
      </p>
    </>
  );
}

function AssetUpload({
  kind,
  label,
  accept,
  assetId,
  hint,
}: {
  kind: BrandAssetKind;
  label: string;
  accept: string;
  assetId: string | null;
  hint: string;
}) {
  const toast = useToast();
  const upload = useUploadBrandAsset();
  const remove = useRemoveBrandAsset();
  const inputRef = useRef<HTMLInputElement>(null);

  const previewUrl = assetId ? `${config.apiBaseUrl}/api/v1/branding/assets/${assetId}` : null;
  const fail = (e: unknown, fallback: string) => toast(e instanceof ApiError ? e.message : fallback, 'error');

  function onPick(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) {
      return;
    }
    upload.mutate(
      { kind, file },
      {
        onSuccess: () => toast(`${label} updated.`),
        onError: (err) => fail(err, 'That image could not be uploaded.'),
      },
    );
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center justify-between">
        <span className="text-sm font-medium text-ink-700 dark:text-ink-200">{label}</span>
        {assetId && <Badge tone="success" dot>Set</Badge>}
      </div>
      <div className="flex aspect-video items-center justify-center overflow-hidden rounded-lg border border-dashed border-ink-200 bg-ink-50 dark:border-ink-700 dark:bg-ink-900">
        {previewUrl ? (
          <img src={previewUrl} alt={`${label} preview`} className="max-h-full max-w-full object-contain p-3" />
        ) : (
          <span className="text-xs text-ink-400">No image — using the WireHQ mark</span>
        )}
      </div>
      <input ref={inputRef} type="file" accept={accept} className="hidden" onChange={onPick} />
      <div className="flex items-center gap-2">
        <Button variant="secondary" size="sm" onClick={() => inputRef.current?.click()} disabled={upload.isPending}>
          <ImageUp className="size-3.5" /> {upload.isPending ? 'Uploading…' : 'Upload'}
        </Button>
        {assetId && (
          <Button
            variant="ghost"
            size="sm"
            onClick={() =>
              remove.mutate(kind, {
                onSuccess: () => toast(`${label} removed.`),
                onError: (err) => fail(err, 'Could not remove the image.'),
              })
            }
            disabled={remove.isPending}
          >
            <Trash2 className="size-3.5" /> Remove
          </Button>
        )}
      </div>
      <p className="text-[11px] text-ink-400">{hint}</p>
    </div>
  );
}
