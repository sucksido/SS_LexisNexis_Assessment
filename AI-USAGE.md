# How AI tooling was used

The brief permits AI tools and asks for the prompts and the thinking behind them. This is that record.

**Tool:** Claude (Anthropic), used as a pair-programmer in an agentic session with access to the repository.

**Short version:** I used it the way I would use a strong pair — to draft code fast once I had decided what the code should do, to argue with me about the decisions, and to catch the things I would have missed at 11pm. Every design decision in [SOLUTION.md](SOLUTION.md) is one I made and can defend; the model's job was to implement them well and to tell me when I was wrong.

---

## The working method

I did not ask for "an order system" and ship what came back. The loop was:

1. **I decide what the interesting problem is.** Here it was the ambiguity in one sentence of the brief (section 1 of SOLUTION.md). No model can make that call for you, because it is a product decision about what the sales rep should experience.
2. **I ask the model to argue both sides,** so I am choosing rather than defaulting.
3. **I state the decision and ask for an implementation.**
4. **I review it as a pull request** — read every line, challenge anything I would challenge in a colleague's PR.
5. **I ask it to attack its own work** before I accept it.

Steps 2 and 5 are where most of the value was. Step 3 is the part that is fast, and the part that is least interesting.

---

## The prompts that mattered

### 1. Framing the ambiguity

> The brief says the system must "avoid creating duplicate orders when the same client-provided reference is submitted more than once, and behave consistently from a user's point of view." That is ambiguous. Enumerate every reasonable interpretation, and for each one describe the situation in which it produces the wrong outcome for a sales rep. Do not recommend one yet.

**Why:** asking for a recommendation first gets you a recommendation and stops your thinking. Asking for failure modes first gets you the material to decide with. The four options and their failure cases in SOLUTION.md section 1 came out of this exchange; I chose option 4 because option 2's failure mode — silently discarding a correction while reporting success — is the one that loses money and trust.

### 2. Drawing the line on "the same order"

> If a repeat submission returns the original order, I need to define "repeat". Propose which fields are material to that decision and which are cosmetic. For each field argue the case for both classifications, and name the user-visible consequence of getting it wrong in each direction.

**Why:** this is the actual design work in the feature and it is a business judgement, not a technical one. The two-directional framing is deliberate — it forces the trade-off into the open instead of producing a plausible-sounding list. The outcome (money and goods are material; notes and display names are not) is section 2.

### 3. Attacking the concurrency story

> Assume check-then-insert. Describe every way two concurrent submissions of the same reference can both succeed, including across multiple API instances. For each defence I might add, state precisely what it does not cover.

**Why:** "add a lock" is the reflex answer and it is incomplete. Forcing the model to say what each defence *fails* to cover is what produced the three-layer design, and — more importantly — the honest admission that the in-process gate is worthless across processes and that the unique index is the only authoritative guard. That admission is in SOLUTION.md section 3 because a reviewer deserves to know it.

### 4. Finding the traps

> I am using EF Core 8 with the in-memory provider by default and SQLite as an option. What will silently behave differently between those two providers, in ways a passing test suite would not reveal?

**Why:** this is the class of question where an AI genuinely outperforms me — recall of specific framework gotchas. It surfaced two that mattered: the in-memory provider accepts a unique index and then ignores it, and SQLite cannot translate `ORDER BY` over a `DateTimeOffset`. Both are handled in the code and both are documented. I verified both against the EF Core documentation rather than taking the answer on trust, which is the part of AI use that is not optional.

### 5. Tests that are worth reading

> Write tests for duplicate prevention under concurrency. Constraints: each test name is a sentence about business behaviour; the race must be deterministic rather than timing-dependent; and include a test that demonstrates duplicates *do* appear when the protection is removed.

**Why:** the last constraint is the one I care about. A test that asserts the bug exists when you delete the guard is documentation that cannot go stale — it tells the next person what the gate was holding up before they remove it. `Without_the_gate_an_unconstrained_store_produces_duplicates` is that test.

### 6. Adversarial review

> Review this as a hostile senior reviewer. Find: rules duplicated in more than one place, comments that restate the code instead of explaining the decision, tests that would pass against a broken implementation, and anything that would embarrass me in a technical interview.

**Why:** models default to agreement. You have to ask for the opposite explicitly. This pass is where the "comments explain why, never what" rule got enforced, and where a couple of assertions that would have passed against a no-op implementation got tightened.

### 7. The document I did not want written for me

> Draft SOLUTION.md covering the decisions we made, the trade-offs, and everything we chose not to build. Use my reasoning from this session, not generic best-practice prose. Where I have not given you a reason, leave a gap rather than inventing one.

**Why:** a design document written by a model that was not in the room reads exactly like one. The instruction to leave gaps rather than invent reasoning is what kept it accurate — and I filled those gaps in myself.

---

## Where it was wrong, and what I caught

Worth stating plainly, because "AI wrote it and it worked" is not a claim anyone should make:

- **Banker's rounding.** The first draft of `Money` used `Math.Round(value, 2)`. That is `MidpointRounding.ToEven` by default, so `2.345` becomes `2.34`. Commercial invoicing rounds away from zero. I caught this because I know the .NET default, not because the code looked wrong — it looked perfectly reasonable. There is now a test pinning it.
- **A leaking idempotency gate.** The first version kept a `SemaphoreSlim` per key in a dictionary forever, which is a memory leak dressed as a lock. I asked what happens after a million distinct references and the reference-counted version with eviction followed, along with the test that asserts the count returns to zero.
- **Attribute constants.** A `[InlineData]` with `decimal` arguments — C# does not allow decimal constants in attributes. It does not compile. A reminder that generated code is a proposal, not a result.
- **Over-eager abstraction.** An early suggestion introduced a repository interface per query. I cut it. Interfaces exist here to make specific failure modes testable, not as a reflex.

---

## What I did not delegate

- **The interpretation of the brief.** Section 1 of SOLUTION.md is a product decision.
- **Which fields are material to a duplicate.** Section 2 is a business rule.
- **What to leave out.** Section 10 is a scoping decision, and scope is judgement about time and risk, which is mine to own.
- **Accepting anything unverified.** Every framework claim was checked against the documentation, and the whole thing was built and tested locally before it was pushed.

---

## My honest assessment of the tooling

It compresses the distance between a decision and working code, and it is very good at recall — framework gotchas, edge cases in a hash normalisation, the API surface of a library I use monthly rather than daily. It is good at arguing a position if you ask it to argue, and poor at arguing if you do not, because its default is to agree with you.

It is not a substitute for knowing what you are building. Every decision in this repository that was actually hard was hard because it required a view about what a sales rep should experience when something goes wrong, and that view has to come from a person. What the tool bought me was the time to have those arguments properly instead of spending the budget on boilerplate.
