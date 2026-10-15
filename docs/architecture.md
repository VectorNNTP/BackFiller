# VectorNNTP.BackFiller architecture

This document describes the current standalone BackFiller process. BackFiller was extracted from VectorNNTP.NNTPD. Shared configuration that still mentions NNTPD is shared-domain context. It is not a second solution in this repository.

The worker consumes RabbitMQ Article Work, retrieves articles from upstream NNTP providers, parses them, retains accepted payloads in memory, and serves them to cache clients over a TLS listener. After the handshake that listener speaks a partial RFC 3977 session: `ARTICLE` by Message-ID and `QUIT`. It does not listen for cleartext NNTP.

## Host lifecycle

`BackFillerApplicationHostedService` is the Generic Host adapter. It starts and stops `ApplicationServiceManager`.

`ApplicationServiceManager` starts registered `IApplicationService` instances in registration order. On startup failure it stops the services that already started (rollback) and leaves the manager idle so start can be tried again. Stop runs in reverse order. One `BackFillerShutdownOptions.GracePeriodSeconds` budget covers the whole stop or rollback loop. It is not granted separately to each service. Overlapping start and stop throw `InvalidOperationException` before waiting on the manager gate.

After start, non-null `IApplicationService.Execution` tasks are watched. A fault, a successful completion, or cancellation of that execution raises `UnexpectedServiceTermination`. `BackFillerApplicationHostedService` marks `BackFillerApplicationHealth` unhealthy and calls `IHostApplicationLifetime.StopApplication`. `IApplicationLifecycleOptions.StopHostOnUnexpectedServiceTermination` exists and is not read.

`SystemdLifecycleNotifier` sends `READY=1` at most once after `IHostApplicationLifetime.ApplicationStarted`. Startup failure does not report ready. Once shutdown begins, further readiness notifications are suppressed. `SystemdWatchdogService` sends keep-alives only while `IApplicationHealth.IsHealthyForWatchdog` is true. Readiness is not inferred from DI registration order.

`ApplicationLifecycle` in `src/BackFiller/Core/ApplicationLifecycle.cs` defines a Created → Starting → Running → Stopping → Stopped state machine. Nothing in `src/BackFiller/` or `tests/BackFiller.tests/` constructs it. It is not the live host lifecycle.

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
| InvalidArticle | Basic.Ack | Yes. Includes an article `NntpDeliveryFraming` cannot send. That rejection happens before retention, so the article is not stored. |
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

`NntpStartTlsTests` assert the client does not send `COMPRESS`. The listener below does not implement `IHAVE`, `TAKETHIS`, `MODE STREAM`, `LIST`, `CAPABILITIES`, `GROUP`, `HEAD`, `BODY`, `STAT`, posting, or authentication.

`NntpArticleParser` parses transport-normalized article bytes after acquisition has removed the multiline terminator and undone dot-stuffing once. `NntpArticleCanonicalMaterializer` writes the retained article buffer in that same pass, and `ArticleBodyWireStuffer` dot-stuffs body lines into that buffer. Headers are not stuffed, and the NNTP terminator is not stored. `ArticleId` remains BLAKE3 of the Message-ID value. `ArticleTypeClassifier` sets control and cancel bits from `Control: cancel`, and a PGP-message bit from ASCII armor. That classification does not implement cancel propagation or PGPVERIFY.

Per-provider steady-state session count comes from MySQL `maxconnections` (`BackFillerProviderDefinition.MaxSessions`). `BackFillerNntpOptions.MaxConcurrentSessionEstablishments` bounds how many sessions may be connecting at once. The pool throws if it would exceed `MaxSessions`.

Relevant local RFC text is under `docs/standards/rfcs/`. For this client, start with RFC 3977 (`ARTICLE`, `DATE`, `CAPABILITIES`, multiline and dot-stuffing), RFC 4643 (`AUTHINFO`), and RFC 4642 / RFC 8143 (TLS). RFC 5536 is the article-format reference used by the parser and by the listener's Message-ID check. The other files in that directory are retained standards text. They are not evidence that this process implements those extensions.

## NNTP listener

`CacheListenerService` binds the configured TLS endpoints and admits connections up to `MaxActiveConnections`. Each admitted socket gets one TLS 1.2 or 1.3 handshake with no client certificate and revocation checks off. The certificate lease is taken per connection and disposed with that connection. After the handshake succeeds, `NntpCacheListenerSession` uses `StreamCacheListenerTransport`. The session does not own the socket or the certificate. `IoProgressTimeout` still applies to each read and each non-empty write. Shutdown cancellation, handshake failure, timeout, I/O, and other connection failures use the same listener logs as before.

The greeting is `201 NNTP Service Ready, posting prohibited` because posting is not implemented. It is sent before any command response. Command keywords are ASCII case-insensitive. One or more spaces or tabs separate the keyword from its argument. Lines end in CRLF. A command longer than 512 octets, including CRLF, is answered with `501 Syntax error` and is not split into a later command. Bytes after one command stay in the buffer. `QUIT` with no argument sends `205 Connection closing` and returns; the service then closes the connection. `QUIT` with an argument is `501` and the connection stays open. Any other command is `500 Unknown command`. A peer close ends the session without another response.

