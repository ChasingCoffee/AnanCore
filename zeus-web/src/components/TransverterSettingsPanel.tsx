// SPDX-License-Identifier: GPL-2.0-or-later
//
// Settings → Transverters. piHPSDR's model: each row is a band — RF range, LO
// and LO error. The operator tunes RF; the radio is sent RF − (LO + error).
// PA disable, open-collector bits, antennas and the XVTR input for a band are
// set in the existing PA and antenna tables, where the band now appears.

import { useEffect, useState, type CSSProperties } from 'react';
import type { TransverterBand } from '../api/client';
import { useTransverterStore } from '../state/transverter-store';

const MHZ = 1_000_000;

function ifRange(b: TransverterBand): string {
  const lo = b.loHz + b.loErrorHz;
  const a = (b.minHz - lo) / MHZ;
  const z = (b.maxHz - lo) / MHZ;
  if (!Number.isFinite(a) || !Number.isFinite(z)) return '—';
  const bad = a < 0 || z > 60;
  return `${a.toFixed(3)}–${z.toFixed(3)} MHz${bad ? ' ⚠ outside 0–60 MHz' : ''}`;
}

function nextId(bands: TransverterBand[]): number {
  return bands.reduce((m, b) => Math.max(m, b.id), 0) + 1;
}

export function TransverterSettingsPanel() {
  const stored = useTransverterStore((s) => s.bands);
  const load = useTransverterStore((s) => s.load);
  const save = useTransverterStore((s) => s.save);
  const [rows, setRows] = useState<TransverterBand[]>(stored);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => { void load(); }, [load]);
  useEffect(() => { setRows(stored); }, [stored]);

  const patch = (id: number, p: Partial<TransverterBand>) =>
    setRows((rs) => rs.map((r) => (r.id === id ? { ...r, ...p } : r)));
  const mhzField = (r: TransverterBand, key: 'minHz' | 'maxHz' | 'loHz', label: string) => (
    <input
      aria-label={`${r.name || 'band'} ${label} MHz`}
      style={num}
      inputMode="decimal"
      value={r[key] / MHZ}
      onChange={(e) => {
        const v = Number(e.target.value.replace(',', '.'));
        if (Number.isFinite(v)) patch(r.id, { [key]: Math.round(v * MHZ) } as Partial<TransverterBand>);
      }}
    />
  );

  const onSave = async () => {
    setSaving(true);
    setError(null);
    try {
      await save(rows);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div style={wrap}>
      <h4>Transverters</h4>
      <p style={help}>
        Tune the RF frequency; the radio is sent RF − (LO + LO error). Set each band&apos;s PA disable,
        open-collector bits, antennas and <b>XVTR</b> input in the PA and antenna tables above — the band
        appears there once saved. With the RX input set to XVTR, the XVTR port receives and carries the
        transmit output on a G2 / G2 Ultra.
      </p>
      <div style={{ overflowX: 'auto' }}>
        <table style={table}>
          <thead>
            <tr>
              <th>On</th><th>Name</th><th>RF start (MHz)</th><th>RF end (MHz)</th>
              <th>LO (MHz)</th><th>LO error (Hz)</th><th>Radio IF</th><th />
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id}>
                <td>
                  <input type="checkbox" aria-label={`${r.name || 'band'} enabled`} checked={r.enabled}
                    onChange={(e) => patch(r.id, { enabled: e.target.checked })} />
                </td>
                <td>
                  <input aria-label="band name" style={name} maxLength={12} value={r.name}
                    onChange={(e) => patch(r.id, { name: e.target.value })} />
                </td>
                <td>{mhzField(r, 'minHz', 'RF start')}</td>
                <td>{mhzField(r, 'maxHz', 'RF end')}</td>
                <td>{mhzField(r, 'loHz', 'LO')}</td>
                <td>
                  <input aria-label={`${r.name || 'band'} LO error Hz`} style={num} inputMode="numeric"
                    value={r.loErrorHz}
                    onChange={(e) => {
                      const v = Number(e.target.value);
                      if (Number.isInteger(v)) patch(r.id, { loErrorHz: v });
                    }} />
                </td>
                <td style={{ whiteSpace: 'nowrap' }}>{ifRange(r)}</td>
                <td>
                  <button type="button" className="btn sm ghost"
                    onClick={() => setRows((rs) => rs.filter((x) => x.id !== r.id))}>Remove</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <button type="button" className="btn sm" disabled={rows.length >= 16}
          onClick={() => setRows((rs) => [...rs, {
            id: nextId(rs), enabled: true, name: '2m',
            minHz: 144_000_000, maxHz: 148_000_000, loHz: 116_000_000, loErrorHz: 0,
          }])}>
          Add band
        </button>
        <button type="button" className="btn sm" disabled={saving} onClick={() => void onSave()}>
          {saving ? 'Saving…' : 'Save'}
        </button>
        {error ? <span role="alert" style={{ color: 'var(--tx, #e05656)' }}>{error}</span> : null}
      </div>
    </div>
  );
}

const wrap: CSSProperties = { display: 'flex', flexDirection: 'column', gap: 8, marginTop: 16 };
const help: CSSProperties = { margin: 0, fontSize: 12, color: 'var(--fg-2, #9aa4b1)' };
const table: CSSProperties = { borderCollapse: 'collapse', fontSize: 12 };
const num: CSSProperties = { width: 96 };
const name: CSSProperties = { width: 72 };
