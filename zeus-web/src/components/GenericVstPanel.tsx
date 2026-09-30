// SPDX-License-Identifier: GPL-2.0-or-later
//
// GenericVstPanel — the rack body for a plugin that ships no UI module
// of its own, i.e. a VST3 registered via "Add VST directory". The host
// can't render a VST3's native editor inside the browser (it's a native
// OS window, not HTML), but the in-process bridge CAN open that real
// editor as a separate window on the host machine's desktop — exactly
// like a standalone VST host. This panel surfaces that "Open Editor"
// action plus the plugin's identity. Reorder / park / remove still work
// from the rack slot chrome, and the VST processes audio regardless.

import { useVstEditor, type VstEditorRoute } from './useVstEditor';
import { useAudioSuiteStore } from '../state/audio-suite-store';

interface GenericVstPanelProps {
  pluginId: string;
  name: string;
  route?: VstEditorRoute;
}

export function GenericVstPanel({ pluginId, name, route = 'tx' }: GenericVstPanelProps) {
  const { open, busy, starting, crashed, error, toggle, openEditor } = useVstEditor(
    pluginId,
    true,
    route,
  );
  const repairVstEngine = useAudioSuiteStore((s) => s.repairVstEngine);
  const enginePhase = useAudioSuiteStore((s) => s.vstEngineInstall.phase);
  const repairing =
    enginePhase === 'downloading' ||
    enginePhase === 'extracting' ||
    enginePhase === 'staging' ||
    enginePhase === 'configuring';

  // The editor opens only when the operator asks (Open Editor below).
  // Selecting a plugin — including the first one, selected automatically
  // when the Suite opens — just shows this panel.

  return (
    <div
      style={{
        display: 'flex',
        flexDirection: 'column',
        gap: 10,
        fontSize: 11,
        color: 'var(--fg-2)',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <span
          style={{
            fontSize: 9,
            fontWeight: 700,
            letterSpacing: 1,
            color: 'var(--fg-0)',
            background: 'var(--bg-2)',
            border: '1px solid var(--line)',
            borderRadius: 3,
            padding: '1px 5px',
          }}
        >
          VST3
        </span>
        <span style={{ color: 'var(--fg-1)', fontWeight: 600 }}>{name}</span>
      </div>

      <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
        <button
          type="button"
          onClick={() => void toggle()}
          disabled={busy}
          title={
            open
              ? 'Close the plugin’s native editor window'
              : 'Open the plugin’s real GUI as a separate desktop window'
          }
          style={{
            padding: '6px 14px',
            borderRadius: 4,
            border: '1px solid ' + (open ? 'var(--tx)' : 'var(--accent)'),
            background: open ? 'var(--tx)' : 'var(--accent)',
            color: '#fff',
            cursor: busy ? 'progress' : 'pointer',
            opacity: busy ? 0.6 : 1,
            fontSize: 11,
            fontWeight: 600,
            letterSpacing: 0.6,
            textTransform: 'uppercase',
            fontFamily: 'inherit',
          }}
        >
          {busy ? (starting ? 'Starting…' : '…') : open ? 'Close Editor' : 'Open Editor'}
        </button>
        <span style={{ color: 'var(--fg-3)', fontSize: 10, lineHeight: 1.3, flex: 1, minWidth: 160 }}>
          Open Editor shows the plugin&rsquo;s own window on the desktop of
          the computer running Zeus — a plugin GUI is a native window, not
          browser HTML, so it can&rsquo;t render here. The plugin processes
          audio whether its editor is open or not.
        </span>
      </div>

      {starting && (
        <div
          style={{
            fontSize: 10,
            color: 'var(--fg-2)',
            background: 'var(--bg-2)',
            border: '1px solid var(--line)',
            borderRadius: 3,
            padding: '5px 8px',
            lineHeight: 1.4,
          }}
        >
          VST engine starting&hellip; the editor opens automatically once it&rsquo;s
          routing. This can take a few seconds on first use.
        </div>
      )}

      {crashed && (
        <button
          type="button"
          disabled={repairing}
          onClick={async () => {
            await repairVstEngine();
            openEditor();
          }}
          title="Re-download the verified VST engine and retry"
          style={{
            alignSelf: 'flex-start',
            padding: '5px 12px',
            borderRadius: 4,
            border: '1px solid var(--accent)',
            background: repairing ? 'var(--bg-2)' : 'var(--accent)',
            color: repairing ? 'var(--fg-2)' : '#fff',
            cursor: repairing ? 'progress' : 'pointer',
            fontSize: 11,
            fontWeight: 600,
            letterSpacing: 0.6,
            textTransform: 'uppercase',
            fontFamily: 'inherit',
          }}
        >
          {repairing ? 'Repairing…' : 'Repair Engine'}
        </button>
      )}

      {error && (
        <div
          role="alert"
          style={{
            fontSize: 10,
            color: 'var(--tx)',
            background: 'var(--tx-soft)',
            border: '1px solid var(--tx)',
            borderRadius: 3,
            padding: '5px 8px',
            lineHeight: 1.4,
          }}
        >
          {error}
        </div>
      )}

      <code
        style={{
          fontSize: 9.5,
          color: 'var(--fg-3)',
          fontFamily: 'var(--font-mono, JetBrains Mono, monospace)',
          wordBreak: 'break-all',
        }}
      >
        {pluginId}
      </code>
    </div>
  );
}
