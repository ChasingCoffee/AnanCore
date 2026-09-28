import { afterEach, describe, expect, it } from 'vitest';
import { formatImdDbc, formatImdValue } from './imd-format';
import { useTxStore } from '../state/tx-store';

describe('IMD readout text', () => {
  it('shows a measurement as a plain number', () => {
    expect(formatImdDbc(-47.34, false)).toBe('-47.3 dBc');
    expect(formatImdValue(-47.34, false)).toBe('-47.3');
  });

  it('shows a value below the measurable limit as an upper bound, never a bare number', () => {
    expect(formatImdDbc(-59.04, true)).toBe('< -59.0 dBc');
    expect(formatImdValue(-59.04, true)).toBe('< -59.0');
  });

  it('shows a dash when there is nothing to measure', () => {
    expect(formatImdDbc(NaN, false)).toBe('—');
    expect(formatImdDbc(NaN, true)).toBe('—');
  });
});

describe('PS meters carry the IMD bound flags', () => {
  afterEach(() => useTxStore.setState({ psImd3IsBound: false, psImd5IsBound: false }));

  it('stores the flags from the meters frame, defaulting to plain values', () => {
    const base = { feedbackLevel: 1, correctionDb: 0, calState: 0, correcting: true, maxTxEnvelope: 0.5 };
    useTxStore.getState().setPsMeters({ ...base, imd3Dbc: -59, imd5Dbc: -61, imd3IsBound: true, imd5IsBound: false });
    expect(useTxStore.getState().psImd3IsBound).toBe(true);
    expect(useTxStore.getState().psImd5IsBound).toBe(false);

    // An older server sends no flags: values are plain measurements.
    useTxStore.getState().setPsMeters({ ...base, imd3Dbc: -45, imd5Dbc: -55 });
    expect(useTxStore.getState().psImd3IsBound).toBe(false);
    expect(useTxStore.getState().psImd5IsBound).toBe(false);
  });
});
