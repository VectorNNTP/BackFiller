# VectorNNTPD.BackFillerService Copilot Instructions

## Project

VectorNNTPD.BackFillerService is a standalone .NET 10 worker extracted from VectorNNTP.NNTPD. It retrieves unavailable Usenet articles from external NNTP providers, validates them, retains them, and settles Article Work through RabbitMQ. Correctness, bounded resources, and graceful shutdown are required.

The solution is `VectorNNTPD.slnx` and contains:

- `src/VectorNNTPD.BackFillerService/VectorNNTPD.BackFillerService.csproj`: production worker. Assembly and root namespace `VectorNNTPD.BackFillerService`.
- `tests/VectorNNTPD.BackFillerService.Tests/VectorNNTPD.BackFillerService.Tests.csproj`: xUnit tests. Assembly and root namespace `VectorNNTPD.BackFillerService.Tests`.

There is no `tools/` benchmark project and no `.copilot/PERFORMANCE-CONTEXT.md`. Do not invent throughput baselines. Shared NNTPD context that the code still uses, including `nntpsharedconfig` and `ConnectionStrings__NntpDB`, is shared-domain configuration, not a reason to build `VectorNNTP.NNTPD.sln`.

Authoritative contracts: `docs/architecture.md`, `docs/configuration.md`, and `docs/standards/rfcs/`. Agent entry point: `AGENTS.md`.

## Runtime and architecture

- Target `net10.0`, nullable enabled, implicit usings enabled. `Directory.Build.props` treats warnings as errors and enables analyzers. Release publish is self-contained Native AOT for `win-x64` and `linux-x64` only (`Directory.Build.targets`).
- Live host lifecycle is `ApplicationServiceManager` started by `BackFillerApplicationHostedService`. Services start in registration order and stop in reverse order. Startup failure rolls back services that already started.
- systemd `READY=1` is sent at most once after Generic Host `ApplicationStarted` (`SystemdLifecycleNotifier`). Do not infer readiness from DI registration order.
- Article Work settlement is specified in `docs/architecture.md`. Terminal outcomes ACK. Retryable outcomes NACK with requeue. The settlement lease makes one attempt on the original channel. A failed broker RPC is not retried on that lease. A stale or closed channel is left for broker redelivery.
- Treat external NNTP, RabbitMQ, DNS, Cloudflare, ACME, and database input as untrusted or failure-prone. Preserve cancellation and disposal ownership across asynchronous boundaries.
- The cache listener is TLS-only (`BackFiller:BindPortTls`). There is no `BackFiller:BindPort` and no cleartext NNTP listener. After TLS it speaks a partial RFC 3977 session: a `201` greeting, `ARTICLE` by Message-ID, `DATE`, and `QUIT`. Upstream NNTP remains a client: `CAPABILITIES`, conditional `STARTTLS`, `AUTHINFO`, `ARTICLE`, and idle `DATE`.
- Use source-generated structured logging. Do not log secrets or article payloads.

## Build and test

From the repository root, matching `.github/workflows/build.yml`:

```text
dotnet restore VectorNNTPD.slnx -p:Platform=x64
dotnet build VectorNNTPD.slnx --configuration Release -p:Platform=x64 --no-restore
dotnet test tests/VectorNNTPD.BackFillerService.Tests/VectorNNTPD.BackFillerService.Tests.csproj --configuration Release -p:Platform=x64 --no-build
```

Platform is x64 only. Output is `bin/x64/<Configuration>/`. Any CPU is rejected. Use the same `-p:Platform=x64` on a `--no-build` test as on the preceding build. Publish the worker with `--runtime win-x64` or `--runtime linux-x64` from `src/VectorNNTPD.BackFillerService/VectorNNTPD.BackFillerService.csproj`.

`dotnet format VectorNNTPD.slnx --verify-no-changes` is a local check. CI does not run it.

Builds enable .NET analyzers, code style, and generated documentation for the production project. The test project disables documentation generation and suppresses `CS1591`. Do not widen that suppression. Do not weaken, skip, or remove tests.

## Engineering conventions

- Follow `CONTRIBUTING.md` and the `.editorconfig` files under `src/VectorNNTPD.BackFillerService/` and `tests/VectorNNTPD.BackFillerService.Tests/`: explicit access modifiers, `_camelCase` private fields, PascalCase public members, and block-scoped namespaces. There is no root `.editorconfig`.
- Do not add filename banners or per-file copyright headers. Assembly copyright comes from `Directory.Build.targets`.
- Document contract-bearing symbols, including private ones when the contract is non-obvious. Do not document a symbol only to raise a coverage count.
- Prefer async I/O, explicit cancellation, and deterministic test coordination. Avoid blocking waits and unobserved fire-and-forget work.
- Performance changes require a measured baseline. Do not claim a throughput number without evidence from this environment.
- Local evidence, local release staging, and CI Native AOT publish output go in `.artifacts/` (gitignored). Permanent docs go in `docs/`. See `.cursor/rules/repository-artifacts.mdc`.

## Additional instructions

- `.github/instructions/csharp.instructions.md`
- `.github/instructions/testing.instructions.md`
- `.github/instructions/documentation.instructions.md`
- `.github/instructions/performance.instructions.md`
