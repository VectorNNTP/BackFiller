using Serilog.Events;
using Serilog.Formatting;

namespace VectorNNTP.BackFiller.Logging
{
    /// <summary>
    /// Appends formatted events to one active log file.
    /// </summary>
    /// <remarks>
    /// This writer appends formatted events to one active file and applies the file-size check.
    /// <see cref="GzipRollingFileSink"/> owns rolling, compression, and retention.
    /// Callers serialize access; this type has no lock of its own.
    /// </remarks>
    internal sealed class ActiveLogFileWriter : IDisposable
    {
        /// <summary>Formatter output. Disposing it disposes the file stream.</summary>
        private readonly TextWriter _output;

        /// <summary>Append-only file. Seeked to the end at open and flushed to disk by <see cref="FlushToDisk"/>.</summary>
        private readonly FileStream _file;

        /// <summary>Byte counter used when a size cap is set. Null when the file is unlimited.</summary>
        private readonly CountingStream? _counted;

        /// <summary>Formatter for this file only. Archived files are not rewritten.</summary>
        private readonly ITextFormatter _formatter;

        /// <summary>Byte cap including bytes already in the file at open. Null means no cap.</summary>
        private readonly long? _fileSizeLimitBytes;

        /// <summary>
        /// When false, each accepted event is flushed to the stream before the next size check.
        /// When true, bytes stay buffered until <see cref="FlushToDisk"/> or dispose.
        /// </summary>
        private readonly bool _buffered;

        /// <summary>Opens <paramref name="path"/> for append.</summary>
        /// <param name="path">Active log path. Created if it does not exist.</param>
        /// <param name="formatter">Formatter for this file only. Archived files are not rewritten.</param>
        /// <param name="fileSizeLimitBytes">Byte cap. <see langword="null"/> does not overflow.</param>
        /// <param name="encoding">Text encoding. <see langword="null"/> is UTF-8 without a BOM.</param>
        /// <param name="buffered">
        /// When <see langword="false"/>, each accepted event is flushed to the file stream
        /// so the next size check sees it. When <see langword="true"/>, bytes stay in the writer
        /// until <see cref="FlushToDisk"/> or dispose.
        /// </param>
        internal ActiveLogFileWriter(
            string path,
            ITextFormatter formatter,
            long? fileSizeLimitBytes,
            Encoding? encoding,
            bool buffered)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
            if (fileSizeLimitBytes is < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(fileSizeLimitBytes));
            }

            _fileSizeLimitBytes = fileSizeLimitBytes;
            _buffered = buffered;
            _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            _file.Seek(0, SeekOrigin.End);
            Stream output = _file;
            if (fileSizeLimitBytes is not null)
            {
                _counted = new CountingStream(_file);
                output = _counted;
            }

            _output = new StreamWriter(output, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        /// <summary>
        /// Writes <paramref name="logEvent"/> when the file is under the size cap.
        /// An event that begins under the cap is written in full.
        /// </summary>
        /// <returns><see langword="false"/> when the file was already at the cap and nothing was written.</returns>
        internal bool TryEmit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            if (_fileSizeLimitBytes is { } limit && _counted!.CountedLength >= limit)
            {
                return false;
            }

            _formatter.Format(logEvent, _output);
            if (!_buffered)
            {
                _output.Flush();
            }

            return true;
        }

        /// <summary>Flushes the writer and asks the operating system to persist the file.</summary>
        internal void FlushToDisk()
        {
            _output.Flush();
            _file.Flush(flushToDisk: true);
        }

        /// <inheritdoc />
        public void Dispose() => _output.Dispose();

        /// <summary>Counts bytes written through to the file, including bytes already present at open.</summary>
        /// <remarks>Seek, read, and setting <see cref="Position"/> are not supported. <see cref="SetLength"/> does not adjust <see cref="CountedLength"/>.</remarks>
        private sealed class CountingStream : Stream
        {
            /// <summary>Inner file stream. Disposed with this wrapper.</summary>
            private readonly Stream _stream;

            /// <summary>Starts the count at the stream length so an existing file is already at the cap when full.</summary>
            /// <param name="stream">File stream opened for append. Not null.</param>
            internal CountingStream(Stream stream)
            {
                _stream = stream;
                CountedLength = stream.Length;
            }

            /// <summary>Bytes present at open plus bytes written through this wrapper.</summary>
            internal long CountedLength { get; private set; }

            /// <summary>Always false. This stream is write-only.</summary>
            public override bool CanRead => false;

            /// <summary>Whether the inner stream can seek. This wrapper still rejects <see cref="Seek"/>.</summary>
            public override bool CanSeek => _stream.CanSeek;

            /// <summary>Always true.</summary>
            public override bool CanWrite => true;

            /// <summary>Current length of the inner stream.</summary>
            public override long Length => _stream.Length;

            /// <summary>Gets the inner position. Setting it throws <see cref="NotSupportedException"/>.</summary>
            public override long Position
            {
                get => _stream.Position;
                set => throw new NotSupportedException();
            }

            /// <summary>Writes <paramref name="count"/> bytes and adds that count.</summary>
            /// <param name="buffer">Source buffer.</param>
            /// <param name="offset">Start offset in <paramref name="buffer"/>.</param>
            /// <param name="count">Number of bytes written.</param>
            public override void Write(byte[] buffer, int offset, int count)
            {
                _stream.Write(buffer, offset, count);
                CountedLength += count;
            }

            /// <summary>Writes <paramref name="buffer"/> and adds its length.</summary>
            /// <param name="buffer">Bytes written to the inner stream.</param>
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                _stream.Write(buffer);
                CountedLength += buffer.Length;
            }

            /// <summary>Flushes the inner stream. This does not ask the operating system to persist the file.</summary>
            public override void Flush() => _stream.Flush();

            /// <summary>Not supported. The active log is append-only.</summary>
            /// <param name="offset">Ignored.</param>
            /// <param name="origin">Ignored.</param>
            /// <returns>This method does not return.</returns>
            /// <exception cref="NotSupportedException">Always thrown.</exception>
            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            /// <summary>Forwards to the inner stream and does not change <see cref="CountedLength"/>.</summary>
            /// <param name="value">New stream length.</param>
            public override void SetLength(long value) => _stream.SetLength(value);

            /// <summary>Not supported.</summary>
            /// <param name="buffer">Ignored.</param>
            /// <param name="offset">Ignored.</param>
            /// <param name="count">Ignored.</param>
            /// <returns>This method does not return.</returns>
            /// <exception cref="NotSupportedException">Always thrown.</exception>
            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            /// <summary>Disposes the inner stream when <paramref name="disposing"/> is true.</summary>
            /// <param name="disposing">True when the public dispose path is running. The inner stream is disposed only then.</param>
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _stream.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
