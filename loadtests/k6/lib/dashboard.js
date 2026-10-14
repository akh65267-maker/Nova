import http from 'k6/http';
import { sleep } from 'k6';
import { Trend } from 'k6/metrics';
import { BASE_URL } from './config.js';

// An administrator with the dashboard open: every few seconds, the figures it shows.
// DASHBOARD_VUS of these run beside the shoppers, to answer one question: do the admin
// analytics queries slow the checkout path? (Compare a run with DASHBOARD_VUS=0.)
//
// Each request is tagged name:admin_<endpoint>, so it never counts towards the shop's
// thresholds (name:products_list and so on), but a failure still counts in http_req_failed.
export const adminAnalytics = new Trend('admin_analytics_ms', true);

const DAY_MS = 24 * 60 * 60 * 1000;

function range(days) {
  const now = new Date();
  const to = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()) + DAY_MS;
  return `from=${new Date(to - days * DAY_MS).toISOString()}&to=${new Date(to).toISOString()}`;
}

export function dashboardVisit(data) {
  if (!data.admin) throw new Error('DASHBOARD_VUS needs ADMIN_EMAIL and ADMIN_PASSWORD');
  const headers = { Authorization: `Bearer ${data.admin.token}` };
  const ninety = range(90);
  const thirty = range(30);

  const requests = [
    ['admin_summary', `/orders/admin/summary?${ninety}`],
    ['admin_timeseries', `/orders/admin/timeseries?${thirty}`],
    ['admin_cancellations', `/orders/admin/cancellations?${ninety}`],
    ['admin_top_products', `/orders/admin/top-products?${ninety}&by=revenue&limit=10`],
    ['admin_pending', '/orders/admin/pending'],
    ['admin_orders', `/orders/admin/orders?${thirty}&page=1&pageSize=25`],
  ];

  for (const [name, path] of requests) {
    const res = http.get(`${BASE_URL}${path}`, { headers, tags: { name } });
    adminAnalytics.add(res.timings.duration, { endpoint: name });
  }

  sleep(2);
}
