# BackFiller (Standalone)

Standalone migration of `VectorNNTP.BackFiller` into an independent repository.

## Structure

- `src/BackFiller/BackFiller.csproj` - standalone BackFiller worker app (net10.0)
- `src/BackFiller/` - production code organized by responsibility (for example `Acme`, `Articles`, `Configuration`, `Core`, `Hosting`, `Nntp`, `Transport`)
- `tests/BackFiller.tests/BackFiller.Tests.csproj` - xUnit test suite, namespace `VectorNNTP.BackFiller.Tests`
- `BackFiller.slnx` - solution including app + tests
- `tools/Invoke-CodeQlAnalysis.ps1` - local traced C# CodeQL analysis

## Build

```powershell
dotnet build .\BackFiller.slnx -c Release -p:Platform=x64
```

## Test

```powershell
dotnet test .\tests\BackFiller.tests\BackFiller.Tests.csproj -c Release -p:Platform=x64
```

## Publish

Windows x64:

```powershell
dotnet publish .\src\BackFiller\BackFiller.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:PublishAot=true -p:SelfContained=true
```

Linux x64:

```powershell
dotnet publish .\src\BackFiller\BackFiller.csproj -c Release -p:Platform=x64 -r linux-x64 --self-contained true -p:PublishAot=true -p:SelfContained=true
```

The production project references the `StuDev.AotAnywhere` 1.0.4 MSBuild SDK. On Windows that SDK links `linux-x64` with the Zig toolchain restored by NuGet. A globalization-enabled Linux host still needs `libicu`.

Native AOT publish keeps trim and AOT analysis enabled. CI then runs the published executable with `VECTORNNTP_BACKFILLER_STARTUP_SMOKE=1` and `VECTORNNTP_BACKFILLER_STARTUP_SMOKE_CONFIG` pointing at an isolated JSON file. The process must print `BackFiller startup smoke: configuration serverId=1 fqdn=backfiller01.usenet.ninja` and exit 0. That checkpoint is configuration binding and host construction. It does not open a listener or contact a database, broker, DNS provider, or certificate authority.

The standalone project has no external build/runtime dependency on a separate `VectorNNTP.Common` project or assembly.

## Linux service

`tools/Publish-Release.ps1 -Platform linux-64` writes `.artifacts/release/linux-64/`. The Linux CI job writes `.artifacts/linux-x64/` and uploads that directory. Both contain `VectorNNTP.BackFiller`, `VectorNNTP.BackFiller.json`, `VectorNNTP.RabbitMQ.json`, `README.md`, and `vectornntp-backfiller.service`.

Install that directory at `/opt/vectornntp/backfiller`. `deploy/systemd/vectornntp-backfiller.service` uses that path for `ExecStart`, `WorkingDirectory`, and `Documentation=`. `ReadWritePaths` lists only `logs/` and `certs/`. Under `ProtectSystem=strict` that re-opens those two directories for writing. It does not change Unix ownership or mode. The shipped JSON writes file logs under `logs/` and ACME state under `certs/`, and startup creates files inside directories that must already exist.

Create the account, install the release as root-owned files, and give `vectornntp` write access only to `logs/` and `certs/` before enabling the unit:

```bash
sudo groupadd --system vectornntp || true
sudo useradd --system --gid vectornntp --home-dir /opt/vectornntp --shell /usr/sbin/nologin vectornntp || true
sudo install -d -o root -g root -m 0755 /opt/vectornntp /opt/vectornntp/backfiller
sudo install -o root -g root -m 0755 VectorNNTP.BackFiller /opt/vectornntp/backfiller/VectorNNTP.BackFiller
sudo install -o root -g root -m 0644 VectorNNTP.BackFiller.json VectorNNTP.RabbitMQ.json README.md /opt/vectornntp/backfiller/
sudo install -d -o vectornntp -g vectornntp -m 0750 /opt/vectornntp/backfiller/logs /opt/vectornntp/backfiller/certs
sudo install -o root -g root -m 0644 vectornntp-backfiller.service /etc/systemd/system/vectornntp-backfiller.service
sudo systemctl daemon-reload
sudo systemctl enable --now vectornntp-backfiller.service
```

`groupadd` and `useradd` may report that the account already exists; that is safe to ignore. Do not `chown -R` the install tree to `vectornntp` and do not use mode `0777`. The executable and JSON stay owned by root and are not writable by the service account. `TimeoutStopSec=60` covers the shipped 30-second shutdown grace; raise it when `BackFiller:Shutdown:GracePeriodSeconds` is raised. `WatchdogSec` stays unset until readiness has been checked on the host.

## CodeQL

Local analysis uses the installed CodeQL CLI. It traces the Release x64 restore and build, then runs the queries selected by `.github/codeql/codeql-config.yml`. That file selects `.github/codeql/csharp-with-alert-suppression.qls`, which imports the `security-extended` and `security-and-quality` suites and adds `AlertSuppression.ql` from the same query pack. The suites themselves do not include that query, and CodeQL CLI 2.27.2 does not record in-source suppressions unless it is analyzed with them. The database, SARIF, and logs are written under `.artifacts/codeql/`, which Git ignores.

A suppression is a `// codeql[rule-id]` comment on the line immediately before the alert, with no other code on that line. A same-line `// codeql[...]` comment is not recognized. A recognized suppression stays in the SARIF with `suppressions.kind` set to `inSource`. Each `cs/catch-of-all-exceptions` suppression is on a boundary that must contain every exception, including types the method cannot enumerate, and the comment states that contract. Generated System.Text.Json and regex sources are not edited and are not suppressed.

`codeql database interpret-results` on CLI 2.27.2 writes `suppressions.kind` as `inSource` and logs `Skipping non-rule, non-metric, non-diagnostic analysis cs/alert-suppression`. The local SARIF from that run still contains those `inSource` entries. The GitHub CodeQL log for the pull-request analysis of merge commit `8fd7de1bed9df4e8d81f3face517c0d2534f1eba` shows the same skip line and `Successfully uploaded results`. That workflow run has no SARIF artifact, and the code-scanning SARIF API returns processing status without the file, so the uploaded SARIF was not compared with the local file. GitHub recorded the analysis with `results_count` 2. The open alerts on `refs/pull/14/merge` are `cs/catch-of-all-exceptions` at `ArticleRetentionAuthority.cs` line 976 and `ArticleWorkSettlementLease.cs` line 115. Both catches on that commit already have this comment on the preceding line. That check did not inventory every default-branch alert. Open `Program.cs` alerts remain from an earlier default-branch analysis and were not results of this upload.

```powershell
.\tools\Invoke-CodeQlAnalysis.ps1
.\tools\Invoke-CodeQlAnalysis.ps1 -CodeQl C:\Tools\codeql\codeql.exe
```

A local SARIF file does not update or clear GitHub code scanning alerts. For a traced C# build, `paths-ignore` in the repository config does not exclude compiled files; the CLI records that limitation while creating the database.
