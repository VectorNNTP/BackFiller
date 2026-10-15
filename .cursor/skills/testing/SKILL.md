---
name: testing
description: >-
  Designs and implements meaningful deterministic regression tests for VectorNNTP
  using xUnit, explicit synchronization, and observable contracts. Use when writing,
  fixing, or reviewing tests, concurrency/lifecycle tests, or when the user asks for
  regression coverage.
---

# Testing

## Purpose

Protect real production contracts with deterministic, failure-sensitive tests.

## When to use

- Adding or changing tests
- Lifecycle, concurrency, config validation, or protocol conformance work
- Reviewing whether coverage is meaningful

## Mandatory workflow

1. Read the **production contract** (implementation + docs + callers) before asserting.
2. State the regression: which failure mode must this test catch?
3. Prefer testing observable behavior/invariants over private implementation details.
4. Use deterministic coordination (`TaskCompletionSource`, channels, semaphores, barriers, lifecycle signals).
5. Use timeouts only as **safety bounds**, never as the mechanism that makes the test pass.
6. Observe background exceptions; do not leave abandoned tasks.
7. Run the **relevant** tests; report exact pass/fail counts and command used.

## Project constraints

- Test project: `tests/BackFiller.tests/BackFiller.Tests.csproj` (`VectorNNTP.BackFiller.Tests`). Shared helpers live in `tests/BackFiller.tests/Fixtures/` and `tests/BackFiller.tests/TestDoubles/`
- Established patterns: fakes in `TestDoubles/`, harnesses in `Fixtures/`, injectable `ILocalIpAddressAssignee`, host builders without live sockets
- Do not require internet, live Cloudflare, DNS mutation, or bound listeners unless the task explicitly demands it
- Analyzer and nullable rules apply to test code. The test project already sets `GenerateDocumentationFile` false and suppresses `CS1591`. Do not add further `NoWarn` entries to hide failures

## Cover when relevant

- Happy path **and** meaningful failure paths
- Cancellation, disposal, repeated stop, concurrent stop/dispose
- Startup failure / partial start rollback
- Ordering: start registration order; stop reverse order
- Config validation boundaries (missing required, invalid ranges)
- Protocol framing / malformed input (when protocol code exists)

## Avoid

- `Thread.Sleep` / timing-based synchronization as the primary wait
- Mocks/fixtures that **guarantee** the asserted outcome
- Tests added only to inflate coverage
- Disabling, skipping, or weakening tests to get a green build
- Broad parallel-disable to hide races (narrow serialization only with justification)
- Settlement assertions that disagree with `ArticleWorkDispositionPlanner` and `ArticleWorkSettlementLease` (see `docs/architecture.md`)

## Failure classification

Before changing production or tests: production regression, intentional contract change, stale test, test defect, race, environment/tooling, or unknown — with evidence.

## Validation checklist

- [ ] Contract understood before assertions
- [ ] Test can fail if the bug returns
- [ ] Deterministic sync; no sleep-as-sync
- [ ] Background failures observed
- [ ] Relevant tests executed; exact results reported
- [ ] No secret values asserted or logged
