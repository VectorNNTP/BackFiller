using System.Collections.Concurrent;
using System.IO.Pipelines;
using VectorNNTP.BackFiller.Nntp;

namespace VectorNNTP.BackFiller.Tests.TestDoubles
{
    /// <summary>
    /// Queues <see cref="ScriptedNntpServer"/> instances and can fail, delay, or block <c>ConnectAsync</c>.
    /// Records attempts and the highest concurrent connect occupancy. Does not open real TCP connections.
    /// </summary>
    internal sealed class ScriptedNntpTransportFactory : INntpTransportFactory
    {
        /// <summary>Scripted servers handed out in FIFO order on connect.</summary>
        private readonly ConcurrentQueue<ScriptedNntpServer> _servers = new();

        /// <summary>Serializes appends to <see cref="ConnectAttempts"/> from overlapping connect calls.</summary>
        private readonly object _attemptsGate = new();

        /// <summary>Connect calls currently inside <see cref="ConnectAsync"/>.</summary>
        private int _currentConnects;

        /// <summary>Peak concurrent connect occupancy for pool limit assertions.</summary>
        private int _maxObservedConnects;

        /// <summary>Provider definitions passed to each <see cref="ConnectAsync"/> in invocation order.</summary>
        public List<BackFillerProviderDefinition> ConnectAttempts { get; } = [];

        /// <summary>When set, thrown after delay and blocking; no stream is returned.</summary>
        public Exception? ConnectException { get; set; }

        /// <summary>When set, awaited inside connect before failure or dequeuing a scripted server.</summary>
        public TimeSpan? ConnectDelay { get; set; }

        /// <summary>Completed when a connect enters after recording the attempt and updating concurrency counters.</summary>
        public TaskCompletionSource? ConnectStarted { get; set; }

        /// <summary>When set, each connect waits on this task until the test releases it.</summary>
        public TaskCompletionSource? BlockConnect { get; set; }

        /// <summary>Gets the highest concurrent <see cref="ConnectAsync"/> occupancy observed.</summary>
        public int MaxObservedConcurrentConnects => Volatile.Read(ref _maxObservedConnects);

        /// <summary>Gets how many <see cref="ConnectAsync"/> calls are inside the method right now.</summary>
        public int CurrentConcurrentConnects => Volatile.Read(ref _currentConnects);

        /// <summary>Queues a scripted server handed out FIFO on the next successful connect.</summary>
        /// <param name="server">In-memory peer whose client stream is returned from connect.</param>
        public void Enqueue(ScriptedNntpServer server)
        {
            ArgumentNullException.ThrowIfNull(server);
            _servers.Enqueue(server);
        }

        /// <summary>
        /// Records the provider, tracks concurrent occupancy, optionally delays or blocks, then dequeues a scripted server.
        /// Throws when no server was enqueued or when <see cref="ConnectException"/> is configured; never opens TCP.
        /// </summary>
        /// <param name="provider">Recorded in <see cref="ConnectAttempts"/> for assertions.</param>
        /// <param name="options">Accepted for interface compatibility; not applied to the fake transport.</param>
        /// <param name="cancellationToken">Honored during delay, blocking, and before dequeuing.</param>
        /// <returns>A duplex stream wired to the dequeued <see cref="ScriptedNntpServer"/>.</returns>
        public async Task<Stream> ConnectAsync(
            BackFillerProviderDefinition provider,
            NntpSessionOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(provider);
            lock (_attemptsGate)
            {
                ConnectAttempts.Add(provider);
            }
            var current = Interlocked.Increment(ref _currentConnects);
            try
            {
                while (true)
                {
                    var observed = Volatile.Read(ref _maxObservedConnects);
                    if (current <= observed
                        || Interlocked.CompareExchange(ref _maxObservedConnects, current, observed) == observed)
                    {
                        break;
                    }
                }

                ConnectStarted?.TrySetResult();
                if (BlockConnect is not null)
                {
                    await BlockConnect.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                if (ConnectDelay is { } delay)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (ConnectException is not null)
                {
                    throw ConnectException;
                }

                if (!_servers.TryDequeue(out var server))
                {
                    throw new InvalidOperationException("No scripted NNTP server was queued.");
                }

                return await server.AcceptClientAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _currentConnects);
            }
        }
    }

