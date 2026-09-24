# Solution: design decisions and trade-offs

This document explains why the code looks the way it does. It is organised around the decisions that were actually hard, and it is honest about the ones that went the other way.

---

## 1. Reading the brief

Most of the brief is unambiguous. One sentence is not:

> Sales reps sometimes resubmit the same order... the system must avoid creating duplicate orders when the same client-provided reference is submitted more than once, and behave consistently from a user's point of view.

"Avoid creating duplicates" has at least four defensible readings:

1. **Reject the second submission.** Simple. Also wrong: a rep whose browser hiccupped now sees an error for an order that saved fine, and has no way to tell that from a real failure.
2. **Silently ignore the second submission and return success.** Feels kind. It is the dangerous one: if the rep *changed* something before resending, you have just told them their correction was saved when it was discarded.
3. **Overwrite the original with the new content.** Turns an idempotency key into an upsert key. A retried stale request from a flaky network can then undo a deliberate later edit.
4. **Return the original order, as long as the request really is the same one.** A repeat is a repeat; a different order wearing the same reference is a conflict the human must resolve.

I built the fourth, and the rest of the design follows from it.

The phrase that decides it is "behaves consistently from a user's point of view". Consistency means the rep can hit Submit as many times as they like and always end up looking at exactly one order containing exactly what they typed. Options 2 and 3 both break that promise in the case that matters — when the second submission is not actually the same.

This is the same contract Stripe, PayPal and every serious payments API use, for the same reason: the client cannot tell a lost response from a lost request, so it has to be safe to retry.

### The resulting contract

| Situation | Response | Why |
| --- | --- | --- |
| New reference | `201 Created` | An order was created. |
| Same reference, same content | `200 OK`, `X-Idempotent-Replay: true` | A repeat. Returns the original. Nothing was written. |
| Same reference, different content | `409 Conflict` + the existing order's id | Not a repeat. The human has to decide. |
| Same reference, different customer | `201 Created` | Two customers may both call their order "PO-1001". |

The status code carries the meaning; the header exists so a client can distinguish the two successes without parsing the body.

### Where the reference comes from

The brief says "client-provided reference", and the first version took that literally: the intake form had a text box and the rep typed their PO number into it. I changed that late, and the change is worth explaining because it looks like it undercuts the whole feature.

The form now asks the server for the next reference (`GET /api/orders/next-reference`, `PO-000001` and upward) and shows it read-only. Two reasons. A rep typing a reference by hand is the single largest source of the *bad* kind of duplicate — the one where the same order is entered twice under `PO-1001` and `PO 1001` and the system is right to treat them as different. And a reference the rep cannot edit makes the accidental-resubmit case, which is the case the brief actually cares about, reproducible on demand: reload, submit the same basket twice, get one order.

**The domain did not change.** `Order` still accepts any non-empty string up to 64 characters and does not care who composed it, because an import job or a partner integration will bring its own. The API contract above is unchanged. What changed is only which client populates the field — and `OrderReferenceSequence` is deliberately a *suggestion*, not a reservation: it hands out a number, nothing is held, and the unique index remains the only thing that actually guarantees uniqueness. It is atomic within one process (`Interlocked.Increment` over a lazily seeded counter); across two processes it would hand the same number to both, and the second insert would lose to the index and be recovered exactly like any other duplicate. A production system would use a database sequence. `OrderReferenceSequenceTests` pins down the parts that are easy to get wrong: resuming above what is already stored rather than from most-recently-created, and tolerating the references that predate the sequence, since the rows reps typed by hand do not disappear.

**What this costs, and I want to be upfront about it:** the UI can no longer reach the 409 case, because reaching it requires reusing a reference with different contents and the form will not let you. That path is still fully tested (`OrderSubmissionTests`) and still demonstrable through Swagger, but it is no longer a thing you can do by hand in the app. That was a deliberate trade: a screen that cannot produce a confusing conflict, against a screen that can demonstrate one. I took the first, and I would argue for it again — the conflict response exists for integrations, not for a rep with a keyboard.

---

## 2. "Same content" — the request fingerprint

`RequestFingerprint.Compute` reduces a submission to a SHA-256 hash of its *material* fields. Deciding what counts as material was the most interesting judgement call in the project.

**Material:** currency, and per line the SKU, quantity and unit price.

**Not material:** notes, and the product display name.

The reasoning runs in both directions, and both directions have a failure mode:

