# VectorNNTP.BackFiller configuration

This document is the configuration contract for the standalone BackFiller host. Property names below match the current options types. Defaults and ranges below are the values those types and their validators enforce today.

Startup registers options with `ValidateOnStart` in `BackFillerServiceCollectionExtensions`. Invalid mandatory configuration fails startup. Do not log a complete options object: ACME, Cloudflare, RabbitMQ, and NntpDB values include secrets.

`VectorEnvironment` loads environment variables with the prefix `VECTOR__`. After the prefix is removed, `VECTOR__RABBITMQ__USERNAME` binds to `RabbitMQ:Username`. The prefixes `nntpd__` and `backfiller__` are not registered and are not aliases.

## Configuration files

The host content root is the application binary directory. `ApplicationJsonConfiguration` removes Generic Host `appsettings.json` and `appsettings.{environment}.json` sources and loads these files instead:

| File | Contents |
|---|---|
| `VectorNNTP.BackFiller.json` | Primary application configuration. The name is `{entry assembly name}.json`. |
| `VectorNNTP.BackFiller.{environment}.json` | Optional environment overlay. |
| `VectorNNTP.RabbitMQ.json` | RabbitMQ connectivity. It is a separate file. Its section name remains `RabbitMQ`. |

Environment variables and command-line arguments are applied after those files. A missing optional file does not fail configuration loading. Invalid JSON in a present file fails when that source is read. Mandatory option validation still runs at startup.

## BackFiller section

Section name: `BackFiller` (`BackFillerOptions.SectionName`).

| Key | Required | Notes |
|---|---|---|
| `ServerId` | Yes | `int?`, range 1–255. Missing and `0` both fail. There is no default. Old key `BackFiller:Id` is not the current key. |
| `DnsSuffix` | No | Binder default `usenet.ninja`. A successful `nntpsharedconfig` projection replaces it. Whitespace fails when the runtime snapshot canonicalizes the suffix. |
| `CloudFlareZoneId` | Shared config | The property remarks say it binds from `BackFiller:CloudFlareZoneId` only. `BackFillerOptionsValidator` does not itself reject an empty value. `NntpSharedConfigurationReader` rejects a blank `cloudflarezoneid` column, and `AcmeCloudflareOptionsValidator` rejects a blank zone id when shared database fields are validated. `ProviderAccountConfigurationService` copies the shared zone id onto `BackFillerOptions`. |
| `BindAddress` | No | Omitted or empty means all interfaces. Explicit addresses must be locally assigned. Wildcard tokens include `*`, `0.0.0.0`, and `::`. |
| `BindPortTls` | Yes | `int?`, range 1–65535. BackFiller is TLS-only. There is no `BackFiller:BindPort`. |
| `AcmeDirectoryUrl` | No | Binder default is the Let's Encrypt staging directory. ACME validation requires an absolute HTTPS URL. A successful `nntpsharedconfig` projection replaces the bound value. |
| `AcmeRenewalThresholdDays` | No | Binder default 30. ACME validation accepts 1–90. A successful `nntpsharedconfig` projection replaces the bound value before renewal uses it. |
| `AcmeStateDir` | No | Default `certs/`. Relative paths resolve against the application binary directory and must stay inside it after canonicalization. Rooted paths stay rooted. |
| `CertificateDirectory` | No | Default `certs`. Old key `DirCerts`. |
| `BackFillerAccountRefreshIntervalSeconds` | No | Default 60. Range 5–3600. This is the NntpDB account poll. It is not the per-account NNTP `DATE` keepalive. |

Generated `Fqdn` is `backfiller{ServerId:00}.{DnsSuffix}`. It is not a configuration key. It is empty when `ServerId` is missing or out of range, or when `DnsSuffix` is empty.

`BackFillerAcmeCloudflareOptionsAdapter` copies BackFiller bind and ACME fields onto `AcmeCloudflareOptions` and sets `IncludeNewsHostnameInCertificate` to false. The shared `AcmeCloudflareOptions` type still defaults that flag to true and documents `BindPort` 119 as an NNTPD cleartext port. BackFiller validation does not use `BindPort`.

Nested groups:

