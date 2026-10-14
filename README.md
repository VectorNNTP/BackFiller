# BackFiller (Standalone)

Standalone migration of `VectorNNTP.BackFiller` into an independent repository.

## Structure

- `src/BackFiller/BackFiller.csproj` - standalone BackFiller worker app (net10.0)
- `src/BackFiller/` - production code organized by responsibility (for example `Acme`, `Articles`, `Configuration`, `Core`, `Hosting`, `Nntp`, `Transport`)
- `src/BackFiller.tests/BackFiller.Tests.csproj` - xUnit test suite, namespace `VectorNNTP.BackFiller.Tests`
- `BackFiller.slnx` - solution including app + tests

## Build

```powershell
dotnet build .\BackFiller.slnx -c Release -p:Platform=x64
```

## Test

```powershell
dotnet test .\src\BackFiller.tests\BackFiller.Tests.csproj -c Release -p:Platform=x64
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

The standalone project has no external build/runtime dependency on a separate `VectorNNTP.Common` project or assembly.
