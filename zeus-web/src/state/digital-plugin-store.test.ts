// SPDX-License-Identifier: GPL-2.0-or-later
//
// digital-plugin-store tests — FUSED BUILD. The digital backend is compiled
// into core, so the mode gate is hardwired open: probe() asserts installed +
// live without discovering anything (no /api/plugins listing, no /status
// probe), whatever the backend or the plugins registry says. An app-WS
// reconnect re-asserts it, which re-syncs the SSE stream.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  isDigitalPluginReady,
  useDigitalPluginStore,
  wsjtxLiveSubset,
} from './digital-plugin-store';
import { usePluginsStore } from '../plugins/state/plugins-store';
import { useDisplayStore } from './display-store';
import {
  DIGITAL_PLUGIN_BASE,
  DIGITAL_PLUGIN_ID,
  LEGACY_DIGITAL_PLUGIN_ID,
  digitalPluginBase,
  resolveDigitalPluginId,
} from '../api/digital-plugin';
import { parsePluginDto } from '../plugins/api/plugins';

const flush = () => new Promise((r) => setTimeout(r, 0));

/** Route-aware fetch stub: /status answers `statusCode`; /api/plugins answers
 *  the given installed list; anything else 200-empty (config pushes etc.). */
function stubFetch(statusCode: number, pluginIds: string[] = []) {
  const fn = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url.endsWith('/status')) {
      return {
        ok: statusCode >= 200 && statusCode < 300,
        status: statusCode,
        headers: new Headers(),
        json: async () => ({ ok: true }),
      };
    }
    if (url === '/api/plugins') {
      return {
        ok: true,
        status: 200,
        headers: new Headers(),
        json: async () => ({
          sdkAbi: 1,
          sdkVersion: '1.2.0',
          plugins: pluginIds.map((id) => ({ id, scanned: false, name: id, version: '1.0.0' })),
        }),
      };
    }
    return { ok: true, status: 200, headers: new Headers(), json: async () => ({}) };
  });
  vi.stubGlobal('fetch', fn as never);
  return fn;
}

function installDigitalPlugin() {
  usePluginsStore.setState({
    installed: [parsePluginDto({ id: DIGITAL_PLUGIN_ID, name: 'Zeus Digital' })],
  });
}

describe('digital-plugin-store', () => {
  beforeEach(() => {
    stubFetch(200);
    useDigitalPluginStore.setState({
      installed: false,
      pluginId: null,
      live: false,
      probed: false,
      sseConnected: false,
    });
    usePluginsStore.setState({ installed: [] });
    useDisplayStore.setState({ connected: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('probe opens the gate: installed + live, with the canonical id', async () => {
    await useDigitalPluginStore.getState().probe();
    const st = useDigitalPluginStore.getState();
    expect(st.installed).toBe(true);
    expect(st.live).toBe(true);
    expect(st.pluginId).toBe(DIGITAL_PLUGIN_ID);
    expect(st.probed).toBe(true);
    expect(isDigitalPluginReady()).toBe(true);
  });

  it('probe discovers nothing: no /api/plugins listing, no /status probe', async () => {
    const fn = stubFetch(404);
    await useDigitalPluginStore.getState().probe();
    const urls = fn.mock.calls.map((c) => String(c[0]));
    expect(urls).not.toContain('/api/plugins');
    expect(urls).not.toContain(`${DIGITAL_PLUGIN_BASE}/status`);
    expect(useDigitalPluginStore.getState().live).toBe(true);
  });

  it('the gate stays open on a network error', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new Error('offline'))) as never);
    await useDigitalPluginStore.getState().probe();
    expect(isDigitalPluginReady()).toBe(true);
  });

  it('the plugins registry does not move the gate', async () => {
    await useDigitalPluginStore.getState().probe();
    installDigitalPlugin();
    await flush();
    usePluginsStore.setState({ installed: [] });
    await flush();
    expect(useDigitalPluginStore.getState().installed).toBe(true);
    expect(useDigitalPluginStore.getState().pluginId).toBe(DIGITAL_PLUGIN_ID);
  });

  it('resolves the preferred new id when installed', () => {
    const installed = [
      parsePluginDto({ id: LEGACY_DIGITAL_PLUGIN_ID, name: 'Zeus Digital Legacy' }),
      parsePluginDto({ id: DIGITAL_PLUGIN_ID, name: 'Zeus Digital' }),
    ];

    expect(resolveDigitalPluginId(installed)).toBe(DIGITAL_PLUGIN_ID);
  });

  it('uses the preferred base when neither id is installed', () => {
    expect(resolveDigitalPluginId([])).toBeNull();
    usePluginsStore.setState({ installed: [] });
    expect(digitalPluginBase()).toBe(DIGITAL_PLUGIN_BASE);
  });

  it('isDigitalPluginReady requires BOTH installed and live', () => {
    useDigitalPluginStore.setState({ installed: true, live: false });
    expect(isDigitalPluginReady()).toBe(false);
    useDigitalPluginStore.setState({ installed: false, live: true });
    expect(isDigitalPluginReady()).toBe(false);
    useDigitalPluginStore.setState({ installed: true, live: true });
    expect(isDigitalPluginReady()).toBe(true);
  });

  it('re-asserts the gate on an app-WS reconnect (server restarted under the tab)', async () => {
    useDisplayStore.setState({ connected: true }); // rising edge
    await flush();
    const st = useDigitalPluginStore.getState();
    expect(st.probed).toBe(true);
    expect(st.live).toBe(true);
  });
});

describe('wsjtxLiveSubset', () => {
  const base = {
    enabled: true,
    host: '127.0.0.1',
    port: 2237,
    instanceId: 'WSJT-X',
    transport: 'unicast' as const,
    multicastGroup: '224.0.0.73',
    multicastTtl: 1,
    sendLiveDecodes: true,
  };

  it('unicast: forwards host, enabled = enabled && sendLiveDecodes', () => {
    expect(wsjtxLiveSubset(base)).toEqual({
      enabled: true,
      host: '127.0.0.1',
      port: 2237,
      multicast: false,
      instanceId: 'WSJT-X',
      multicastTtl: 1,
    });
  });

  it('multicast: the group becomes the host, id + TTL forwarded', () => {
    expect(wsjtxLiveSubset({ ...base, transport: 'multicast', instanceId: 'ZeusDigi', multicastTtl: 4 })).toEqual({
      enabled: true,
      host: '224.0.0.73',
      port: 2237,
      multicast: true,
      instanceId: 'ZeusDigi',
      multicastTtl: 4,
    });
  });

  it('live decodes off ⇒ disabled even when the broadcaster is enabled', () => {
    expect(wsjtxLiveSubset({ ...base, sendLiveDecodes: false }).enabled).toBe(false);
  });
});
