// SPDX-License-Identifier: GPL-2.0-or-later
//
// The docked Audio Tools rails: each header offers only its TX / RX Suite
// button (plugin scanning, folders and clearing live in the Suite window),
// and there is one Native route with no engine toggle.
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

async function settle(): Promise<void> {
  for (let i = 0; i < 5; i++) {
    await act(async () => {
      await new Promise((r) => setTimeout(r, 0));
    });
  }
}

describe('TxAudioToolsPanel rails', () => {
  beforeEach(() => {
    useAudioSuiteStore.setState(useAudioSuiteStore.getInitialState(), true);
    // Permissive default for the rails' on-mount loaders (chain order /
    // master bypass).
    vi.stubGlobal('fetch', vi.fn<typeof fetch>(async () => response({})));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
    useAudioSuiteStore.setState(useAudioSuiteStore.getInitialState(), true);
  });

  it('offers only the Suite button in each header; scanning lives in the Suite', async () => {
    const { container, unmount } = render(createElement(TxAudioToolsPanel));
    await settle();

    const headerButtons = (region: string) =>
      Array.from(
        container.querySelector(`[aria-label="${region}"]`)?.querySelectorAll('button') ?? [],
      )
        .map((b) => b.textContent?.trim() ?? '')
        .filter((t) => /Suite$|^Scan|VST3|Download VST Engine/.test(t));

    expect(headerButtons('TX Audio')).toEqual(['TX Suite']);
    expect(headerButtons('RX Audio')).toEqual(['RX Suite']);

    unmount();
  });

  it('has no Native/VST route toggle and reports one Native route', async () => {
    const { container, unmount } = render(createElement(TxAudioToolsPanel));
    await settle();

    const buttons = Array.from(container.querySelectorAll('button'));
    expect(buttons.some((b) => b.textContent === 'VST' || b.textContent === 'Native')).toBe(false);
    expect(container.textContent).not.toContain('VST OFF');
    expect(container.textContent).not.toContain('VST ON');
    expect(container.textContent).toContain('NATIVE');
    expect(container.textContent).not.toContain('VST engine not available');

    unmount();
  });
});
