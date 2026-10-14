# VectorNNTP.BackFiller architecture

This document describes the current standalone BackFiller process. BackFiller was extracted from VectorNNTP.NNTPD. Shared configuration that still mentions NNTPD is shared-domain context. It is not a second solution in this repository.

The worker consumes RabbitMQ Article Work, retrieves articles from upstream NNTP providers, parses them, retains accepted payloads in memory, and serves them to cache clients over a TLS listener. It does not listen for NNTP.

## Host lifecycle

`BackFillerApplicationHostedService` is the Generic Host adapter. It starts and stops `ApplicationServiceManager`.

`ApplicationServiceManager` starts registered `IApplicationService` instances in registration order. On startup failure it stops the services that already started (rollback) and leaves the manager idle so start can be tried again. Stop runs in reverse order. One `BackFillerShutdownOptions.GracePeriodSeconds` budget covers the whole stop or rollback loop. It is not granted separately to each service. Overlapping start and stop throw `InvalidOperationException` before waiting on the manager gate.

After start, non-null `IApplicationService.Execution` tasks are watched. A fault, a successful completion, or cancellation of that execution raises `UnexpectedServiceTermination`. `BackFillerApplicationHostedService` marks `BackFillerApplicationHealth` unhealthy and calls `IHostApplicationLifetime.StopApplication`. `IApplicationLifecycleOptions.StopHostOnUnexpectedServiceTermination` exists and is not read.

`SystemdLifecycleNotifier` sends `READY=1` at most once after `IHostApplicationLifetime.ApplicationStarted`. Startup failure does not report ready. Once shutdown begins, further readiness notifications are suppressed. `SystemdWatchdogService` sends keep-alives only while `IApplicationHealth.IsHealthyForWatchdog` is true. Readiness is not inferred from DI registration order.

`ApplicationLifecycle` in `src/BackFiller/Core/ApplicationLifecycle.cs` defines a Created → Starting → Running → Stopping → Stopped state machine. Nothing in `src/BackFiller/` or `src/BackFiller.tests/` constructs it. It is not the live host lifecycle.

Article Work shutdown uses `BackFillerShutdownOptions`:

- `DrainQueuedWork` defaults to true. When false, admitted work that has not entered processing is cancelled and NACK-requeued. Broker-prefetched deliveries that were never admitted are not drained by this flag.
- `FinishActiveArticles` defaults to true. When false, active work is cooperatively cancelled. Cancellation does not ACK the delivery.
- `ArticleWorkConsumerSession.RetireAsync()` without a token waits for admitted work and does not arm the grace timer. The host shutdown path passes a grace token from `GracePeriodSeconds`.

## Article Work settlement

`ArticleWorkDispositionPlanner` maps one outcome to ACK or NACK, the NACK requeue flag, and whether a terminal response is published. `ArticleWorkSettlementTests` locks this map for replyable deliveries when cancellation is not already requested.

| Outcome | Broker action | Publish terminal response |
|---|---|---|
| Success | Basic.Ack | Yes |
| ArticleNotFound | Basic.Ack | Yes |
| InvalidArticle | Basic.Ack | Yes |
| InvalidRequest | Basic.Ack | Yes when both `CorrelationId` and `ReplyTo` are present. Otherwise ACK without publication |
| ProviderFailure | Basic.Nack, requeue true | No |
| Cancelled | Basic.Nack, requeue true | No |
| UnexpectedFailure | Basic.Nack, requeue true | No |
| RetentionRejected | Basic.Nack, requeue true | No |

If cancellation is already requested, the planner returns NACK requeue and does not publish, including when the handler outcome was terminal.

`ArticleWorkSettlementLease` is one settlement attempt for one delivery tag on the original consumer channel and connection generation. It sets the settled flag before the broker RPC. If that RPC throws, the flag stays set and the lease does not retry. If the channel is stale, closed, or not the original channel, `TrySettleAsync` returns false without ACK or NACK, and the broker may redeliver. Pipeline settlement calls use `CancellationToken.None`.