    /// <summary>In-memory NNTP server script over duplex pipes; tests enqueue response lines and cap reads.</summary>
    internal sealed class ScriptedNntpServer
    {
        /// <summary>Server-to-client bytes (responses and greeting).</summary>
        private readonly Pipe _toClient = new();
        /// <summary>Client-to-server bytes (commands).</summary>
        private readonly Pipe _toServer = new();
        /// <summary>Raw command lines read from the client.</summary>
        private readonly List<byte[]> _commands = [];
        /// <summary>Maps uppercased command prefix to response bytes.</summary>
        private Func<string, byte[]> _onCommand;

        /// <summary>Builds a server with the given greeting and default 430 handler.</summary>
        /// <param name="greeting">Initial status line written on accept.</param>
        public ScriptedNntpServer(string greeting = "200 posting allowed")
        {
            _onCommand = _ => "430 no such article\r\n"u8.ToArray();
            Greeting = greeting;
        }

        /// <summary>Initial status line written on accept unless <see cref="CompleteWithoutGreeting"/> is true.</summary>
        public string Greeting { get; }

        /// <summary>ASCII command lines read from the client after accept, in order.</summary>
        public IReadOnlyList<string> Commands =>
            _commands.Select(static bytes => Encoding.ASCII.GetString(bytes)).ToArray();

        /// <summary>Reserved for TLS negotiation tests; the scripted server does not perform a real handshake.</summary>
        public bool UseTlsRequested { get; set; }

        /// <summary>When true, accept completes the outbound pipe immediately without writing a greeting.</summary>
        public bool CompleteWithoutGreeting { get; set; }

        /// <summary>When true, the command loop exits after the first response flush.</summary>
        public bool CompleteAfterResponse { get; set; }

        /// <summary>Whether the most recently read command line ended with CRLF before the line feed.</summary>
        public bool LastCommandUsedCrlf { get; private set; }

        /// <summary>Set when an ARTICLE command is read, before any optional block.</summary>
        public TaskCompletionSource? ArticleStarted { get; set; }

        /// <summary>When set, ARTICLE waits here before the scripted response is written.</summary>
        public TaskCompletionSource? BlockArticle { get; set; }

        /// <summary>Set when a DATE command is read, before any optional block.</summary>
        public TaskCompletionSource? DateStarted { get; set; }

        /// <summary>When set, DATE waits here before the scripted response is written.</summary>
        public TaskCompletionSource? BlockDate { get; set; }

        /// <summary>When set, the server closes after reading DATE without writing a status.</summary>
        public bool CompleteAfterDateWithoutResponse { get; set; }

        /// <summary>
        /// When set, the client stream throws <see cref="NotSupportedException"/> on a DATE write.
        /// Connect commands are unaffected.
        /// </summary>
        public bool FaultClientDateWrite { get; set; }

        /// <summary>When set, CAPABILITIES includes this label (default STARTTLS).</summary>
        public string StartTlsCapabilityLabel { get; set; } = "STARTTLS";

        /// <summary>When true, CAPABILITIES advertises STARTTLS.</summary>
        public bool AdvertiseStartTls { get; set; }

        /// <summary>When true, CAPABILITIES advertises COMPRESS. The client must not negotiate it.</summary>
        public bool AdvertiseCompress { get; set; }

        /// <summary>Full CAPABILITIES response override, including status and terminator when complete.</summary>
        public string? CapabilitiesResponse { get; set; }

        /// <summary>When set, the server closes after writing the CAPABILITIES response.</summary>
        public bool CompleteAfterCapabilities { get; set; }

        /// <summary>STARTTLS command response. Used only when the client issues STARTTLS.</summary>
        public string StartTlsResponse { get; set; } = "382 Continue with TLS negotiation\r\n";

