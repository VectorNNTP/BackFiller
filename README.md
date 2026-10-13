# BackFiller (Standalone)

Standalone migration of `VectorNNTP.BackFiller` into an independent repository.

## Structure

- `src/BackFiller.csproj` - standalone BackFiller worker app (net10.0)
- `src/Acme`, `src/Articles`, `src/Cloudflare`, `src/Configuration`, `src/Core`, `src/Dns`, `src/Hosting`, `src/Logging`, `src/Messaging`, `src/Networking`, `src/NntpDb`, `src/Transport` - integrated implementation formerly sourced from VectorNNTP.Common, now organized by BackFiller responsibility
- `tests/VectorNNTP.BackFiller.Tests` - migrated BackFiller test suite
- `BackFiller.slnx` - solution including app + tests

## Build

```powershell
dotnet build .\BackFiller.slnx
```

## Test

```powershell
dotnet test .\BackFiller.slnx -c Debug
```

## Publish

Windows x64:

```powershell
dotnet publish .\src\BackFiller.csproj -c Release -r win-x64
```

Linux x64:

```powershell
dotnet publish .\src\BackFiller.csproj -c Release -r linux-x64
```

The standalone project has no external build/runtime dependency on a separate `VectorNNTP.Common` project or assembly.
