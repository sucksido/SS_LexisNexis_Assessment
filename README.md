# Order Intake & Tracking

An internal tool for a sales team to record customer purchase orders and track their status.

The interesting requirement in the brief is this one:

> Sales reps sometimes resubmit the same order. The system must avoid creating duplicate orders when the same client-provided reference is submitted more than once, and behave consistently from a user's point of view.

Everything else here is competent CRUD. That sentence is the actual problem, and [SOLUTION.md](SOLUTION.md) explains how it is answered and what was deliberately left out.

- **Backend** — C# / ASP.NET Core 8 (LTS), clean layered architecture, EF Core 8, FluentValidation, xUnit
- **Frontend** — Angular 18, standalone components, signals, reactive forms, strict TypeScript 5.5
- **Docs** — [SOLUTION.md](SOLUTION.md) for the design, the trade-offs and the limitations

---

## Prerequisites

You need **one** of the following.

**Option A — run locally (recommended, gives you hot reload):**

| Tool | Version | Check | Get it |
| --- | --- | --- | --- |
| .NET SDK | 8.0 or later | `dotnet --version` | <https://dotnet.microsoft.com/download/dotnet/8.0> |
| Node.js | 20 LTS or later | `node --version` | <https://nodejs.org> |

The Angular CLI does not need to be installed globally; `npm start` uses the local copy.

**Option B — run in containers:** Docker Desktop (or Docker Engine 24+ with Compose v2). Nothing else.

A `NuGet.config` at the repository root pins package restore to nuget.org and clears any feeds inherited from your machine. If you have a private or corporate feed configured globally, restore would otherwise query it for every package and fail the whole build on a 401 — including for projects that reference no packages at all, since the vulnerability audit uses the same source list. Pinning it here means a clean clone builds the same way everywhere.

---

## Run it

### Option A — locally

Two terminals.

**Terminal 1 — API**

```bash
dotnet run --project src/OrderIntake.Api
```

Serves <http://localhost:5080>. The root redirects to Swagger at <http://localhost:5080/swagger>.

**Terminal 2 — web app**

```bash
cd web
npm install     # first time only
npm start
```

Serves <http://localhost:4200> and opens automatically.

`npm start` runs with `proxy.conf.json`, which forwards `/api` to `http://localhost:5080`. The browser therefore only ever talks to its own origin, so there is nothing to configure and no CORS to trip over.

The API seeds a few sample orders on first run so the list screen is not empty.

### Option B — containers

```bash
docker compose up --build
```

Then <http://localhost:4200> for the app and <http://localhost:5080/swagger> for the API.

The compose stack runs against SQLite with a named volume, so orders survive a restart. The image build also runs the backend test suite, so an image that starts is an image whose tests passed.

---

## Verify it

Run these before you believe anything above.

```bash
# Backend: builds clean (warnings are errors) and all tests pass.
dotnet build OrderIntake.sln --configuration Release
dotnet test  OrderIntake.sln --configuration Release

# Frontend: type-checks under strict mode, builds, and unit tests pass.
cd web
npm ci
npm run build
npm test
```

Expected: a green build with no warnings, and every test passing. The suite covers the domain rules, the fingerprint, the service, the EF queries and the concurrency gate — including a test that spins up 25 simultaneous identical submissions and asserts that exactly one order exists afterwards.

A quick manual smoke test, in order:

1. Open <http://localhost:4200>, click **New order**. The reference arrives from the server already filled in and read-only. Fill in the rest, submit. You land on the detail page with server-computed totals.
2. On that detail page, press **Submit again**. You land on the *same* order, with a banner saying it already existed. The list still shows one row — that is the duplicate guard doing its job.
3. The 409 case — same reference, *different* contents — cannot be produced from the UI now that the reference is server-issued, so drive it from Swagger: `POST /api/orders` twice with the same `externalReference` and the same customer email, changing a quantity on the second call. You get a 409 carrying the id of the order that already holds the reference.
4. On an order's detail page, use the status buttons. Try to jump straight from Pending to Fulfilled via Swagger — the API refuses and tells you what is allowed instead.

`src/OrderIntake.Api/OrderIntake.Api.http` scripts that whole sequence if you prefer to drive it from an editor (VS Code REST Client, Rider, or Visual Studio).

---

## Configuration

All of it lives in `src/OrderIntake.Api/appsettings.json` and can be overridden by environment variables using the standard `Section__Key` form.

| Key | Default | What it does |
| --- | --- | --- |
| `Storage:Provider` | `InMemory` | `InMemory` or `Sqlite`. |
| `Storage:SqliteConnectionString` | `Data Source=orderintake.db` | Used when the provider is `Sqlite`. |
| `Storage:SeedSampleData` | `true` | Writes a handful of example orders on first run. |
| `Cors:AllowedOrigins` | `http://localhost:4200` | Origins allowed to call the API directly. |

To run against SQLite locally:

```bash
dotnet run --project src/OrderIntake.Api --launch-profile sqlite
```

Worth knowing: **the EF Core in-memory provider accepts a unique index and then ignores it.** The in-memory default is convenient, but only the SQLite path proves the database-level duplicate guard actually holds. SOLUTION.md covers why the design does not depend on which one you pick.

---

## The API

| Method | Route | Notes |
| --- | --- | --- |
| `POST` | `/api/orders` | Idempotent on `externalReference`, scoped to the customer. `201` for a new order, `200` for a replay, `409` if the reference now means something different. |
| `GET` | `/api/orders` | Newest first. `page`, `pageSize` (max 100), `status`, `search`. |
| `GET` | `/api/orders/{id}` | Full order with lines and totals. |
| `PUT` | `/api/orders/{id}/status` | `200` on success, `409` with the legal alternatives attached if the move is not allowed. |
| `GET` | `/api/orders/statuses` | The status graph, so the UI never hard-codes the transition rules. |
| `GET` | `/api/orders/next-reference` | The next sequential reference (`PO-000001` upward) for the intake form. A suggestion, not a reservation — nothing is held, and the unique index stays the only guarantee. |
| `GET` | `/health` | Liveness. |

`POST /api/orders` still accepts any non-empty reference, so an integration or an import job can bring its own. The intake form simply chooses not to: it fetches one and shows it read-only, which is why submitting the same basket twice from the UI is a replay rather than two orders. SOLUTION.md §1 covers the reasoning and what it costs.

Errors are RFC 7807 ProblemDetails with a stable machine-readable `code`, so clients branch on the code and never on the prose.

---

## Repository layout

```
src/
  OrderIntake.Domain/          Entities, invariants, the status policy. No dependencies.
  OrderIntake.Application/     Use cases, contracts, validation, the fingerprint.
  OrderIntake.Infrastructure/  EF Core, repositories, the keyed idempotency gate, the reference sequence.
  OrderIntake.Api/             Controllers, ProblemDetails mapping, Swagger, composition.
tests/
  OrderIntake.UnitTests/       Domain, application, infrastructure and concurrency tests.
web/                           Angular 18 app.
```

Dependencies point inward: the domain knows nothing about EF Core, HTTP or Angular, which is what lets its rules be tested without any of them running.
