import { RESULT_NAME } from './config.js';

// k6's end-of-test summary, formatted for the questions these tests ask. k6 changed
// the shape of the summary object between major versions, so values are read
// defensively: either { values: {...} } or the values directly.

const valuesOf = (metric) => (metric ? metric.values || metric : {});

function metric(data, name) {
  return valuesOf(data.metrics[name]);
}

const ms = (v) => (v === undefined ? '   n/a' : `${v.toFixed(0).padStart(5)}ms`);
const pct = (v) => (v === undefined ? '  n/a' : `${(v * 100).toFixed(2).padStart(5)}%`);

function phaseRow(data, phase) {
  const duration = metric(data, `http_req_duration{phase:${phase}}`);
  const failed = metric(data, `http_req_failed{phase:${phase}}`);
  const reqs = metric(data, `http_reqs{phase:${phase}}`);
  const confirmed = metric(data, `checkout_confirmed{phase:${phase}}`);
  return {
    phase,
    requests: reqs.count,
    p50: duration.med,
    p95: duration['p(95)'],
    p99: duration['p(99)'],
    failed: failed.rate,
    confirmed: confirmed.rate,
  };
}

export function phaseTable(data, phases) {
  const rows = phases.map((p) => phaseRow(data, p.name));
  const lines = ['', 'By phase', `  ${'phase'.padEnd(14)} ${'requests'.padStart(8)} ${'p50'.padStart(7)} ${'p95'.padStart(7)} ${'p99'.padStart(7)} ${'failed'.padStart(7)} ${'checkouts ok'.padStart(13)}`];
  for (const r of rows) {
    lines.push(
      `  ${r.phase.padEnd(14)} ${String(r.requests ?? 0).padStart(8)} ${ms(r.p50)} ${ms(r.p95)} ${ms(r.p99)} ${pct(r.failed)} ${pct(r.confirmed).padStart(13)}`,
    );
  }
  const straddle = metric(data, 'checkout_confirmed{phase:straddle}');
  const crossed = (straddle.passes ?? 0) + (straddle.fails ?? 0);
  if (crossed > 0) {
    lines.push(
      `  (${crossed} checkout(s) began in one phase and ended in another; ` +
        `${straddle.passes ?? 0} confirmed. They are counted in no phase above, so a fault cannot be blamed on the baseline.)`,
    );
  }
  return { lines, rows };
}

export function overview(data) {
  const reqs = metric(data, 'http_reqs');
  const duration = metric(data, 'http_req_duration');
  const failed = metric(data, 'http_req_failed');
  const resolve = metric(data, 'checkout_resolve_seconds');
  const confirmed = metric(data, 'checkout_confirmed');
  const dropped = metric(data, 'dropped_iterations');
  const iterations = metric(data, 'iterations');
  const outcomes = ['Confirmed', 'Cancelled', 'Pending']
    .map((s) => `${s} ${metric(data, `checkout_outcome{status:${s}}`).count ?? 0}`)
    .join(', ');

  const analytics = metric(data, 'admin_analytics_ms');

  return [
    `  iterations        ${iterations.count ?? 0}${dropped.count ? `   (${dropped.count} DROPPED: the generator could not keep up or the system stopped accepting)` : ''}`,
    `  http requests     ${reqs.count ?? 0}   (${(reqs.rate ?? 0).toFixed(1)}/s)`,
    `  failed requests   ${pct(failed.rate)}`,
    `  latency           p50 ${ms(duration.med)}   p95 ${ms(duration['p(95)'])}   p99 ${ms(duration['p(99)'])}   max ${ms(duration.max)}`,
    `  checkout resolve  p50 ${ms(resolve.med)}   p95 ${ms(resolve['p(95)'])}   max ${ms(resolve.max)}   confirmed ${pct(confirmed.rate)}`,
    `  checkout outcomes ${outcomes}`,
    ...(analytics.med !== undefined ? [`  admin analytics   p50 ${ms(analytics.med)}   p95 ${ms(analytics['p(95)'])}   max ${ms(analytics.max)}`] : []),
  ];
}

// The pass/fail criteria, one line each. The always-true thresholds that only exist so
// k6 records a per-phase sub-metric (see phases.js) are left out: they are not criteria.
function thresholdLines(data) {
  const observation = new Set(['max>=0', 'rate>=0', 'count>=0']);
  const lines = [];
  for (const [name, m] of Object.entries(data.metrics)) {
    for (const [expression, result] of Object.entries(m.thresholds || {})) {
      if (observation.has(expression)) continue;
      lines.push(`  ${result.ok ? 'PASS' : 'FAIL'}  ${name}  ${expression}`);
    }
  }
  // FAIL sorts before PASS, so a failing run leads with what failed.
  return lines.sort();
}

// Builds the value returned from handleSummary: text on stdout and the full data as
// JSON in the results directory that run.sh mounts.
export function report(data, { title, phases = [], extra = [] }) {
  const lines = ['', `=== ${title} ===`, ...overview(data)];
  if (phases.length > 0) lines.push(...phaseTable(data, phases).lines);

  const criteria = thresholdLines(data);
  if (criteria.length > 0) lines.push('', 'Thresholds', ...criteria);
  lines.push(...extra, '');

  // setup_data holds the throwaway users' passwords and session tokens. They are only
  // useful to the run that created them, so they are not written to disk.
  const { setup_data: _setupData, ...persisted } = data;

  return {
    stdout: lines.join('\n'),
    [`/results/${RESULT_NAME}.json`]: JSON.stringify(persisted, null, 2),
  };
}