- Too strict, and harmless re-sends become errors. A caller who fixes a typo in the notes and resends gets a 409 they cannot resolve, because the "conflict" is a comment field. An integration would end up inventing `PO-1001-B` to get around the system, and now the reference no longer matches the paperwork.
- Too loose, and a real amendment is silently swallowed. If quantity were not material, changing 2 units to 3 and resubmitting would return the old order and report success.

So the line sits at *money and goods*. Anything that changes what is owed or what is shipped is a different order. Anything cosmetic is not.

The hash is also normalised before it is taken, so that irrelevant representation differences do not produce false conflicts:

- Lines are **sorted**, because re-ordering rows in the UI does not change the order.
- SKUs and currency are **upper-cased**, because `kb-001` and `KB-001` are the same product.
- Prices are **normalised to 2dp** before hashing, so `89.9` and `89.90` agree. Prices finer than a cent never reach this point (see §5), so this only fixes scale, never value.

All of this is pinned down by `RequestFingerprintTests`, where each test is one sentence about business meaning rather than one assertion about a hash.

**Trade-off I accepted:** storing a hash means that when the fingerprints differ we know *that* the order changed but not *what* changed. A richer diff would need the original request body stored alongside the order. For a conflict message that says "this reference already belongs to a different order, here it is" that is not worth the storage or the PII footprint, but on a system where reps argue about who changed what, it would be.

---

## 3. Preventing duplicates under concurrency

A single check-then-insert is a race. Two requests can both read "no existing order" before either writes. Under a load balancer they are not even on the same machine. So there are three defences, each covering what the one before it cannot.

**Defence 1 — read before write.** Handles the overwhelmingly common case: the rep clicked twice, a second apart. Cheap, and it catches almost everything.

**Defence 2 — an in-process keyed gate** (`KeyedIdempotencyGate`). Serialises callers that share a `(customer, reference)` key, so the read-then-write is atomic within one process. It is a reference-counted `SemaphoreSlim` per key, not a dictionary of semaphores that lives forever — a naive version of this is a slow memory leak, which is exactly what `Entries_are_reclaimed_once_nobody_holds_them` exists to prevent.

Two things it deliberately does *not* do: it does not serialise unrelated orders (different keys run in parallel — `Different_keys_do_not_block_each_other` fails if that regresses), and it does not pretend to work across processes.

**Defence 3 — a unique index** on `(CustomerId, ExternalReference)`. This is the only guard that is actually authoritative, because it is the only one the database enforces regardless of how many API instances are running. When it fires, EF throws `DbUpdateException`; the repository translates that into a `DuplicateReferenceException`, and the service treats it as "someone else won the race", re-reads the winner and returns it as a replay. **Losing the race produces the same answer as winning it**, which is the whole point.

### The uncomfortable detail

The default storage is the EF Core in-memory provider, and **it accepts a unique index and then ignores it.** So in the default configuration defence 3 does not exist, and the gate is load-bearing rather than belt-and-braces.

Rather than hide that, the test suite states it. `Without_the_gate_an_unconstrained_store_produces_duplicates` configures a store with no unique index and no gate and asserts that duplicates *appear*. It is a test that asserts the bug, and it is there so that anyone who decides the gate looks redundant finds out immediately what it was holding up.

`The_unique_index_on_customer_and_reference_is_declared` covers the other half: no runtime test can prove a constraint the in-memory provider ignores, so that test asserts the index is declared on the EF model, and fails the day someone removes it from the configuration.

### What I would do differently in production

Drop defence 2. In-process locking is a correctness *optimisation*, not a correctness guarantee, and on more than one instance it buys much less than it appears to. Behind a real database the unique index plus the catch-and-resolve path is sufficient and simpler. The gate is here because the brief asks for in-memory or file-backed storage, and it is the only thing standing between that recommendation and a duplicate under load.

---

## 4. Architecture

Four projects, dependencies pointing inward.

```
Api  ->  Application  ->  Domain
             ^
      Infrastructure
```

- **Domain** — `Order`, `OrderLine`, `Customer`, `Money`, `OrderStatusPolicy`. No EF, no ASP.NET, no NuGet packages at all. Its rules are testable without a database or a web server, and the tests run in milliseconds.
- **Application** — use cases and contracts. Depends on `IOrderRepository` and `IIdempotencyGate`, both defined here, both implemented elsewhere. This is what lets `OrderServiceHarness` run the real service against fakes that can be told to *fail in a specific way*.
- **Infrastructure** — EF Core, the repositories, the gate.
- **Api** — controllers, ProblemDetails mapping, Swagger, composition root.