        /// <summary>When set, the server closes after the STARTTLS status (handshake then fails).</summary>
        public bool CompleteAfterStartTls { get; set; }

        /// <summary>Installs a string responder for commands not handled by built-in CAPABILITIES, STARTTLS, or QUIT logic.</summary>
        /// <param name="responder">Maps the full command line to a response encoded as ASCII bytes.</param>
        public void Respond(Func<string, string> responder)
        {
            ArgumentNullException.ThrowIfNull(responder);
            _onCommand = command => Encoding.ASCII.GetBytes(responder(command));
        }

        /// <summary>Installs a byte responder for generic commands; use when the response is not plain ASCII text.</summary>
        /// <param name="responder">Maps the full command line to raw response bytes.</param>
        public void RespondBytes(Func<string, byte[]> responder)
        {
            ArgumentNullException.ThrowIfNull(responder);
            _onCommand = responder;
        }

        /// <summary>
        /// Writes the greeting when enabled, starts the background command loop, and returns a duplex client stream.
        /// Does not listen on a network socket.
        /// </summary>
        /// <returns>A stream backed by in-memory pipes to this scripted peer.</returns>
        public async Task<Stream> AcceptClientAsync()
        {
            if (CompleteWithoutGreeting)
            {
                await _toClient.Writer.CompleteAsync().ConfigureAwait(false);
                return new DuplexPipeStream(_toClient.Reader, _toServer.Writer, this);
            }

            var greeting = Encoding.ASCII.GetBytes(Greeting.EndsWith("\r\n", StringComparison.Ordinal)
                ? Greeting
                : Greeting + "\r\n");
            _toClient.Writer.Write(greeting);
            await _toClient.Writer.FlushAsync().ConfigureAwait(false);
            _ = RunServerAsync();
            return new DuplexPipeStream(_toClient.Reader, _toServer.Writer, this);
        }

        /// <summary>
        /// Reads CRLF-delimited commands, applies built-in handlers and the configured responder, and completes the outbound pipe on exit.
        /// Swallows exceptions so the client observes a closed pipe rather than this task faulting.
        /// </summary>
        private async Task RunServerAsync()
        {
            var reader = new NntpTestLineReader(_toServer.Reader);
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    LastCommandUsedCrlf = reader.LastLineUsedCrlf;
                    _commands.Add(line);
                    var text = Encoding.ASCII.GetString(line);
                    if (text.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    if (text.Equals("CAPABILITIES", StringComparison.OrdinalIgnoreCase))
                    {
                        var capabilities = Encoding.ASCII.GetBytes(BuildCapabilitiesResponse());
                        _toClient.Writer.Write(capabilities);
                        var capabilitiesFlush = await _toClient.Writer.FlushAsync().ConfigureAwait(false);
                        if (CompleteAfterCapabilities || capabilitiesFlush.IsCompleted)
                        {
                            break;
                        }

                        continue;
                    }

                    if (text.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase))
                    {
                        var startTls = Encoding.ASCII.GetBytes(
                            StartTlsResponse.EndsWith("\r\n", StringComparison.Ordinal)
                                ? StartTlsResponse
                                : StartTlsResponse + "\r\n");
                        _toClient.Writer.Write(startTls);
                        var startTlsFlush = await _toClient.Writer.FlushAsync().ConfigureAwait(false);
                        if (CompleteAfterStartTls || startTlsFlush.IsCompleted)
                        {
                            break;
                        }

                        continue;
                    }

                    if (text.StartsWith("ARTICLE", StringComparison.OrdinalIgnoreCase))
                    {
                        ArticleStarted?.TrySetResult();
                        if (BlockArticle is not null)
                        {
                            await BlockArticle.Task.ConfigureAwait(false);
                        }
                    }
                    else if (text.Equals("DATE", StringComparison.OrdinalIgnoreCase))
                    {
                        DateStarted?.TrySetResult();
                        if (BlockDate is not null)
                        {
                            await BlockDate.Task.ConfigureAwait(false);
                        }

                        if (CompleteAfterDateWithoutResponse)
                        {
                            break;
                        }
                    }

                    var response = _onCommand(text);
                    _toClient.Writer.Write(response);
                    var flush = await _toClient.Writer.FlushAsync().ConfigureAwait(false);
                    if (CompleteAfterResponse || flush.IsCompleted)
                    {
                        break;
                    }
                }
            }
            // codeql[cs/catch-of-all-exceptions] The scripted peer must finish the pipe after any read or command failure.
            catch (Exception)
            {
                /* The caller observes the closed pipe rather than this task. */
            }
            finally
            {
                await _toClient.Writer.CompleteAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Builds the CAPABILITIES multiline from flags and optional override; does not reflect live server state.</summary>
        /// <returns>Full CAPABILITIES response including status line and terminator when synthesized.</returns>
        private string BuildCapabilitiesResponse()
        {
            if (CapabilitiesResponse is not null)
            {
                return CapabilitiesResponse.EndsWith("\r\n", StringComparison.Ordinal)
                       || CapabilitiesResponse.Length == 0
                    ? CapabilitiesResponse
                    : CapabilitiesResponse + "\r\n";
            }

            var body = new StringBuilder();
            body.Append("101 Capability list follows\r\n");
            body.Append("VERSION 2\r\n");
            body.Append("READER\r\n");
            if (AdvertiseStartTls)
            {
                body.Append(StartTlsCapabilityLabel);
                body.Append("\r\n");
            }

            if (AdvertiseCompress)
            {
                body.Append("COMPRESS DEFLATE\r\n");
            }

            body.Append(".\r\n");
            return body.ToString();
        }
    }