When the success publisher reports that it does not complete success publication, the pipeline NACK-requeues and still returns `ArticleWorkOutcome.Success`. A non-cancellation publication failure of a non-requeue disposition still settles that disposition, including ACK. Cancellation of the publication attempt NACK-requeues.

Publisher confirms (`BasicAcksAsync` / `BasicNacksAsync` on an async-confirm publish channel) are the broker's response to an outbound publish. They are not the consumer ACK of the inbound delivery.

## NNTP client

`NntpProviderSession` is an upstream client. Connect requires a 200 or 201 greeting, always sends `CAPABILITIES`, sends `STARTTLS` only when that capability is advertised and the provider is not already on implicit TLS, then sends `AUTHINFO USER` and, when required, `AUTHINFO PASS`. `ARTICLE` takes a Message-ID. Idle pooled sessions may send RFC 3977 `DATE` when the provider keepalive is enabled. `ARTICLE` and `DATE` share one busy lock.

`NntpStartTlsTests` assert the client does not send `COMPRESS`. There is no NNTP listener, `IHAVE`, `TAKETHIS`, `MODE STREAM`, or `LIST` implementation.

`NntpArticleParser` parses transport-normalized article bytes after acquisition has removed the multiline terminator and undone dot-stuffing once. `ArticleTypeClassifier` sets control and cancel bits from `Control: cancel`, and a PGP-message bit from ASCII armor. That classification does not implement cancel propagation or PGPVERIFY.

Per-provider steady-state session count comes from MySQL `maxconnections` (`BackFillerProviderDefinition.MaxSessions`). `BackFillerNntpOptions.MaxConcurrentSessionEstablishments` bounds how many sessions may be connecting at once. The pool throws if it would exceed `MaxSessions`.

Relevant local RFC text is under `docs/standards/rfcs/`. For this client, start with RFC 3977 (`ARTICLE`, `DATE`, `CAPABILITIES`, multiline and dot-stuffing), RFC 4643 (`AUTHINFO`), and RFC 4642 / RFC 8143 (TLS). RFC 5536 is the article-format reference used by the parser. The other files in that directory are retained standards text. They are not evidence that this process implements those extensions.

## Byte-oriented protocol data plane

NNTP command bytes stay bytes through session I/O. `NntpProtocolIo` pre-encodes `DATE`, `CAPABILITIES`, `STARTTLS`, and the `ARTICLE`, `AUTHINFO USER`, and `AUTHINFO PASS` prefixes, and appends CRLF as bytes. The Message-ID argument is sent as ASCII bytes. A non-ASCII Message-ID is not placed on the wire.

Incoming status lines and article payloads are read as bytes. Dot-stuffing is removed in the stream reader before the parser sees the article. Header and body slices alias the caller buffer.

Strings are used at explicit boundaries: configuration, logging, AMQP metadata, and article-identity keys such as the Message-ID string stored by retention. Do not add a bytes-to-string-to-bytes conversion on the session I/O path because an API currently accepts a string.

The cache listener speaks VATP, not NNTP. That path is also byte-framed. It is a different protocol from the NNTP client above.

## Source-generated structured logging

Application logs use `LoggerMessageAttribute` on partial methods, usually in a `*LogMessages.cs` companion. Serilog is the host logging stack. Message templates keep structured property names. Do not preformat those properties into the message string.

Do not log Cloudflare API keys, ACME passwords, RabbitMQ passwords, `AUTHINFO` secrets, or article payloads. NNTP wire debug logs redact `AUTHINFO USER` and `AUTHINFO PASS`. Guard expensive trace or debug arguments with `IsEnabled` when building them has a cost.

`[LoggerMessage]` message strings are ASCII in the current contributing guidance. XML documentation may use Unicode.

## Not implemented

This process does not implement:

- an NNTP server or cleartext NNTP listener
- NNTP compression, streaming feeds, or `LIST` extensions
- PGPVERIFY or SPAMD (`docs/standards/pgpverify/` and `docs/standards/spamd/` are not in this repository)
- a BenchmarkDotNet project or a `TransitPublisherTests` baseline
- the `ApplicationLifecycle` state machine as the running host
