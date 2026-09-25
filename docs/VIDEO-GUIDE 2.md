# Video guide — recording the 5–10 minute walkthrough

**This file is for you, not for the reviewers.** It is excluded from git (see `.gitignore`) so it cannot be pushed by accident. Delete it if you prefer.

---

## What they actually asked for

The recruiter's wording is the whole brief for this video:

> Rather than just walking through the code, use this time to go in-depth on your implementation. Explain **why** you made certain choices, **why you chose one approach over another**, and **what you decided not to do**. We use this to understand your reasoning.

Read that again. They are telling you the failure mode: a tour of the file tree. "Here's the domain layer, here's the controller, here's the service" scores nothing, because the repo already shows them that. The video exists to show them the thinking that the repo cannot.

They also say the take-home is the basis of the technical interview. So the video is not just an assessment — it is you choosing which questions you will be asked. Spend your minutes on the material you most want to discuss for an hour.

---

## Before you hit record

**Audio is the one thing that can sink an otherwise good video.** They mention it explicitly, which usually means they have received bad ones.

- Use a headset or earbuds mic, not the laptop's built-in. Laptop mics pick up the fan and the room.
- Record a 20-second test. Play it back **through headphones** and listen for: hum, echo, keyboard clatter, plosive pops on "p" and "b". Move the mic slightly off to the side of your mouth if you hear popping.
- Close Slack, Teams, email and anything that makes a noise or shows a notification.
- Silence your phone and put it face down.
- If your household is loud, record early or late. One barking dog can cost you a re-record.

**Screen setup:**

- Set your display to 1080p if you can, and bump the editor font to ~16pt. Reviewers often watch at half size on a laptop; unreadable code is skipped code.
- Light editor theme reads better in compressed video than a dark one. Not essential, but it helps.
- Close every tab you do not need. A visible tab bar full of "angular idempotency stack overflow" is a bad look.
- Have these ready in a fixed order so you never hunt for anything: the running app at `localhost:4200`, Swagger at `localhost:5080/swagger`, your editor with the three files you will show already open in tabs, and a terminal with the test output already green.

**Run everything once, all the way through, before you record.** Both servers up, tests passing, sample data seeded, your demo order already typed out on a sticky note so you are not composing a SKU on camera.

**Turn your camera on.** Loom puts your face in a bubble in the corner. It matters more than it should — people hire people. Look at the camera when you introduce yourself and when you close.

---

## Structure: 8 minutes

Aim for eight. Under five looks thin; over ten and they stop watching. Loom shows the length before they press play, and 8:00 is an easy yes.

### 0:00–0:40 — Who you are and what the problem was

Straight to camera, no screen share yet.

> "Hi, I'm Success Shibambu. This is my Order Intake and Tracking build.
>
> Before I show you anything, I want to tell you what I think the actual problem in this brief was, because most of it is straightforward CRUD and one sentence isn't.
>
> That sentence is: the system must avoid creating duplicate orders when the same reference is submitted twice, and behave consistently from the user's point of view. Almost everything I'm going to show you comes out of how I read that sentence — so let me start there."

You have now told them this is not a file-tree tour, and you have earned the next seven minutes.

### 0:40–2:00 — The decision, before any code

Still no code. This is the highest-value ninety seconds in the video. Screen share SOLUTION.md section 1 if you want something on screen, or just talk.

> "'Avoid creating duplicates' has at least four reasonable readings, and they're not equivalent.
>
> You could reject the second submission — simple, but a rep whose browser hiccupped now sees an error for an order that actually saved, and they can't tell that from a real failure.
>
> You could silently ignore it and return success. That one feels kind and it's the dangerous one: if the rep *changed* something before resending, you've just told them their correction was saved when you threw it away.
>
> You could overwrite the original. Now a retried stale request from a flaky network can undo a deliberate later edit.
>
> Or — what I built — you return the original order, but only if the request really is the same order. If it isn't, that's a conflict a human has to resolve.
>
> I picked that because of the phrase 'consistently from a user's point of view'. Consistency means the rep can hit Submit as many times as they like and always end up looking at exactly one order containing exactly what they typed. The two middle options both break that promise in the exact case that matters.
>
> It's also the contract Stripe and every serious payments API use, for the same reason: the client can't tell a lost response from a lost request, so retrying has to be safe."

