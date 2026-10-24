# Full guide: running everything

A hands-on guide to running every part of Nova Commerce on a development machine: the backend stack, the shop and admin console, the AI assistant, Storybook, tests, monitoring, and a public demo link. The other documents explain *why* things are built the way they are; this one only says *how to run them*. Commands are written for the setup this project is developed on (Windows, Docker inside WSL Ubuntu); on Linux or macOS drop the `wsl.exe` wrapper.

## 1. What is where

| Repository | Local path | Contents |
|---|---|---|
| Backend `akh65267-maker/Nova` (folder still named `TaskManager`) | `D:\source\repos\TaskManager` | .NET 10 microservices, gateway, `deploy/docker-compose.yml`, tests, load tests, Azure templates (`infra/`), this guide |
| Frontend `akh65267-maker/Nova-web` (folder still named `taskmanager-web`) | `D:\source\repos\taskmanager-web` | npm monorepo: `apps/web` (shop), `apps/admin` (admin console), `packages/*` (shared UI, API client, auth, session) |

### Ports

| Port | What | Started by |
|---|---|---|
| 3100 | Shop (dev) | `npm run dev` in the frontend |
| 3200 | Admin console (dev) | `npm run dev:admin` in the frontend |
| 3300 | Shop (production build, for demos) | `npx next start -p 3300` (section 9) |
| 6006 | Storybook | `npm run storybook` in `apps/web` |
| 8000 | API gateway: the only entry point the apps use | compose |
| 8080–8086 | Services directly: tasks, users, catalog, inventory, basket, order, assistant | compose |
| 5432–5436 | Postgres: tasks, users, catalog, inventory, order | compose |
| 5672 / 15672 | RabbitMQ / its management UI | compose |
| 6379 | Redis (basket) | compose |
| 16686 | Jaeger (traces) | compose |
| 9090 | Prometheus (metrics) | compose |
| 3000 | Grafana (dashboards) | compose |
| 5050 / 5540 | pgAdmin / RedisInsight | compose, `--profile tools` |
| 11434 (inside Docker only) | Ollama (local AI model) | compose, `--profile ai` |

## 2. Prerequisites

- **WSL with Ubuntu, and Docker Engine inside it.** Docker runs only in WSL here, so every `docker` command goes through `wsl.exe -d Ubuntu -- bash -lc "..."`.
- **.NET SDK 10** (for builds and backend tests; not needed just to run the stack, which builds in Docker).
- **Node.js 24** and npm (the frontend; CI uses Node 24).
- **Git.**
- Optional: **cloudflared** for demo links (already at `D:\tools\cloudflared\cloudflared.exe` on this machine).

**Keep WSL awake.** WSL shuts its virtual machine down, containers included, when no WSL session is open. Before working, leave this running in a terminal of its own:

```bash
wsl.exe -d Ubuntu -- sleep infinity
```

If the shop sits on grey loading placeholders after a break, this is the first thing to check (section 13).

## 3. Backend: first-time setup

### 3.1 Secrets

```bash
cd D:/source/repos/TaskManager/deploy
cp .env.example .env
```

Fill in `.env`: `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD`, `JWT_KEY` (at least 32 bytes), `ADMIN_PASSWORD`, `GRAFANA_ADMIN_PASSWORD` and, if you use the tools profile, `PGADMIN_PASSWORD`. `openssl rand -base64 24` makes good values. `.env` is git-ignored; never commit it. `ADMIN_EMAIL` / `ADMIN_PASSWORD` become the admin account, created by the user service at startup.

### 3.2 Databases, then migrations, then services

No service creates its tables at startup, and the user service creates the admin at startup, so the order matters: **databases → migrations → services**.

```bash
wsl.exe -d Ubuntu -- bash -lc "cd /mnt/d/source/repos/TaskManager/deploy && docker compose up -d --wait postgres postgres-users postgres-catalog postgres-inventory postgres-order rabbitmq redis"
```

Apply the migrations with the migration image (the same one the Azure job and the e2e stack use):

