// SPDX-License-Identifier: GPL-2.0-or-later
/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render } from './meters/__tests__/harness';
import { GenericVstPanel } from './GenericVstPanel';

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });
}

describe('GenericVstPanel', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('waits for Open Editor instead of opening the editor when shown', async () => {
    let windowOpen = false;
    const fetchMock = vi.fn<typeof fetch>(async (_input, init) => {
      if (init?.method === 'POST') windowOpen = true;
      return jsonResponse({ open: windowOpen });
    });
    vi.stubGlobal('fetch', fetchMock);

    const { container, unmount } = render(
      createElement(GenericVstPanel, { pluginId: 'com.openhpsdr.zeus.vst.clear', name: 'Clear' }),
    );
    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    const posts = () => fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST');
    expect(posts()).toHaveLength(0);

    const button = Array.from(container.querySelectorAll('button')).find(
      (b) => b.textContent === 'Open Editor',
    );
    expect(button).toBeTruthy();
    await act(async () => {
      button!.click();
      await Promise.resolve();
      await Promise.resolve();
    });
    expect(posts()).toHaveLength(1);

    unmount();
  });
});
