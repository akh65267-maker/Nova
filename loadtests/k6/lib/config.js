// Configuration for every load-test scenario. Everything is an environment
// variable so the same scripts run against the local docker-compose stack today
// and a deployed environment later; nothing here is a secret.

export function num(name, fallback) {
  const raw = __ENV[name];
  if (raw === undefined || raw === '') return fallback;
  const value = Number(raw);
  if (!Number.isFinite(value)) throw new Error(`${name} must be a number, got "${raw}"`);
  return value;
}

// The gateway's name on the docker-compose network. run.sh joins the k6
// container to that network, so this needs no published port and no
// host-networking quirks between Docker Desktop and native Docker.
export const BASE_URL = (__ENV.BASE_URL || 'http://gateway:8080').replace(/\/+$/, '');

export const RUN_ID = __ENV.RUN_ID || String(Date.now());
export const RESULT_NAME = __ENV.RESULT_NAME || `k6-${RUN_ID}`;

// Users to log in once and share between VUs. POST /users/login is rate limited
// to 10 per minute per IP, so this cannot scale with VUs - and must not, since
// login is the one endpoint deliberately protected against being hammered.
export const USERS = num('USERS', 10);

// How many products the load is spread across. Real traffic is spread over a catalog;
// concentrating it on one product makes every ReserveStock fight over a single
// inventory row, which is a legitimate scenario (a flash sale) but a worst case, not
// the default. PRODUCTS=1 reproduces it on purpose.
export const PRODUCTS = num('PRODUCTS', 5);

// Stock for EACH dedicated load-test product. Checkout consumes real stock, so a
// run would otherwise exhaust a catalog product and turn into a test of the
// "insufficient stock" path.
export const STOCK = num('LOADTEST_STOCK', 1000000);
export const PRICE = 1;

// How long one simulated shopper waits for an order to leave Pending.
export const CHECKOUT_WAIT_S = num('CHECKOUT_WAIT_S', 30);

// Required: creating the dedicated products (POST /products) and reading exact stock
// (GET /inventory) are Admin-only.
export const ADMIN_EMAIL = __ENV.ADMIN_EMAIL || '';
export const ADMIN_PASSWORD = __ENV.ADMIN_PASSWORD || '';

// Administrators with the dashboard open, beside the shoppers (default none). Used to
// measure whether the analytics queries slow checkouts; needs the admin credentials.
export const DASHBOARD_VUS = num('DASHBOARD_VUS', 0);

// Share of iterations spent browsing / on the basket / checking out. The rest of
// the 100% is checkout. A storefront is read-heavy; adjust to match real traffic.
export const MIX_BROWSE = num('MIX_BROWSE', 0.7);
export const MIX_BASKET = num('MIX_BASKET', 0.2);

// Thresholds a run must meet to pass. These are numbers for the local docker
// stack on a developer machine, NOT service-level objectives: replace them with
// targets derived from real traffic before using a run to make a capacity claim.
export const LIMITS = {
  browseP95: num('P95_BROWSE_MS', 500),
  detailP95: num('P95_DETAIL_MS', 300),
  orderCreateP95: num('P95_ORDER_CREATE_MS', 1000),
  checkoutP95: num('P95_CHECKOUT_MS', 3000),
  maxFailedRate: num('MAX_FAILED_RATE', 0.01),
  minConfirmedRate: num('MIN_CONFIRMED_RATE', 0.99),
};

export const TREND_STATS = ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'];