```bash
wsl.exe -d Ubuntu -- bash -lc '
cd /mnt/d/source/repos/TaskManager/deploy
network=$(docker compose ps -q postgres-order | xargs docker inspect -f "{{range \$name, \$n := .NetworkSettings.Networks}}{{\$name}}{{end}}")
password=$(grep -E "^POSTGRES_PASSWORD=" .env | cut -d= -f2-)
docker build -q -f ../infra/images/migrations/Dockerfile -t taskflow-migrations ..
docker run --rm --network "$network" \
  -e "ConnectionStrings__UserDatabase=Host=postgres-users;Port=5432;Database=userflow;Username=postgres;Password=$password" \
  -e "ConnectionStrings__CatalogDatabase=Host=postgres-catalog;Port=5432;Database=catalogflow;Username=postgres;Password=$password" \
  -e "ConnectionStrings__InventoryDatabase=Host=postgres-inventory;Port=5432;Database=inventoryflow;Username=postgres;Password=$password" \
  -e "ConnectionStrings__OrderDatabase=Host=postgres-order;Port=5432;Database=orderflow;Username=postgres;Password=$password" \
  taskflow-migrations'
```

Run this again whenever `main` brings new migrations (a service failing with "relation ... does not exist" or a missing column means you forgot). Then start everything:

```bash
wsl.exe -d Ubuntu -- bash -lc "cd /mnt/d/source/repos/TaskManager/deploy && docker compose up -d --build"
```

Check: `curl http://localhost:8000/categories` answers `200` with a JSON list.

### 3.3 Fill the catalog

An empty shop shows nothing useful. The seed script adds about 190 realistic products from DummyJSON (a free test-data service) with photos, galleries, specifications, stock and some discounts, through the normal admin API. Running it again skips products that already exist.

```bash
cd D:/source/repos/TaskManager
node tools/seed-catalog/seed-catalog.mjs              # reads ADMIN_EMAIL / ADMIN_PASSWORD from deploy/.env
LIMIT=10 node tools/seed-catalog/seed-catalog.mjs     # a quick partial run
```

For local and demo use only: the data carries real brand names and photos.

### 3.4 A throwaway stack instead

`deploy/scripts/e2e-stack.sh up` builds a complete disposable stack (random secrets, migrations, one seeded product) and `down` deletes it with its data. It refuses to run where `deploy/.env` already exists, so it is meant for CI or a clean checkout, not your everyday stack.

## 4. Backend: everyday use

```bash
# start (or pick up rebuilt images after pulling main)
wsl.exe -d Ubuntu -- bash -lc "cd /mnt/d/source/repos/TaskManager/deploy && docker compose up -d --build"

# rebuild a few services only
wsl.exe -d Ubuntu -- bash -lc "cd /mnt/d/source/repos/TaskManager/deploy && docker compose up -d --build catalog-api order-api gateway"

# status and logs
wsl.exe -d Ubuntu -- bash -lc "cd /mnt/d/source/repos/TaskManager/deploy && docker compose ps"
wsl.exe -d Ubuntu -- bash -lc "cd /mnt/d/source/repos/TaskManager/deploy && docker compose logs -f --tail 100 order-api"

# stop (data is kept in volumes)
wsl.exe -d Ubuntu -- bash -lc "cd /mnt/d/source/repos/TaskManager/deploy && docker compose down"
```

**After pulling backend changes, rebuild.** A container keeps running the code it was built from. An endpoint that returns 404 in the shop although it exists on `main` usually means a stale container. The gateway's routes are baked into its image, so a new route also needs `--build gateway`.

`docker compose down -v` also deletes every database, so you would start again at section 3.2.

## 5. Frontend: shop and admin console

First time:

```bash
cd D:/source/repos/taskmanager-web
npm ci
cp apps/web/.env.local.example apps/web/.env.local
cp apps/admin/.env.local.example apps/admin/.env.local
```

The defaults point at the gateway on `localhost:8000`, which is what the compose stack exposes.

Run:

