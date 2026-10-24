#!/usr/bin/env node
// Fills a local catalog with about 190 realistic products from DummyJSON (https://dummyjson.com), a free
// test-data service: name, description, brand, price, attributes, stock, a main picture and a gallery.
// Everything goes through the gateway's admin API, as if an administrator had typed it in, so the
// catalog, inventory and their events stay consistent. Running it again skips products already there.
//
//   node tools/seed-catalog/seed-catalog.mjs            # reads ADMIN_EMAIL / ADMIN_PASSWORD from deploy/.env
//   GATEWAY_URL=http://localhost:8000 LIMIT=20 node tools/seed-catalog/seed-catalog.mjs
//
// For local and demo stacks only: the data carries real brand names and product photos.

import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const GATEWAY = process.env.GATEWAY_URL ?? "http://localhost:8000";
const SOURCE = "https://dummyjson.com/products?limit=0";
const LIMIT = Number(process.env.LIMIT ?? Infinity);
const CONCURRENCY = 4;
const MAX_IMAGE_BYTES = 2 * 1024 * 1024; // the catalog's own limit
const MAX_GALLERY = 4; // the catalog allows 8; four keeps the import quick

// DummyJSON's categories folded into the shop's own where they fit; the rest become new categories.
const CATEGORY = {
  smartphones: "Electronics",
  laptops: "Electronics",
  tablets: "Electronics",
  "mobile-accessories": "Electronics",
  "mens-shirts": "Fashion",
  "mens-shoes": "Fashion",
  "mens-watches": "Fashion",
  tops: "Fashion",
  "womens-dresses": "Fashion",
  "womens-shoes": "Fashion",
  "womens-bags": "Fashion",
  "womens-jewellery": "Fashion",
  "womens-watches": "Fashion",
  sunglasses: "Fashion",
  furniture: "Home & Kitchen",
  "home-decoration": "Home & Kitchen",
  "kitchen-accessories": "Home & Kitchen",
  "sports-accessories": "Sports & Outdoors",
  beauty: "Beauty",
  fragrances: "Beauty",
  "skin-care": "Beauty",
  groceries: "Groceries",
  motorcycle: "Vehicles",
  vehicle: "Vehicles",
};

// "mens-shirts" → "Mens shirts": the source category, kept as the product's Type.
const typeName = (slug) => slug.replace(/-/g, " ").replace(/^./, (c) => c.toUpperCase());

function adminCredentials() {
  if (process.env.ADMIN_EMAIL && process.env.ADMIN_PASSWORD) return { email: process.env.ADMIN_EMAIL, password: process.env.ADMIN_PASSWORD };
  const envFile = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "deploy", ".env");
  const values = Object.fromEntries(
    readFileSync(envFile, "utf8")
      .split(/\r?\n/)
      .filter((line) => line.includes("=") && !line.startsWith("#"))
      .map((line) => [line.slice(0, line.indexOf("=")), line.slice(line.indexOf("=") + 1)]),
  );
  if (!values.ADMIN_EMAIL || !values.ADMIN_PASSWORD) throw new Error(`ADMIN_EMAIL and ADMIN_PASSWORD are not set, and not in ${envFile}`);
  return { email: values.ADMIN_EMAIL, password: values.ADMIN_PASSWORD };
}

let token;
async function signIn() {
  const response = await fetch(`${GATEWAY}/users/login`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(adminCredentials()) });
  if (!response.ok) throw new Error(`Admin sign-in failed: ${response.status}`);
  const body = await response.json();
  token = body.token ?? body.accessToken;
}

// An admin call; signs in again once if the token has expired during a long import.
async function api(method, path, body, contentType = "application/json") {
  for (let attempt = 0; ; attempt++) {
    const response = await fetch(`${GATEWAY}${path}`, {
      method,
      headers: { Authorization: `Bearer ${token}`, ...(body !== undefined && { "Content-Type": contentType }) },
      body: body === undefined ? undefined : contentType === "application/json" ? JSON.stringify(body) : body,
    });
    if (response.status === 401 && attempt === 0) {
      await signIn();
      continue;
    }
    return response;
  }
}

