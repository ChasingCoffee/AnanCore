// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it } from 'vitest';
import { classifyDecode } from './Ft8DecodeTable';
import type { Ft8Row } from '../../state/ft8-store';

function row(text: string, extra?: Partial<Ft8Row>): Ft8Row {
  return {
    id: 'x',
    receiver: 0,
    protocol: 'FT8',
    slotStartUnixMs: 0,
    snrDb: -10,
    dtSec: 0.1,
    freqHz: 1234,
    score: 20,
    text,
    ...extra,
  };
}

describe('classifyDecode', () => {
  it('flags CQ calls', () => {
    expect(classifyDecode(row('CQ RK9AX MO05'))).toBe('cq');
    // A CQ stays a CQ even if it is my own call CQing.
    expect(classifyDecode(row('CQ KB2UKA FN12'), 'KB2UKA')).toBe('cq');
  });

  it('flags messages directed at my call (call-to slot)', () => {
    expect(classifyDecode(row('KB2UKA RK9AX -12'), 'KB2UKA')).toBe('me');
    expect(classifyDecode(row('kb2uka rk9ax 73'), 'KB2UKA')).toBe('me'); // case-insensitive
  });

  it('does NOT flag me when I am only the caller (second slot)', () => {
    expect(classifyDecode(row('RK9AX KB2UKA RR73'), 'KB2UKA')).toBe('normal');
  });

  it('flags worked-before from the legacy server row flag (fallback)', () => {
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05', { workedBefore: true }))).toBe('worked');
    // Same message WITHOUT the flag and no worked set is not worked.
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05', { workedBefore: false }))).toBe('normal');
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05'))).toBe('normal');
  });

  it('flags worked-before at render time from the worked-calls set', () => {
    const worked = new Set(['RK9AX']);
    // Sender (DE call) is in the set → worked, regardless of the row flag.
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05'), undefined, undefined, worked)).toBe('worked');
    // A different sender is untouched.
    expect(classifyDecode(row('GJ0KYZ K1ABC MO05'), undefined, undefined, worked)).toBe('normal');
    // The TARGET being worked must NOT flag the row (sender is token 1).
    expect(classifyDecode(row('RK9AX K1ABC MO05'), undefined, undefined, worked)).toBe('normal');
    // A worked station's CQ shows as worked; 'me' still outranks worked.
    expect(classifyDecode(row('CQ RK9AX MO05'), undefined, undefined, worked)).toBe('worked');
    expect(classifyDecode(row('CQ K1ABC FN42'), undefined, undefined, worked)).toBe('cq');
    expect(classifyDecode(row('KB2UKA RK9AX -12'), 'KB2UKA', undefined, worked)).toBe('me');
  });

  it('defaults to normal', () => {
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05'))).toBe('normal');
  });

  it('lights a new (unworked) grid on a directed decode', () => {
    const grids = new Set(['FN42']);
    // MO05 not yet worked → new; FN42 already worked → normal.
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05'), undefined, grids)).toBe('new');
    expect(classifyDecode(row('GJ0KYZ RK9AX FN42'), undefined, grids)).toBe('normal');
  });

  it('keeps CQ green even when its grid is new', () => {
    const grids = new Set<string>();
    expect(classifyDecode(row('CQ RK9AX MO05'), undefined, grids)).toBe('cq');
  });

  it('prefers worked-before over new-grid', () => {
    const grids = new Set<string>();
    expect(classifyDecode(row('GJ0KYZ RK9AX MO05', { workedBefore: true }), undefined, grids)).toBe(
      'worked',
    );
  });

  it('prefers calling-me over worked-before', () => {
    // I am being called AND the sender is worked-before → 'me' wins.
    expect(
      classifyDecode(row('KB2UKA RK9AX -12', { workedBefore: true }), 'KB2UKA'),
    ).toBe('me');
  });

  it('shows a worked station\'s CQ as worked-before', () => {
    expect(classifyDecode(row('CQ RK9AX MO05', { workedBefore: true }))).toBe('worked');
  });

  it('lights a sender from a DXCC entity not yet worked', () => {
    const worked = new Set([281, 291]); // Spain, United States
    const dx = (text: string, dxcc: number | null) => row(text, { dxcc });
    expect(classifyDecode(dx('CQ EA8AR IL18', 29), undefined, undefined, undefined, worked)).toBe('dxcc');
    expect(classifyDecode(dx('CQ EA5IUE IM76', 281), undefined, undefined, undefined, worked)).toBe('cq');
    expect(classifyDecode(dx('K1ABC EA8AR -10', 29), undefined, undefined, undefined, worked)).toBe('dxcc');
    // Someone calling me still shows as 'me', new DXCC or not.
    expect(classifyDecode(dx('KB2UKA EA8AR -10', 29), 'KB2UKA', undefined, undefined, worked)).toBe('me');
    // A worked-before station from a new entity is still a new DXCC (a new country for the log).
    expect(
      classifyDecode(dx('CQ EA8AR IL18', 29), undefined, undefined, new Set(['EA8AR']), worked),
    ).toBe('dxcc');
    // Unknown entity, or the worked set not loaded yet: never flagged.
    expect(classifyDecode(dx('CQ XX9ZZ', null), undefined, undefined, undefined, worked)).toBe('cq');
    expect(classifyDecode(dx('CQ EA8AR IL18', 29), undefined, undefined, undefined, null)).toBe('cq');
  });

  it('returns normal for a directed decode with no grid', () => {
    const grids = new Set<string>();
    expect(classifyDecode(row('GJ0KYZ RK9AX -12'), undefined, grids)).toBe('normal');
  });
});