```bash
npm run dev          # shop on http://localhost:3100
npm run dev:admin    # admin console on http://localhost:3200
```

- **Use port 3100 for the shop.** The gateway's CORS allows that origin, which the end-to-end tests rely on.
- **Sign in to the admin console** with `ADMIN_EMAIL` / `ADMIN_PASSWORD` from `deploy/.env`. Only an Admin account can sign in there. The two apps keep separate sessions.
- **After pulling frontend changes,** run `npm ci` again if `package-lock.json` changed, and hard-refresh the browser (Ctrl+Shift+R). If the site still looks old, check which branch the folder is on (`git status`): the dev server shows whatever is checked out.

Other commands, from the frontend root:

```bash
npm run lint         # every workspace
npm run test:unit    # unit tests (packages/session)
npm run build        # production build of every app
```

## 6. The AI shop assistant

The assistant is off by default (`ASSISTANT_ENABLED=false` returns 503). Choose a model:

**Local, free, slow (Ollama).** In `deploy/.env` set `ASSISTANT_ENABLED=true`, then:

```bash
wsl.exe -d Ubuntu -- bash -lc "cd /mnt/d/source/repos/TaskManager/deploy && docker compose --profile ai up -d --build"
wsl.exe -d Ubuntu -- bash -lc "docker exec taskflow-ollama ollama pull qwen2.5:7b"     # once, about 4.7 GB
```

On a CPU the first answer takes about a minute while the model loads; `ASSISTANT_MODEL` picks another model.

**Hosted, paid per use, fast (Anthropic).** In `deploy/.env`:

```bash
ASSISTANT_ENABLED=true
ASSISTANT_PROVIDER=anthropic
ANTHROPIC_API_KEY=<your key>
ASSISTANT_ANTHROPIC_MODEL=claude-haiku-5-5     # the default; any current Claude model id works
```

then `docker compose up -d --build assistant-api`. No `ai` profile is needed for this one.

**Use it:** sign in to the shop and open the round chat button at the bottom right. It can search the catalog (in-stock products only), see your cart, and look up your own orders. How it works, and how to build one elsewhere: [Ai-guide.md](Ai-guide.md).

## 7. Storybook

The shop's components with mock data. It needs neither the backend nor a sign-in.

```bash
cd D:/source/repos/taskmanager-web/apps/web
npm run storybook          # http://localhost:6006
npm run build-storybook    # a static site in apps/web/storybook-static/
```

- **What's in it:** product cards in every state, prices, the hero, the promo tiles, the product rows, placeholders and the gallery. "Home / Sections / Home page" assembles the whole home page.
- **Theme:** a toolbar button switches between light and dark.
- **Sharing it:** the folder `npm run build-storybook` creates is plain HTML and JS. Host it anywhere (Netlify, Vercel, GitHub Pages, Chromatic) or zip it and send it; it contains no source code.
- **Mock data:** it lives in `apps/web/src/stories/` (`products.json`, `mock-data.ts`). A new story goes next to them as `*.stories.tsx`.

## 8. Tests

### Backend

```bash
cd D:/source/repos/TaskManager
dotnet test NovaCommerce.slnx                      # every unit test, no Docker needed
dotnet test tests/Saga.IntegrationTests            # the checkout saga against real Postgres/RabbitMQ (needs Docker)
```

### Frontend end-to-end (Playwright)

The backend stack must be running and migrated (section 3). The tests register a fresh user per run, so no stored credentials are needed.

```bash
cd D:/source/repos/taskmanager-web
npx playwright install chromium    # once
npm run test:e2e                   # shop then admin; starts the dev servers itself if they are not running
```

The report lands in `apps/<app>/playwright-report/`.

### Load tests (k6, in Docker)

```bash
cd D:/source/repos/TaskManager
loadtests/run.sh load      # also: stress, spike, soak, chaos
```

The profiles, what each one measures and how results are checked: [loadtests/README.md](../loadtests/README.md).

### CI