async function existingNames() {
  const names = new Set();
  for (let page = 1; ; page++) {
    const response = await fetch(`${GATEWAY}/products?page=${page}&pageSize=100`);
    if (!response.ok) throw new Error(`Listing products failed: ${response.status}`);
    const { items, totalCount } = await response.json();
    for (const item of items) names.add(item.name.toLowerCase());
    if (page * 100 >= totalCount || items.length === 0) return names;
  }
}

async function ensureCategories(wanted) {
  const have = new Set((await (await fetch(`${GATEWAY}/categories`)).json()).map((c) => c.name));
  for (const name of wanted) {
    if (have.has(name)) continue;
    const response = await api("POST", "/categories", { name });
    if (!response.ok && response.status !== 409) throw new Error(`Creating category ${name} failed: ${response.status}`);
    console.log(`+ category ${name}`);
  }
}

async function download(url) {
  const response = await fetch(url);
  if (!response.ok) return null;
  const bytes = new Uint8Array(await response.arrayBuffer());
  return bytes.length > 0 && bytes.length <= MAX_IMAGE_BYTES ? { bytes, type: response.headers.get("content-type") ?? "image/webp" } : null;
}

async function importProduct(source) {
  const attributes = [
    { name: "Type", value: typeName(source.category) },
    source.sku && { name: "SKU", value: source.sku },
    source.weight && { name: "Weight", value: `${source.weight} kg` },
    source.dimensions && { name: "Dimensions", value: `${source.dimensions.width} × ${source.dimensions.height} × ${source.dimensions.depth} cm` },
    source.warrantyInformation && { name: "Warranty", value: source.warrantyInformation },
    source.shippingInformation && { name: "Shipping", value: source.shippingInformation },
    source.returnPolicy && { name: "Returns", value: source.returnPolicy },
  ].filter(Boolean);

  const created = await api("POST", "/products", {
    name: source.title,
    description: source.description,
    price: source.price,
    category: CATEGORY[source.category] ?? typeName(source.category),
    brand: source.brand ?? null,
    attributes,
  });
  if (!created.ok) throw new Error(`create ${created.status} ${await created.text()}`);
  const { id } = await created.json();

  // The main picture is the first full-size one; the rest go to the gallery.
  const [main, ...rest] = source.images ?? [];
  const mainImage = main && (await download(main));
  if (mainImage) await api("PUT", `/products/${id}/image`, mainImage.bytes, mainImage.type);
  for (const url of rest.slice(0, MAX_GALLERY)) {
    const image = await download(url);
    if (image) await api("POST", `/products/${id}/gallery`, image.bytes, image.type);
  }

  // Stock as the source has it; zero stays zero, so some products show as out of stock.
  const stock = await api("POST", "/inventory", { productId: id, quantityAvailable: Math.max(0, Math.round(source.stock ?? 0)) });
  if (!stock.ok && stock.status !== 409) console.warn(`  stock for ${source.title}: ${stock.status}`);

  // A real offer only where the source has a deep one, so Best offers is a short list.
  if (source.discountPercentage >= 15) {
    await api("PUT", `/products/${id}/discount`, { kind: "Percent", value: Math.round(source.discountPercentage), startsAtUtc: null, endsAtUtc: null });
  }
}

async function main() {
  await signIn();
  const { products } = await (await fetch(SOURCE)).json();
  const known = await existingNames();
  const todo = products.filter((p) => !known.has(p.title.toLowerCase())).slice(0, LIMIT);
  console.log(`${products.length} in the source, ${products.length - todo.length} already here or skipped, ${todo.length} to import.`);

  await ensureCategories(new Set(todo.map((p) => CATEGORY[p.category] ?? typeName(p.category))));

  let done = 0;
  let failed = 0;
  const queue = [...todo];
  await Promise.all(
    Array.from({ length: CONCURRENCY }, async () => {
      for (let source = queue.shift(); source; source = queue.shift()) {
        try {
          await importProduct(source);
          done++;
          if (done % 10 === 0) console.log(`  ${done}/${todo.length}`);
        } catch (error) {
          failed++;
          console.warn(`! ${source.title}: ${error.message}`);
        }
      }
    }),
  );
  console.log(`Imported ${done}, failed ${failed}.`);
  if (failed > 0) process.exitCode = 1;
}

main().catch((error) => {
  console.error(error.message);
  process.exit(1);
});