Nobody else applying for this role will open like that.

### 2:00–3:30 — Demo the consequence

Now share the running app. Narrate what it *proves*, not what you are clicking.

1. Submit an order. Land on the detail page. **Point at the totals:** "these came back from the server — the form showed a preview, but the client never calculates money."
2. Go back, submit **the same reference and the same lines**. Land on the same order, banner visible. **Show the list still has one row.** "Same order, not a second one. The API answered 200 instead of 201, and the client can tell the difference."
3. Open the network tab or Swagger and point at `X-Idempotent-Replay: true` for one beat.
4. Submit the same reference with **one quantity changed**. 409, with a link to the order in the way. "That's not a repeat — that's a different order wearing the same reference. Guessing here is how you lose someone's amendment."
5. On the detail page, click Confirm. Then in Swagger, try Pending → Fulfilled on a fresh order and show the 409 that names the legal alternatives.

Keep moving. This section is proof, not exploration.

### 3:30–5:15 — The concurrency story

This is your strongest technical material. Open `OrderService.SubmitAsync` and read the three defences off it.

> "A check-then-insert is a race — two requests can both read 'nothing there' before either writes. So there are three defences, and what matters is what each one *doesn't* cover.
>
> First, read before write. That catches the rep who clicked twice a second apart, which is the overwhelmingly common case, and it's cheap.
>
> Second, an in-process keyed gate. It serialises callers that share a customer-plus-reference key. Two things it deliberately doesn't do: it doesn't block unrelated orders — different keys run in parallel, and there's a test that fails if that ever regresses — and it does **not** work across processes. Behind a load balancer it buys you nothing.
>
> Third, and this is the only one that's actually authoritative: a unique index on customer and reference. When it fires, EF throws, the repository translates it, and the service re-reads the winner and returns it as a replay. **Losing the race produces the same answer as winning it.** That's the whole point."

Then the part that separates you from the field:

> "Now the uncomfortable bit. The default storage is the EF in-memory provider, and it accepts a unique index and then ignores it. So in the default config, defence three doesn't exist and that gate is load-bearing rather than belt-and-braces.
>
> I didn't hide that — I wrote a test for it."

Open `Without_the_gate_an_unconstrained_store_produces_duplicates`.

> "This test configures a store with no unique index and no gate, and asserts that duplicates *do* appear. It's a test that asserts the bug. It's there so that when someone looks at that gate in six months and thinks it's redundant, they find out immediately what it was holding up.
>
> And in production I'd delete the gate. In-process locking is a correctness optimisation, not a guarantee. Behind a real database, the unique index plus the catch-and-resolve path is enough and it's simpler. The gate is here because the brief asked for in-memory storage."

Saying you would remove your own clever code is a senior signal. Most candidates defend everything they wrote.

### 5:15–6:15 — One decision that shows judgement

Pick **one**. My recommendation is the rule-lives-once story, because it spans backend and frontend and they are hiring for both.

Show `OrderStatusPolicy`, then `allowedTransitions` on the response, then the Angular detail component rendering buttons from it.

> "The transition rules exist in exactly one place — this table in the domain. The domain enforces it, the API projects it onto every order response, and the Angular page renders one button per entry.
>
> The client therefore has no copy of the rules at all. It can't offer a move the server would reject, and when the workflow changes the UI follows without a front-end deployment.
>
> The alternative is rules in the domain and a mirrored TypeScript constant. That stays correct for exactly as long as nobody changes it.
>
> I still handle the 409, because between rendering the page and clicking the button someone else might have moved the order on. And that 409 comes back carrying the transitions that *are* legal now, so the screen corrects itself rather than just apologising."

If you would rather show something else, the money rounding story is the other strong one: *"`Math.Round` is banker's rounding by default — 2.345 becomes 2.34. Invoicing rounds away from zero. There's a test pinning it, because this is exactly the thing someone tidies up assuming the default is fine."*

Do not do both. One decision explained properly beats three mentioned.

### 6:15–7:15 — What you chose not to build

They asked for this explicitly and most candidates skip it. It is free marks.

