// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

import { useId, useRef, useState } from 'react';
import { X } from 'lucide-react';
import { useDialogFocusTrap } from '../layout/useDialogFocusTrap';
import type { PluginScanPaths } from '../state/audio-suite-store';

interface PluginScanPathsDialogProps {
  paths: PluginScanPaths;
  /** Save both lists; an empty list means "the standard folders". */
  onSave: (vst3: string[], clap: string[]) => Promise<{ ok: boolean; error?: string }>;
  onCancel: () => void;
}

const toText = (dirs: string[]) => dirs.join('\n');
const toList = (text: string) =>
  text
    .split(/\r?\n/)
    .map((d) => d.trim())
    .filter((d) => d.length > 0);

/**
 * "Set paths" — the folders Scan VST3 and Scan CLAP sweep, one per line.
 * They are folders on the machine running the Zeus server. Clearing a list
 * (or "Use standard folders") returns that format to the OS defaults.
 */
export function PluginScanPathsDialog({ paths, onSave, onCancel }: PluginScanPathsDialogProps) {
  const titleId = useId();
  const bodyId = useId();
  const vst3Id = useId();
  const clapId = useId();
  const [vst3, setVst3] = useState(paths.vst3Custom ? toText(paths.vst3) : '');
  const [clap, setClap] = useState(paths.clapCustom ? toText(paths.clap) : '');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const dialogRef = useRef<HTMLDivElement | null>(null);
  const vst3Ref = useRef<HTMLTextAreaElement | null>(null);

  useDialogFocusTrap({ dialogRef, initialFocusRef: vst3Ref, onClose: onCancel });

  const save = async () => {
    setSaving(true);
    setError(null);
    const res = await onSave(toList(vst3), toList(clap));
    setSaving(false);
    if (!res.ok) setError(res.error ?? 'Could not save the folders.');
  };

  const section = (
    id: string,
    label: string,
    value: string,
    setValue: (v: string) => void,
    defaults: string[],
    ref?: React.RefObject<HTMLTextAreaElement | null>,
  ) => (
    <label className="text-input-dialog-field">
      <span id={id} style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <span style={{ flex: 1 }}>{label}</span>
        <button
          type="button"
          className="btn ghost"
          style={{ padding: '2px 6px', fontSize: 9 }}
          onClick={() => setValue(toText(defaults))}
          title={defaults.length ? defaults.join('\n') : 'No standard folders on this system'}
        >
          Insert standard folders
        </button>
      </span>
      <textarea
        ref={ref}
        rows={4}
        value={value}
        placeholder={defaults.join('\n')}
        spellCheck={false}
        aria-labelledby={id}
        onChange={(e) => setValue(e.target.value)}
      />
    </label>
  );

  return (
    <div className="modal-backdrop confirm-dialog-backdrop">
      <div
        ref={dialogRef}
        className="confirm-dialog confirm-dialog--primary"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={bodyId}
        tabIndex={-1}
        onClick={(e) => e.stopPropagation()}
        style={{ width: 'min(560px, 100%)' }}
      >
        <div className="confirm-dialog-header">
          <h2 id={titleId}>Plugin folders</h2>
          <button
            type="button"
            className="workspace-tile-close"
            aria-label="Close dialog"
            title="Close (Esc)"
            onClick={onCancel}
            style={{ width: 22, height: 22 }}
          >
            <X size={12} aria-hidden />
          </button>
        </div>
        <div id={bodyId} className="confirm-dialog-body">
          <p>
            Folders on the Zeus server that Scan VST3 and Scan CLAP search, one per line.
            Leave a list empty to use the standard folders.
          </p>
          {section(vst3Id, 'VST3 folders', vst3, setVst3, paths.defaultVst3, vst3Ref)}
          {section(clapId, 'CLAP folders', clap, setClap, paths.defaultClap)}
          {error && <p style={{ color: 'var(--tx)' }}>{error}</p>}
        </div>
        <div className="confirm-dialog-actions">
          <button type="button" className="btn ghost" onClick={onCancel}>
            Cancel
          </button>
          <button type="button" className="btn active" onClick={() => void save()} disabled={saving}>
            {saving ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  );
}
