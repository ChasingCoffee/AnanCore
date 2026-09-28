// SPDX-License-Identifier: GPL-2.0-or-later
//
// Two-tone IMD readout text. The server reports each product either as a
// measurement or, when the product is below what the feedback display can
// measure (typical once PureSignal has converged), as an UPPER BOUND — the
// true IMD is at least that good. A bound is shown as "< −59.0", never as a
// bare number: the old analyser reported noise peaks there as if they were
// products, reading worse than the amplifier really was.

export function formatImdValue(dbc: number, isBound: boolean): string {
  if (!Number.isFinite(dbc)) return '—';
  const v = dbc.toFixed(1);
  return isBound ? `< ${v}` : v;
}

export function formatImdDbc(dbc: number, isBound: boolean): string {
  if (!Number.isFinite(dbc)) return '—';
  return `${formatImdValue(dbc, isBound)} dBc`;
}

export const IMD_BOUND_TITLE =
  'Below what the feedback display can measure: the products are at or under the noise, ' +
  'so the true IMD is at least this good.';
