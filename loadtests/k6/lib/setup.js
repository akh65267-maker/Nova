import http from 'k6/http';
import { sleep } from 'k6';
import {
  ADMIN_EMAIL,
  ADMIN_PASSWORD,
  DASHBOARD_VUS,
  BASE_URL,
  PRICE,
  PRODUCTS,
  RUN_ID,
  STOCK,
  USERS,
} from './config.js';

const JSON_HEADERS = { 'Content-Type': 'application/json' };

// Refuses to point a test at anything that is not the local stack unless asked
// to. A load test is a denial-of-service against whatever it targets; the safe
// default has to be "only my own machine".
function guardTarget() {
  const host = BASE_URL.replace(/^https?:\/\//, '').split(/[:/]/)[0];
  const local = ['gateway', 'localhost', '127.0.0.1', 'host.docker.internal'];
  if (!local.includes(host) && __ENV.ALLOW_REMOTE !== 'true') {
    throw new Error(
      `Refusing to run against "${host}". This is not the local stack. ` +
        'Set ALLOW_REMOTE=true only for an environment you own and are ready to overload.',
    );
  }
}

function postJson(path, body, token, name) {
  const headers = token ? { ...JSON_HEADERS, Authorization: `Bearer ${token}` } : JSON_HEADERS;
  return http.post(`${BASE_URL}${path}`, JSON.stringify(body), { headers, tags: { name } });
}

// Login is limited to 10/minute/IP, so retry rather than fail when the limit is
// hit - which happens legitimately when runs follow each other closely.
export function login(email, password) {
  for (let attempt = 1; attempt <= 8; attempt++) {
    const res = postJson('/users/login', { email, password }, null, 'setup_login');
    if (res.status === 200) return { token: res.json('token'), expiresAtUtc: res.json('expiresAtUtc') };
    if (res.status === 429) {
      sleep(10);
      continue;
    }
    throw new Error(`login for ${email} failed: ${res.status} ${res.body}`);
  }
  throw new Error(`login for ${email} still rate limited after 8 attempts`);
}

function createProducts() {
  const admin = login(ADMIN_EMAIL, ADMIN_PASSWORD);
  const products = [];

  for (let i = 1; i <= PRODUCTS; i++) {
    const created = postJson(
      '/products',
      {
        name: `loadtest-${RUN_ID}-${i}`,
        description: 'Created by the k6 load tests; safe to delete (loadtests/scripts/cleanup.sh).',
        price: PRICE,
        category: 'LoadTest',
      },
      admin.token,
      'setup_create_product',
    );
    if (created.status !== 201) throw new Error(`creating product ${i} failed: ${created.status} ${created.body}`);
    const productId = created.json('id');

    const stock = postJson('/inventory', { productId, quantityAvailable: STOCK }, admin.token, 'setup_create_stock');
    if (stock.status !== 201) throw new Error(`creating stock for product ${i} failed: ${stock.status} ${stock.body}`);

    products.push({ productId, price: PRICE, initialStock: STOCK });
  }
  return products;
}

// Without admin credentials: the best-stocked products that already exist. Loudly
// second best, because a long run can drain them.
function pickExistingProducts() {
  const list = http.get(`${BASE_URL}/products?pageSize=100`, { tags: { name: 'setup_list' } }).json('items');
  const inventory = http.get(`${BASE_URL}/inventory`, { tags: { name: 'setup_inventory' } }).json();
  const stock = new Map(inventory.map((i) => [i.productId, i.quantityAvailable]));

  const candidates = list.filter((p) => p.price > 0 && (stock.get(p.id) ?? 0) > 0);
  if (candidates.length === 0) {
    throw new Error('no purchasable product exists; set ADMIN_EMAIL/ADMIN_PASSWORD so some can be created');
  }
  candidates.sort((a, b) => stock.get(b.id) - stock.get(a.id));
  const best = candidates.slice(0, PRODUCTS);
  console.warn(
    `WARNING: no admin credentials, using ${best.length} existing product(s). ` +
      'A long or heavy run can exhaust them and start measuring the out-of-stock path instead.',
  );
  return best.map((p) => ({ productId: p.id, price: p.price, initialStock: stock.get(p.id) }));
}

export function prepare() {
  guardTarget();

  const products = ADMIN_EMAIL && ADMIN_PASSWORD ? createProducts() : pickExistingProducts();

  const users = [];
  for (let i = 1; i <= USERS; i++) {
    const email = `loadtest+${RUN_ID}-${i}@example.com`;
    const password = `Lt-${RUN_ID}-${i}`;
    const register = postJson('/users', { email, displayName: `Load ${i}`, password }, null, 'setup_register');
    if (register.status !== 201 && register.status !== 200) {
      throw new Error(`registering ${email} failed: ${register.status} ${register.body}`);
    }
    const session = login(email, password);
    users.push({ email, password, token: session.token, issuedAt: Date.now() });
  }

  // Machine-readable, one per product: run.sh reads these to know which products to
  // check the stock-conservation invariant against, and what each started at.
  for (const product of products) {
    console.log(`LOADTEST_PRODUCT ${product.productId} ${product.initialStock}`);
  }

  // Only when asked for: one session shared by the dashboard VUs (login is rate limited).
  const admin = DASHBOARD_VUS > 0 ? login(ADMIN_EMAIL, ADMIN_PASSWORD) : null;

  return { products, users, admin };
}