Both repositories run CI on every pull request. The backend runs build, unit tests and the saga tests. The frontend runs lint, unit tests and build, then end-to-end tests against a throwaway stack checked out from the backend repo.

## 9. A public demo link (Cloudflare quick tunnel)

Lets anyone open the shop running on this PC, with no account and no deployment.

```bash
# 1. a production build of the shop (faster, no dev overlay), on its own port
cd D:/source/repos/taskmanager-web/apps/web
npm run build
npx next start -p 3300

# 2. in a second terminal: the tunnel
D:/tools/cloudflared/cloudflared.exe tunnel --url http://localhost:3300
```

The tunnel prints `https://<random-words>.trycloudflare.com`; share that.

- **It runs only while you do:** the link works only while both commands and the Docker stack are running. It changes every time the tunnel starts; a permanent address needs a free Cloudflare account and a named tunnel.
- **Anyone with the link** can register, order and use the assistant against your local data. Share it with your audience only.
- **After code changes,** rebuild (`npm run build`) and restart step 1. The tunnel and its link can keep running.

## 10. Monitoring and admin tools

| Open | For | Sign in with |
|---|---|---|
| http://localhost:3000 | Grafana dashboards | `admin` / `GRAFANA_ADMIN_PASSWORD` |
| http://localhost:9090 | Prometheus metrics and alerts | none |
| http://localhost:16686 | Jaeger: one request followed across services | none |
| http://localhost:15672 | RabbitMQ queues and messages | `RABBITMQ_USER` / `RABBITMQ_PASSWORD` |
| http://localhost:5050 | pgAdmin (`--profile tools`) | `PGADMIN_EMAIL` / `PGADMIN_PASSWORD` |
| http://localhost:5540 | RedisInsight (`--profile tools`) | none |

```bash
wsl.exe -d Ubuntu -- bash -lc "cd /mnt/d/source/repos/TaskManager/deploy && docker compose --profile tools up -d"
```

**Scripts for incidents** (in `deploy/scripts/`):
- `diagnose-order.sh` traces one order through the services.
- `replay-errors.sh` replays failed messages.

What is measured and what alerts exist: [observability.md](observability.md).

## 11. Azure

Templates in `infra/` deploy the stack to Azure Container Apps with `infra/scripts/deploy-azure.sh`. Nothing is deployed today. Mapping, settings, costs and known limits are in [azure-deployment.md](azure-deployment.md). Read it before running anything there.

## 12. Git workflow

- `main` is protected by habit: work on a branch, open a pull request, and merge when CI is green.
- Commit messages end with the `Co-Authored-By` trailer when an AI assistant co-wrote the change.
- Files use CRLF line endings on this machine; Git normalises them.
- Merged branches are deleted from GitHub's branches page by the owner.

## 13. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Shop stuck on grey loading placeholders | Backend not answering yet: WSL went to sleep, or containers are still starting | Keep WSL awake (section 2), check `docker compose ps`, then refresh |
| A page 404s on an API call that exists on `main` | Stale container | `docker compose up -d --build <service> gateway` |
| "relation ... does not exist" in a service log | Migrations not applied | Section 3.2, migrations step |
| Can't sign in to the admin console | Not an Admin account, or a changed `ADMIN_PASSWORD` | The admin is created once, from `deploy/.env` at the first start; an existing account keeps its password |
| Shop changes don't show | Old branch checked out, or browser cache | `git status` / `git pull` in `taskmanager-web`, then Ctrl+Shift+R |
| `EADDRINUSE` when starting a server | Another app on that port (3200 is the admin console) | Use another port, or stop the other server |
| Assistant says "not switched on" | `ASSISTANT_ENABLED` is false | Section 6 |
| Assistant times out on first question | Local model still loading | Wait, or use a hosted model |

## 14. More documentation

- **Backend:** [docs/README.md](README.md) indexes architecture, services, messaging, the order saga, authentication, databases, observability and decisions.
- **Frontend:** `docs/frontend.md` in `taskmanager-web` covers the apps' structure, sessions, testing and the web-vitals budget.