**Is this over-engineered for a four-hour take-home?** For the feature set, yes — one project would have shipped it. It is here because the assessment asks to see architecture and because the seams paid for themselves: the concurrency tests need a repository that can be *told* to lose a race, which is only possible because the service depends on an interface. On a genuinely small tool I would start with one project and split when a seam started to hurt.

### `Order` is an aggregate, not a bag of properties

Private setters, a private `_lines` collection exposed as `IReadOnlyList`, a static `Create` factory, and behaviour methods (`ChangeStatus`, `RecalculateTotals`). You cannot construct an invalid `Order`, you cannot mutate a line to make the totals wrong, and you cannot move it to an illegal status. The invariants live with the data they constrain, so there is no path that bypasses them — including future paths nobody has written yet.

### Status rules live in one table

`OrderStatusPolicy` holds the transition graph as data. The domain reads it to enforce moves; the API projects it onto every order response as `allowedTransitions`; the Angular UI renders one button per entry. **The rule exists once.** The client cannot offer a move the server would reject, and when the workflow changes the UI follows it without a front-end deployment.

The alternative — transition rules in the domain, mirrored in a TypeScript constant — is the kind of duplication that stays correct for exactly as long as nobody changes it.

---

## 5. Money

**`decimal`, never `double`.** `0.1 + 0.2 != 0.3` in binary floating point, and an order total that is a cent out is a support ticket.

**A price finer than a cent is refused, not quietly rounded.** This is the decision I changed my mind about, and the reason is worth the paragraph.

The first version rounded the unit price on the way in and then multiplied: `UnitPrice = Money.Round(unitPrice)` followed by `LineTotal = UnitPrice * Quantity`. That looks accommodating and it overcharges. Three units at `9.995` become three units at `10.00`, and the customer is billed `30.00` for something the rep priced at `29.99`. The error is not a rounding cent — it scales with the quantity, and it is silent, because the stored price no longer matches the one that was typed.

So `Money.IsCentPrecision` is now the single definition of a price the system will accept, and both the request validator and the `OrderLine` constructor ask it the same question. A sub-cent price comes back as a 400 naming the SKU. **A price we were not asked to change is not ours to change** — if a rep really means `9.995`, that is a conversation about the price list, not something an intake form should decide on their behalf.

The consequence: unit prices are exact to the cent and quantities are whole numbers, so **every line total multiplies out exactly and the subtotal is exactly the sum of the line totals.** No rounding drift is tucked in anywhere. A person checking the order by hand gets the same figure the system does, which is the property that actually matters on an invoice. `The_subtotal_is_exactly_the_sum_of_the_line_totals` pins it.

**Rounding away from zero, not to even — and honestly, it does not fire yet.** `Money.Round` uses `MidpointRounding.AwayFromZero` because .NET's default is banker's rounding, which turns `2.345` into `2.34`; commercial invoicing wants `2.35`. But given the rule above, no amount on the order path is ever inexact, so the rounding call in `OrderLine` currently cannot change a value. It is a guard, not a live code path, and I would rather say so than claim a rounding policy the code never exercises.

It stays for two reasons. The first percentage discount, tax rate or currency conversion to land on a line makes it load-bearing immediately, and the alternative is rediscovering the midpoint question under deadline. And a guard nobody exercises is a guard nobody notices breaking — so it is tested directly, in `MoneyTests`, rather than incidentally through order totals. `The_dotnet_default_would_answer_differently` asserts both answers side by side, so the day someone "tidies up" to the framework default, the test says exactly what was lost.

**Computed on the server, always.** The client sends quantities and unit prices; it never sends a total. The Angular form shows a running figure clearly labelled a preview, because a number in the browser that is not the number that gets stored is a lie waiting to be believed.

**What I would add next:** the Angular form validates `min(0)` but not cent precision, so typing `1.005` costs the rep a round trip to find out. The server is right to refuse it; the client should refuse it sooner.

---

## 6. Error handling

Every failure is RFC 7807 ProblemDetails, produced by one .NET 8 `IExceptionHandler`. The controllers contain no `try`/`catch`, and the domain throws meaningful exceptions without knowing that HTTP exists.

Each response carries a stable machine-readable `code`. **Clients branch on the code, never on the message** — a message is prose, and prose gets rewritten the first time someone fixes a typo in it.

The errors try to be *useful*, not merely correct:

