// SPDX-License-Identifier: GPL-2.0-or-later
//
// '?' shows every keyboard and mouse binding. Field report #62 (WD4X) asked
// for keyboard tuning that already existed — arrows tune, Space is PTT — but
// nothing in the app said so. This works whether or not a radio is connected,
// and ignores '?' typed into a text field.

import { useEffect, useState, type CSSProperties } from 'react';
import { SHORTCUT_GROUPS } from '../util/keyboard-shortcuts-list';

function isEditable(t: EventTarget | null): boolean {
  const el = t as HTMLElement | null;
  if (!el) return false;
  if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement) return true;
  return el.isContentEditable === true;
}

export function ShortcutsOverlay() {
  const [open, setOpen] = useState(false);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (isEditable(e.target)) return;
      if (e.key === '?' && !e.ctrlKey && !e.metaKey && !e.altKey) {
        e.preventDefault();
        setOpen((v) => !v);
        return;
      }
      if (e.key === 'Escape') setOpen(false);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  if (!open) return null;
  return (
    <div role="dialog" aria-label="Keyboard and mouse shortcuts" style={backdrop} onClick={() => setOpen(false)}>
      <div style={card} onClick={(e) => e.stopPropagation()}>
        <div style={head}>
          <span>KEYBOARD &amp; MOUSE SHORTCUTS</span>
          <button type="button" onClick={() => setOpen(false)} style={close} aria-label="Close">×</button>
        </div>
        <div style={grid}>
          {SHORTCUT_GROUPS.map((g) => (
            <section key={g.title}>
              <div style={groupTitle}>{g.title}</div>
              {g.entries.map((en) => (
                <div key={en.keys} style={row}>
                  <kbd style={kbd}>{en.keys}</kbd>
                  <span style={action}>{en.action}</span>
                </div>
              ))}
            </section>
          ))}
        </div>
        <div style={foot}>Press ? or Esc to close. Keys are ignored while typing in a text field.</div>
      </div>
    </div>
  );
}

const backdrop: CSSProperties = {
  position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.55)', zIndex: 1000,
  display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 16,
};
const card: CSSProperties = {
  background: '#0f1319', border: '1px solid #2a3140', borderRadius: 8, padding: '14px 18px',
  maxWidth: 760, width: '100%', maxHeight: '85vh', overflow: 'auto', color: '#e8ecf1',
};
const head: CSSProperties = {
  display: 'flex', justifyContent: 'space-between', alignItems: 'center',
  font: '600 11px var(--font-mono, monospace)', letterSpacing: '0.1em', marginBottom: 10,
};
const close: CSSProperties = { background: 'none', border: 'none', color: '#9aa4b1', fontSize: 18, cursor: 'pointer' };
const grid: CSSProperties = { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(300px, 1fr))', gap: '12px 24px' };
const groupTitle: CSSProperties = { color: '#f0b33a', font: '600 11px var(--font-ui, system-ui)', margin: '4px 0 6px' };
const row: CSSProperties = { display: 'flex', gap: 10, alignItems: 'baseline', padding: '3px 0' };
const kbd: CSSProperties = {
  minWidth: 120, flexShrink: 0, font: '500 11px var(--font-mono, monospace)', color: '#cfe6ff',
  background: '#1a2030', border: '1px solid #2a3140', borderRadius: 4, padding: '1px 6px',
};
const action: CSSProperties = { font: '400 12px var(--font-ui, system-ui)', color: '#cfd6e0' };
const foot: CSSProperties = { marginTop: 12, font: '400 11px var(--font-ui, system-ui)', color: '#7d8694' };
