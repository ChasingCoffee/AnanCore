import { describe, expect, it } from 'vitest';
import type { TransverterBand } from '../api/client';
import { transverterFor, transverterIfHz } from './transverter-store';
import { digitPlacesFor } from '../components/VfoDisplay';

const twoM: TransverterBand = {
  id: 1, enabled: true, name: '2m', minHz: 144_000_000, maxHz: 148_000_000, loHz: 116_000_000, loErrorHz: 0,
};

describe('transverter bands on the client', () => {
  it('finds the enabled band for an RF frequency, and the IF the radio is on', () => {
    const b = transverterFor(144_200_000, [twoM]);
    expect(b?.name).toBe('2m');
    expect(transverterIfHz(144_200_000, b!)).toBe(28_200_000);
    expect(transverterIfHz(144_200_000, { ...twoM, loErrorHz: 1_500 })).toBe(28_198_500);
  });

  it('ignores disabled bands and HF', () => {
    expect(transverterFor(144_200_000, [{ ...twoM, enabled: false }])).toBeNull();
    expect(transverterFor(14_200_000, [twoM])).toBeNull();
  });
});

describe('VFO digit places', () => {
  it('keeps HF at eight places and grows for transverter frequencies', () => {
    expect(digitPlacesFor(14_200_000)).toHaveLength(8);
    expect(digitPlacesFor(144_200_000)).toHaveLength(9);
    const ghz = digitPlacesFor(2_300_100_000);
    expect(ghz).toHaveLength(10);
    expect(ghz[0]).toEqual({ decade: 1_000_000_000, separatorAfter: '.' });
    expect(digitPlacesFor(10_000_000_000)).toHaveLength(11);
  });
});