    /// <summary>
    /// Stream adapter over PipeReader/PipeWriter for scripted NNTP server tests.
    /// </summary>

    internal sealed class DuplexPipeStream(PipeReader reader, PipeWriter writer, ScriptedNntpServer? owner = null) : Stream
    {
        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var taken = (int)Math.Min(buffer.Length, result.Buffer.Length);
                if (taken == 0)
                {
                    reader.AdvanceTo(result.Buffer.End);
                    if (result.IsCompleted)
                    {
                        return 0;
                    }

                    continue;
                }

                result.Buffer.Slice(0, taken).CopyTo(buffer.Span);
                reader.AdvanceTo(result.Buffer.GetPosition(taken));
                return taken;
            }
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        /// <inheritdoc />
        /// <remarks>Honors <see cref="ScriptedNntpServer.FaultClientDateWrite"/> on the optional owner.</remarks>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (owner?.FaultClientDateWrite == true
                && buffer.Span.IndexOf("DATE"u8) >= 0)
            {
                throw new NotSupportedException("DATE write fault");
            }

            writer.Write(buffer.Span);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                reader.Complete();
                writer.Complete();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Reads CRLF-delimited lines from a PipeReader for scripted NNTP responses.
    /// </summary>

    internal sealed class NntpTestLineReader(PipeReader reader)
    {
        /// <summary>Whether the line returned from the latest read ended with CRLF before the line feed.</summary>
        public bool LastLineUsedCrlf { get; private set; }

        /// <summary>Reads the next LF-terminated line from the pipe, stripping a trailing CR when present.</summary>
        /// <returns>Line bytes without the delimiter, or null when the pipe completes before a line feed.</returns>
        public async Task<byte[]?> ReadLineAsync()
        {
            while (true)
            {
                var result = await reader.ReadAsync().ConfigureAwait(false);
                var buffer = result.Buffer;
                var position = buffer.PositionOf((byte)'\n');
                if (position is null)
                {
                    reader.AdvanceTo(buffer.Start, buffer.End);
                    if (result.IsCompleted)
                    {
                        return null;
                    }

                    continue;
                }

                var line = buffer.Slice(0, position.Value);
                var bytes = line.ToArray();
                LastLineUsedCrlf = bytes.Length > 0 && bytes[^1] == (byte)'\r';
                if (LastLineUsedCrlf)
                {
                    bytes = bytes[..^1];
                }

                reader.AdvanceTo(buffer.GetPosition(1, position.Value));
                return bytes;
            }
        }
    }
}
