---
name: lifecycle-and-concurrency
description: >-
  Protects VectorNNTP application lifecycle, service orchestration, cancellation,
  disposal, and concurrency safety around ApplicationLifecycle and hosted services.
  Use when changing startup/shutdown, IApplicationService, systemd notify/watchdog,
  async ownership, or diagnosing races, abandoned tasks, or shutdown bugs.
---

# Lifecycle and concurrency

## Purpose

Keep startup, running, and shutdown correct under concurrency and failure.

## When to use

- Changes to `ApplicationLifecycle`, `ApplicationServiceManager`, hosted services, systemd integration
- Cancellation, disposal, or concurrent stop/start work
- Lifecycle or concurrency bugs and their tests

## Authoritative architecture

Inspect before changing:

- `docs/architecture.md`
- `ApplicationServiceManager` start order / reverse stop / rollback
- `BackFillerApplicationHostedService` (Generic Host adapter; unexpected execution end stops the host)
- `SystemdLifecycleNotifier`, `SystemdWatchdogService`, `BackFillerApplicationHealth` / `IApplicationHealth`
- `ApplicationLifecycle` only when the change is to that type. The host does not construct it

Do **not** invent readiness or ordering guarantees. Prefer verified code comments and tests.

## Mandatory workflow

1. Restate the lifecycle contract under change (states, ordering, idempotency).
2. Trace ownership of tasks, CTS, sockets/streams (when present), and disposables.
3. Map cancellation: who owns the token; expected vs exceptional cancel.
4. Check concurrent stop/dispose/start interactions.
5. Ensure exceptions on background work are observed and classified.
6. Add deterministic lifecycle regression tests (see `testing` skill).
7. Run relevant tests; report exact results.

## Invariants to verify (when present in code)

- Valid state transitions only; invalid transitions throw as documented
- Startup failure rolls back partially started services
- Repeated stop is safe (await in-flight stop)
- Concurrent lifecycle ops are serialized where the implementation requires it
- Unexpected `IApplicationService.Execution` completion marks `BackFillerApplicationHealth` unhealthy and calls `IHostApplicationLifetime.StopApplication`. `StopHostOnUnexpectedServiceTermination` is not read
- systemd `READY=1` is sent at most once from Generic Host `ApplicationStarted`, not from `ApplicationLifecycle`
- No custom competing SIGTERM/SIGINT handlers unless architecture changes intentionally

## Avoid

- Blocking waits / sleeps to “wait for shutdown”
- Fire-and-forget without observation
- Assuming DI construction order equals readiness
- Premature dispose of resources still used by async work
- Replacing Article Work drain and settlement (`DrainQueuedWork`, `FinishActiveArticles`, NACK requeue on cancellation) with a different host's shutdown model

## Validation checklist

- [ ] Contract traced in real lifecycle types
- [ ] Ownership and cancellation paths explicit
- [ ] Concurrent stop/dispose considered
- [ ] Background exceptions observed
- [ ] Deterministic tests added/updated and run
- [ ] No invented lifecycle guarantees in docs/comments