`ARTICLE` with no argument, or with an all-digit article number, is `412 No newsgroup selected`. There is no selected group. Extra arguments, or a Message-ID that fails `NntpMessageIdValidation`, are `501 Syntax error`. That validator is the RFC 5536 grammar already used by article admission, so it is stricter than an opaque RFC 3977 message-id. A valid Message-ID that is not retained is `430 No Such Article Found`.

A successful claim writes `220 0`, the Message-ID octets from the command, ` article follows`, CRLF, the multiline article image, and one `.\r\n`. The claim is granted only when every retained line ends in CRLF and the buffer ends in CRLF. A bare CR, a bare LF, or NUL anywhere in the article is `NotFrameable`: the session responds `403 Article not available` before `220` or any article byte, and the stored buffer and byte accounting stay unchanged. A header line that begins with `.` is dot-stuffed only in the bytes written to the socket, so a client that removes one leading dot restores that header. The body is copied as retained, so a stuffing dot already stored on a body line is not added again. A dot-leading line with no `CRLF CRLF` separator is also `NotFrameable`, because the header region and the already-stuffed body cannot be distinguished. The same `403` is used when another delivery already holds the entry, and the connection stays open so the client can retry. `436` is not used.

The retained entry is removed only after the status line, the article bytes, and the terminator have all been accepted by the local transport. A failure, cancellation, or session fault before that rolls the claim back when the entry is still indexed and unexpired. TTL is not extended because a delivery is active. An active claim keeps the `ArtData` bytes reachable for the write even if expiry or disposal drops the authority's own reference, and it pins the entry against FIFO physical reclamation, including when its last RequestId is cancelled during the write. Expiry or disposal that already removed the entry does not reindex it on rollback. A second claim while one is active is refused and does not cancel a RequestId. One committed `ARTICLE` removes the entry and every RequestId attached to it. RequestIds remain Article Work capabilities that pin FIFO reclaim until cancel, TTL, committed delivery, or dispose. They are not a second listener protocol.

Acceptance of those writes by the local socket is not proof that the peer received or processed the article. A disconnect after some bytes have been accepted can leave the peer with a prefix; rollback then allows another delivery. A commit followed by a lost connection does not.

NNTP is the only listener protocol. There is no cleartext listener and no second framing after TLS.

## Byte-oriented protocol data plane

NNTP command bytes stay bytes through session I/O. `NntpProtocolIo` pre-encodes `DATE`, `CAPABILITIES`, `STARTTLS`, and the `ARTICLE`, `AUTHINFO USER`, and `AUTHINFO PASS` prefixes, and appends CRLF as bytes. The Message-ID argument is sent as ASCII bytes. A non-ASCII Message-ID is not placed on the wire.

Incoming status lines and article payloads are read as bytes. Dot-stuffing is removed in the stream reader before the parser sees the article. Header and body slices alias the caller buffer. The retained article image is that canonical article with body lines dot-stuffed once in the materializer's output buffer. The listener appends one `.\r\n`. It copies the retained body unchanged and adds a stuffing dot only to header lines that begin with `.`, and only in the socket image.

Strings are used at explicit boundaries: configuration, logging, AMQP metadata, and article-identity keys such as the Message-ID string stored by retention. The listener's command parser and `220` line stay on the command octets. Do not add a bytes-to-string-to-bytes conversion on the session I/O path because an API currently accepts a string.

The listener command path is byte-oriented, as is the upstream NNTP client. They are different roles: the client retrieves articles, and the listener serves retained bytes.

## Source-generated structured logging

Application logs use `LoggerMessageAttribute` on partial methods, usually in a `*LogMessages.cs` companion. Serilog is the host logging stack. Message templates keep structured property names. Do not preformat those properties into the message string.

Do not log Cloudflare API keys, ACME passwords, RabbitMQ passwords, `AUTHINFO` secrets, or article payloads. NNTP wire debug logs redact `AUTHINFO USER` and `AUTHINFO PASS`. Guard expensive trace or debug arguments with `IsEnabled` when building them has a cost.

`[LoggerMessage]` message strings are ASCII in the current contributing guidance. XML documentation may use Unicode.

## Not implemented

This process does not implement:

- a complete RFC 3977 server, a cleartext NNTP listener, listener `CAPABILITIES`, `GROUP`, `HEAD`, `BODY`, `STAT`, posting, or authentication
- NNTP compression, streaming feeds, or `LIST` extensions
- PGPVERIFY or SPAMD (`docs/standards/pgpverify/` and `docs/standards/spamd/` are not in this repository)
- a BenchmarkDotNet project or a `TransitPublisherTests` baseline
- the `ApplicationLifecycle` state machine as the running host