- `BackFiller:Logging`
- `BackFiller:ArticleRetention` — `MaximumRetainedPayloadGigabytes` default 4 (must be > 0 and within the physical-memory ceiling), `RetentionTtlSeconds` default 60 and range 1–60, `MaxOpenableRequestIdsPerArticle` default 16 and range 1–256, `SweepIntervalSeconds` default 1 and range 1–60
- `BackFiller:Shutdown` — `GracePeriodSeconds` default 30, range 5–600; `DrainQueuedWork` default true; `FinishActiveArticles` default true
- `BackFiller:Listener` — `TlsHandshakeTimeoutSeconds` default 30, range 1–300, copied into the runtime snapshot and used for the handshake. `IoProgressTimeoutSeconds` default 60, range 1–600, copied and used as the per-operation no-progress timeout. `MaxActiveConnections` default 1024, minimum 1, and the listener rejects further accepts at that count. `MaxQueuedFoundPayloadBytes` default 67108864, minimum 1, is copied into the snapshot; the cache listener does not read it. `ParserAccumulationMaxBytes` default 262144, minimum 32768, and `AwaitingReceiptAckTimeoutSeconds` default 30, range 1–300, are validated and are not copied into `BackFillerListenerRuntimeOptions`.
- `BackFiller:Nntp` — `MaxConcurrentSessionEstablishments` default 8, range 1–1024. Zero is invalid. This does not replace per-provider `MaxSessions` from MySQL
- `BackFiller:Systemd` — notify and watchdog enablement

## RabbitMQ section

Section name: `RabbitMQ` (`RabbitMqOptions.SectionName`). It is not nested under `BackFiller`.

Username and password are published from `nntpsharedconfig` (`rabbitmqusername`, `rabbitmqpassword`). Bound username and password values are discarded. Environment variables:

```text
VECTOR__RABBITMQ__USERNAME
VECTOR__RABBITMQ__PASSWORD
```

Never commit or log those values. Other connectivity settings, including `Hosts`, bind from the `RabbitMQ` section. See `RabbitMqOptions` for the current defaults (`WorkRequestMaxPayloadBytes` 1024, `RpcTimeoutSeconds` 30, `ConnectionBlockedTimeoutSeconds` 30, `ChannelLeaseTimeoutSeconds` 60).

## Shared ACME and Cloudflare secrets

These are not BackFiller-section keys. NNTPD, BackFiller, and StorageServer publish them from `nntpsharedconfig`. Environment variables:

```text
VECTOR__CLOUDFLAREAPIKEY
VECTOR__CLOUDFLAREZONEID
VECTOR__ACMEACCOUNT
VECTOR__ACMECERTIFICATEPASSWORD
```

`CloudFlareApiKey` is a secret. Ordinary options validation must not call Cloudflare or mutate DNS. Tests inject NIC data through `ILocalIpAddressAssignee` instead of reading the live host.

`BackFiller:CloudFlareZoneId` is the application key. `VECTOR__CLOUDFLAREZONEID` is the shared environment variable documented on `AcmeCloudflareOptions`. Do not invent an `nntpd__` name for either.

## NntpDB

`NntpDbOptions.SectionName` is `NntpDb`. The connection string name is `ConnectionStrings:NntpDB`. The environment variable is:

```text
ConnectionStrings__NntpDB
```

That name has no `VECTOR__` prefix. Do not log the connection string. The same key is what NNTPD uses. That is shared-domain configuration, not an instruction to build the NNTPD solution.

## Validation behavior that is easy to misread

- Missing `ServerId` is not the same as `0`. Both fail. The property is nullable so those cases stay distinct.
- `BindPortTls` missing, `0`, or outside 1–65535 fails. TLS disabled is not a valid BackFiller mode.
- `nntpd__cloudflareapikey` and `nntpd__CloudFlareZoneId` are not current environment variables.
- Do not log `BackFillerOptions`, `AcmeCloudflareOptions`, or `RabbitMqOptions` as a whole object.
- `AcmeStateDir` and `BackFiller:Logging:File:LogDir` use the same rule. A relative value, including one that contains `..`, is rejected when its canonical path leaves the application binary directory. A rooted value is an explicit location and is not forced back under that directory.
