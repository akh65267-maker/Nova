// LOAD: can the system handle the traffic we expect?
//
// A steady, realistic number of concurrent shoppers for long enough to reach steady
// state. The pass criteria are the "healthy" thresholds; anything that fails here
// means the system cannot even carry expected load.
//
//   VUS   concurrent shoppers (default 20)   HOLD   steady period (default 3m)
//   RAMP  ramp-up and ramp-down (default 1m)
//   DASHBOARD_VUS  administrators polling the analytics endpoints meanwhile (default 0)
import exec from 'k6/execution';
import { DASHBOARD_VUS, TREND_STATS, num } from './lib/config.js';
import { dashboardVisit } from './lib/dashboard.js';
import { journey } from './lib/journeys.js';
import { observe } from './lib/phases.js';
import { report } from './lib/report.js';
import { prepare } from './lib/setup.js';
import { healthy, merge } from './lib/thresholds.js';

const VUS = num('VUS', 20);
const HOLD = __ENV.HOLD || '3m';
const RAMP = __ENV.RAMP || '1m';

export const options = {
  setupTimeout: '10m',
  summaryTrendStats: TREND_STATS,
  scenarios: {
    load: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: RAMP, target: VUS },
        { duration: HOLD, target: VUS },
        { duration: RAMP, target: 0 },
      ],
      gracefulRampDown: '30s',
    },
    ...(DASHBOARD_VUS > 0 && {
      dashboard: {
        executor: 'constant-vus',
        vus: DASHBOARD_VUS,
        duration: `${toSeconds(RAMP) * 2 + toSeconds(HOLD)}s`,
        exec: 'dashboard',
      },
    }),
  },
  thresholds: merge(healthy(), observe([]), DASHBOARD_VUS > 0 ? { admin_analytics_ms: ['max>=0'] } : {}),
};

// '3m' -> 180. Only the m and s suffixes the defaults use.
function toSeconds(duration) {
  const match = /^([0-9]+)(m|s)$/.exec(duration);
  if (!match) throw new Error(`HOLD and RAMP must look like 3m or 90s, got "${duration}"`);
  return Number(match[1]) * (match[2] === 'm' ? 60 : 1);
}

export function setup() {
  return prepare();
}

export default function (data) {
  if (exec.vu.iterationInScenario === 0 && exec.vu.idInTest === 1) {
    console.log(`LOADTEST_TRAFFIC_STARTED ${Date.now()}`);
  }
  journey(data);
}

export function dashboard(data) {
  dashboardVisit(data);
}

export function handleSummary(data) {
  return report(data, { title: `LOAD  (${VUS} VUs, ${HOLD} steady)` });
}