> "Things I deliberately left out, and why.
>
> No auth. It's an internal tool, it would have eaten a big share of four hours, and it demonstrates nothing about the actual problem. In production it sits behind the corporate IdP, and customer scoping is already the natural tenancy seam.
>
> No distributed idempotency — Redis or similar. Right answer for a multi-instance deployment, wrong answer for a take-home with in-memory storage. It's behind an interface, so it's a registration change, not a rewrite.
>
> No migrations. `EnsureCreated` is honest for a demo and wrong for production from commit one.
>
> No generated TypeScript client from the OpenAPI doc. For six endpoints the generator adds a build step, generated code in the repo and a version to keep in step, to save writing sixty lines once. Bigger API, I'd generate.
>
> And one that's a genuine gap rather than a choice: there are no end-to-end tests. `Program.cs` already exposes the partial class so a `WebApplicationFactory` suite drops in, and that's the first hour I'd spend next — one test driving a real HTTP double-submission through the full pipeline against SQLite."

Naming a real gap yourself is stronger than hoping they miss it. They will not miss it.

### 7:15–8:00 — Close

Back to camera.

> "Quick note on tooling: I used AI as a pair programmer, and the prompts and my reasoning are in AI-USAGE.md. The short version is that I used it to argue both sides of the decisions before I made them and to implement fast once I had, and it got things wrong — it gave me banker's rounding on the money type, and a first version of the idempotency gate that leaked a semaphore per key forever. Both are in that document with how I caught them.
>
> Everything else is in the README and SOLUTION.md. Thanks for watching — I'd enjoy digging into any of this."

---

## Two things to say that most candidates never do

1. **"Here's what I'd delete."** Volunteering that your in-process gate should not survive contact with production is the single clearest seniority signal available to you.
2. **"Here's what's still wrong with it."** No E2E tests, no migrations, the conflict message can't say *what* changed. Stating limitations plainly reads as confidence. Hiding them reads as not knowing.

---

## Mistakes that cost people the interview

- **Narrating the file tree.** "This is the domain folder, this is the application folder." They can read.
- **Reading code aloud.** Never read a line of code out. Say what it decides and why.
- **Apologising.** No "sorry, this is a bit rough" or "I ran out of time so it's not great". State a limitation as a decision with a reason, not as a confession.
- **Running over.** At ten minutes they stop. Put your best material in the first five.
- **Live debugging.** If something breaks on camera, stop the recording and start again. Do not narrate a fix.
- **Filler.** "Basically", "essentially", "kind of", "if that makes sense". Record once, listen back, notice your own tic, record again.
- **Silent clicking.** Never let the screen move without your voice explaining what it proves.

---

## Practical recording notes

- **Do a full rehearsal without recording.** Not a script read — just talk through it once. The second take is always dramatically better than the first, and rehearsal makes take one *be* take two.
- **Do not edit.** Loom's trimming is fine for topping and tailing, but a slightly imperfect continuous take sounds more competent than an obviously cut one. Fluency in conversation is what they are assessing.
- **Speak slightly slower than feels natural.** Nerves speed everyone up, and there is technical content here that needs to land.
- **Loom settings:** screen + camera, and check "HD" if your plan offers it. On the free plan, confirm your video is not over the 5-minute cap **before** you record eight minutes — if it is, record in Loom Desktop with a paid trial, or record locally with OBS or the Xbox Game Bar and upload to Google Drive or YouTube unlisted.
- **Test the share link in a private window** before you send it, and set it to "anyone with the link can view". A link that asks a reviewer to request access is a link nobody watches.
- **Put the video link in the README** as well as in the email. Reviewers arrive at the repo from all sorts of directions.

---

## Before you submit

- [ ] Repo is public, or `submissions@offerzen.com` has been added as a collaborator
- [ ] README, SOLUTION.md and AI-USAGE.md are all committed
- [ ] `docs/VIDEO-GUIDE.md` is **not** committed (it is gitignored, but check)
- [ ] `dotnet test` green, `npm test` green, `npm run build` clean — confirm on a fresh clone
- [ ] Video link is public and plays in a private browser window
- [ ] Video link is in the README
- [ ] Commit history looks like work, not one "initial commit" containing everything
- [ ] Both the repo link and the video link are in the reply email

On the commit history point: if it is currently one commit, it is worth splitting into a handful of sensible ones — domain, application, infrastructure, API, tests, Angular, docs. It takes ten minutes and it is the first thing some reviewers look at.
