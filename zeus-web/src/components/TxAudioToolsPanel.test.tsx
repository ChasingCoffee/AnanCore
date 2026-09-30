// SPDX-License-Identifier: GPL-2.0-or-later
//
// Bug 2 regression: the docked Audio Tools rail showed NO affordance button on
// macOS because the per-OS platform flags were never fetched for the docked
// rails. The fix has the TX and RX rails call loadEngineSupportFromServer on
// mount; this test proves the macOS shape (engineSupported=false,
// auSupported=true) makes the in-process "Scan AU" affordance render. Before the
// fix engineSupportLoaded stayed false and neither the Download nor the Scan
// button appeared.
//
// `./meters/__tests__/harness` is imported first for its side-effect: it
// installs a dependable in-memory localStorage polyfill before any Zustand
// store module loads (jsdom's bare localStorage breaks the persist middleware).

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { act, render } from './meters/__tests__/harness';
import { useAudioSuiteStore } from '../state/audio-suite-store';
import { TxAudioToolsPanel } from './TxAudioToolsPanel';

// Neutralise the panel's non-rail children so the test exercises only the
// docked rails' mount behaviour (no plugin panels, no CFC fetches). The audio
// devices rail self-disables when host capabilities are unseeded.
vi.mock('../plugins/runtime/usePluginPanels', () => ({
  usePluginPanels: () => [],
}));
vi.mock('./CfcSettingsPanel', () => ({
  CfcSettingsPanel: () => null,
}));

function response(body: unknown, ok = true): Response {
  return {
    ok,
    status: ok ? 200 : 500,
    json: async () => body,
  } as Response;
}

// macOS shape: no out-of-process engine, AU hosting available in-process.
const MACOS_INSTALL_DTO = {
  phase: 'idle',
  percent: 0,
  engineAvailable: false,
  engineSupported: false,
  inProcessHostSupported: true,
  auSupported: true,
};

async function flush(cond: () => boolean): Promise<void> {
  for (let i = 0; i < 50; i++) {
    if (cond()) return;
    // eslint-disable-next-line no-await-in-loop
    await act(async () => {
      await new Promise((r) => setTimeout(r, 0));
    });
  }
}

describe('TxAudioToolsPanel docked rails (Bug 2 platform affordance)', () => {
  beforeEach(() => {
    useAudioSuiteStore.setState(useAudioSuiteStore.getInitialState(), true);
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      if (String(input) === '/api/tx-audio-suite/vst-engine/install') {
        return response(MACOS_INSTALL_DTO);
      }
      // Permissive default for the rails' other on-mount loaders
      // (chain order / master-bypass / processing-mode).
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
    useAudioSuiteStore.setState(useAudioSuiteStore.getInitialState(), true);
  });

  it('fetches the platform affordance DTO on mount and renders the in-process Scan button', async () => {
    const { container, unmount } = render(createElement(TxAudioToolsPanel));

    await flush(() => useAudioSuiteStore.getState().engineSupportLoaded);

    // The rails pulled the per-OS flags from the server.
    expect(fetch).toHaveBeenCalledWith('/api/tx-audio-suite/vst-engine/install');
    const state = useAudioSuiteStore.getState();
    expect(state.engineSupportLoaded).toBe(true);
    expect(state.engineSupported).toBe(false);
    expect(state.auSupported).toBe(true);

    // macOS affordance is rendered (auSupported => "Scan AU"); the Windows-only
    // "Download VST Engine" button is not.
    const buttons = Array.from(container.querySelectorAll('button'));
    const scan = buttons.find((b) => b.textContent === 'Scan AU');
    expect(scan).toBeTruthy();
    expect(buttons.some((b) => b.textContent?.includes('Download VST Engine'))).toBe(false);

    unmount();
  });

  it('puts the header buttons in the same order on TX and RX', async () => {
    const { container, unmount } = render(createElement(TxAudioToolsPanel));
    await flush(() => useAudioSuiteStore.getState().engineSupportLoaded);

    const order = (region: string) =>
      Array.from(
        container.querySelector(`[aria-label="${region}"]`)?.querySelectorAll('button') ?? [],
      )
        .map((b) => b.textContent?.trim() ?? '')
        .filter((t) => /Suite$|^Scan AU$|^\+ VST3$/.test(t));

    expect(order('TX Audio')).toEqual(['TX Suite', 'Scan AU', '+ VST3']);
    expect(order('RX Audio')).toEqual(['RX Suite', 'Scan AU', '+ VST3']);

    unmount();
  });
});

// Windows shape: the platform where the retired out-of-process engine was
// supported. Before the retirement this DTO made BOTH rails render the engine
// download button INSTEAD of Scan/Add — and TX showed even that only in VST
// mode — so Windows had no way to add a VST3 at all (field, issue #62).
const WINDOWS_INSTALL_DTO = {
  phase: 'idle',
  percent: 0,
  engineAvailable: false,
  engineSupported: true,
  inProcessHostSupported: true,
  auSupported: false,
};

describe('TxAudioToolsPanel on Windows (VST engine retired)', () => {
  beforeEach(() => {
    useAudioSuiteStore.setState(useAudioSuiteStore.getInitialState(), true);
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
        const url = String(input);
        if (url.endsWith('/vst-engine/install')) return response(WINDOWS_INSTALL_DTO);
        return response({});
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
    useAudioSuiteStore.setState(useAudioSuiteStore.getInitialState(), true);
  });

  it('offers Scan/Add on both rails, in Native mode, and never the engine download', async () => {
    const { container, unmount } = render(createElement(TxAudioToolsPanel));
    await flush(() => useAudioSuiteStore.getState().engineSupportLoaded);
    expect(useAudioSuiteStore.getState().engineSupported).toBe(true);

    const buttons = Array.from(container.querySelectorAll('button'));
    // One Scan/Add per rail (TX and RX), even though processing mode is Native.
    expect(buttons.filter((b) => b.textContent === 'Scan plugins')).toHaveLength(2);
    expect(buttons.some((b) => b.textContent?.includes('Download VST Engine'))).toBe(false);
    expect(container.textContent).not.toContain('VST engine not available');

    unmount();
  });

  it('has no Native/VST route toggle and reports one Native route', async () => {
    const { container, unmount } = render(createElement(TxAudioToolsPanel));
    await flush(() => useAudioSuiteStore.getState().engineSupportLoaded);

    const buttons = Array.from(container.querySelectorAll('button'));
    expect(buttons.some((b) => b.textContent === 'VST' || b.textContent === 'Native')).toBe(false);
    expect(container.textContent).not.toContain('VST OFF');
    expect(container.textContent).not.toContain('VST ON');
    expect(container.textContent).toContain('NATIVE');

    unmount();
  });
});