- A rejected status change returns 409 with `currentStatus`, `requestedStatus` and `allowedTransitions`. It does not just say no; it says what to do instead, and the UI repaints its buttons from that list.
- A reference clash returns `existingOrderId`, so the UI can link straight to the order that is in the way.
- Validation failures come back grouped per field, camelCased to match the JSON that was sent, so the Angular form binds every message to its control in one pass instead of the rep fixing one error at a time.

409 rather than 400 for both conflicts, deliberately: the request is well-formed, it just disagrees with the current state of the world. That distinction is what separates "fix your payload" from "something moved while you were typing".

---

## 7. Validation in two places, on purpose

FluentValidation at the application boundary answers "is this well-formed?" and reports every problem at once. The domain enforces the same invariants again in `Order.Create`.

That is duplication, and it is intentional. The validator exists to give a good error message; the domain exists to be correct. If a future caller — a message consumer, an import job, a test — bypasses the validator, the domain must still refuse to build a broken order. The validator is a courtesy; the domain is the guarantee.

The duplication that is *not* intentional is duplicating the rule itself. Both layers call `Money.IsCentPrecision` rather than each carrying its own expression, because two copies of a rule are two rules the moment somebody edits one. The first version of this code had exactly that problem in a subtler form — the validator rejected sub-cent prices while the domain silently rounded them — and §5 covers what that cost.

Validators are registered explicitly rather than by assembly scanning. Scanning needs an extra package and hides a registration behind a convention; five `AddScoped` lines are greppable.

---

## 8. The Angular app

Angular 18, standalone components, no NgModules. Signals for component state, RxJS only where there is an actual stream (HTTP, and the debounced search box). Routes are lazily loaded, so the bundle that renders the list does not contain the submission form.

**One HTTP boundary.** `OrderApiService` is the only file that knows about HTTP. Components receive domain-shaped results and `ApiError`s — never an `HttpErrorResponse`, a status code or a header name. That boundary is what keeps components testable against a plain stub, and it is where the 201-vs-200 replay signal is translated into something the UI can express in a sentence.

**Server-side filtering and paging.** Client-side filtering would have been less code and is correct only while the entire result set fits on one page. After that it quietly starts hiding matches that live on page two — the worst kind of bug, because the screen still looks right.

**No component library.** Three screens do not justify Material's footprint and theming API. Roughly 300 lines of CSS covers it. Past a dozen screens the trade flips.

**Status buttons come from the server.** Covered above; it is the decision I would most want to defend in a review.

**The reference field is disabled, and that is a trap Angular sets for you.** A disabled `FormControl` is excluded from `form.value` *and* from validation. So `Validators.required` on the reference can never fire, and a naive `this.form.value` would post a request with no reference at all. Two mitigations, both deliberate: `toRequest()` reads `getRawValue()`, and `send()` refuses to run unless the reference actually arrived (`referenceState() === 'ready'`), with both submit buttons disabled until then. If the sequence endpoint is down the form says so and offers a retry rather than failing at the server. This is the kind of thing that works in the happy path and quietly posts garbage on a slow connection, which is why the guard is explicit rather than relying on the form's own validity.

**Currency is a two-item `<select>`, and the client-side pattern validator came out.** The server accepts any three-letter ISO code; the UI offers USD and ZAR. A regex checking a value the user can no longer type is a rule that lies about being checked. The list is hard-coded, and that is right only while it is short — a third currency makes it a server-owned list served alongside the status graph, for exactly the reason the status graph is server-owned.

---

## 9. Testing

The backend suite is grouped by what it protects rather than by class:

- **Domain** — totals and price precision (`OrderTotalsTests`), the money primitives including the rounding mode (`MoneyTests`), status transitions. No infrastructure.
- **Application** — submission, replay, conflict, status changes, driven through the real `OrderService` against controllable fakes.
- **Infrastructure** — the EF queries run against a real provider, because ordering, filtering and paging are LINQ a provider has to *translate*. Testing them against a hand-written fake would only prove the fake sorts correctly. Plus metadata assertions for the constraints the in-memory provider ignores, and the reference sequence (`OrderReferenceSequenceTests`), which is mostly a test of what it does with input it did not create: references a rep typed under the old behaviour, a negative number that would walk the counter backwards, a number wider than a `long`.
- **Concurrency** — the gate in isolation, and 25 simultaneous identical submissions through the full service.

Test names are sentences about behaviour: `A_cancelled_order_still_owns_its_reference`, `Losing_the_insert_race_resolves_as_a_replay`. A failing test should tell you what broke without your having to read it.

The fakes have deliberate switches — `EnforceUniqueIndex`, `WriteLatency` — so a test can choose its failure mode and the race window can be widened enough to be reproducible instead of flaky.

**What is not tested:** there are no end-to-end tests and no Angular component tests beyond the API service. `Program.cs` already exposes `public partial class Program` so a `WebApplicationFactory` suite can be added without changes, and that is where I would spend the next hour: one test that drives a real HTTP double-submission through the real pipeline against SQLite, proving the 201/200 contract end to end.

---

## 10. What I deliberately did not build

Every one of these was considered and cut, and the reason matters more than the list.

- **Authentication and authorisation.** The brief says internal tool; adding auth would have consumed a large share of the budget and demonstrated nothing about the actual problem. In production this sits behind the corporate IdP, and `CustomerId` scoping is already in place as the natural tenancy seam.
- **Payments, inventory, shipping, tax.** Out of scope, and each is a system rather than a feature. Notice that `Total` and `Subtotal` are separate fields even though they are currently equal — that is the seam where tax and shipping land, and keeping them separate now costs nothing and avoids a data migration later.
- **A real database.** The brief recommends in-memory or file-backed. EF Core means moving to PostgreSQL or SQL Server is a provider swap plus migrations; the only provider-specific code in the solution is the SQLite unique-constraint detection and one `DateTimeOffset` converter, both isolated and commented.
- **Migrations.** `EnsureCreated` is honest for a demo. A production system needs migrations from commit one, and would not use `EnsureCreated` at all.
- **Multi-currency conversion.** Currency is recorded and validated, and orders in different currencies are never summed together. Conversion needs a rate source, a rate-at-time-of-order decision and a rounding policy per currency — a project, not a field.
- **Soft delete / order editing.** The status workflow covers cancellation. Editing an order would raise versioning, audit and "does an amendment invalidate the fingerprint" questions that the brief does not ask.
- **Distributed idempotency (Redis, etc.).** Correct answer for a multi-instance deployment, wrong answer for a take-home with in-memory storage. `IIdempotencyGate` is an interface precisely so this is a registration change.
- **An event log or outbox.** Would be the right shape once anything downstream cares about order status. Nothing does yet.
- **Generated TypeScript clients from OpenAPI.** For six endpoints the generator adds a build step, generated code in the repo and a version to keep in step, to save writing sixty lines once. On a larger API I would generate.
- **Optimistic concurrency on status changes.** Two reps confirming the same order at the same moment currently both succeed, and the second is a harmless no-op. A `rowversion` column would be the fix if the transitions ever gained side effects, at which point "already in that status" stops being harmless.

---

## 11. Known limitations

Stated plainly, because a reviewer will find them anyway:

1. **The in-process gate does not span processes.** Behind a load balancer, defence 3 is the only guard, so the in-memory provider must not be used there.
2. **`EnsureCreated`, not migrations.** Schema changes mean deleting the SQLite file.
3. **Customers are created implicitly on first submission**, matched by email. Convenient for the brief, wrong for a real system with a customer master.
4. **No retry or resilience policy on the client.** A dropped response leaves the rep to click Submit again — which is safe, and is the whole point of the idempotency work, but an automatic retry would be better.
5. **The conflict message cannot say what changed**, only that something material did. See section 2.
6. **Search is a `LIKE` on three columns.** Fine at this size, wrong at a million rows, where it wants a real index or a search engine.
7. **The reference sequence is process-local and leaves gaps.** Two API instances would hand out the same number and the second insert would lose to the unique index — recovered correctly, but noisy. A rep who opens the form and walks away burns a number. Both are acceptable for a demo and both want a database sequence in production. See section 1.
8. **The 409 conflict is no longer reachable from the UI**, by design, since the reference is server-issued. It is covered by tests and reproducible through Swagger.

---

## 12. If I had another four hours

In priority order:

1. `WebApplicationFactory` integration tests over the real pipeline against SQLite, including the 201/200/409 submission matrix.
2. PostgreSQL with EF migrations, and the same concurrency test run against it — that is where the unique index stops being a claim and becomes a demonstration.
3. Structured logging with Serilog, correlation ids flowing from the Angular client through to the log line.
4. Angular component tests for the form's server-error binding and the detail page's transition buttons.
5. Optimistic concurrency on status changes.
