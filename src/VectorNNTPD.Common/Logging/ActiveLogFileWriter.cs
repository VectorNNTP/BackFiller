using System.Text;
using Serilog.Events;
using Serilog.Formatting;

namespace VectorNNTPD.Common.Logging
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
        /// <summary>Text writer over the file, or over <see cref="_counted"/> when a size cap is set.</summary>
        private readonly TextWriter _output;

        /// <summary>Active file, opened for append.</summary>
        private readonly FileStream _file;

        /// <summary>Byte counter in front of <see cref="_file"/>. <see langword="null"/> when there is no size cap.</summary>
        private readonly CountingStream? _counted;

        /// <summary>Formatter for events written to this file.</summary>
        private readonly ITextFormatter _formatter;

        /// <summary>Byte cap. <see langword="null"/> does not reject events.</summary>
        private readonly long? _fileSizeLimitBytes;

        /// <summary>Whether events stay in the writer until <see cref="FlushToDisk"/> or dispose.</summary>
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
        public ActiveLogFileWriter(
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
        /// <param name="logEvent">Event to format.</param>
        /// <returns><see langword="false"/> when the file was already at the cap and nothing was written.</returns>
        public bool TryEmit(LogEvent logEvent)
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
        public void FlushToDisk()
        {
            _output.Flush();
            _file.Flush(flushToDisk: true);
        }

        /// <inheritdoc />
        public void Dispose() => _output.Dispose();

        /// <summary>Counts bytes written through to the file, including bytes already present at open.</summary>
        private sealed class CountingStream : Stream
        {
            /// <summary>File stream that receives the writes.</summary>
            private readonly Stream _stream;

            /// <summary>Wraps <paramref name="stream"/> and starts the count at its current length.</summary>
            /// <param name="stream">Open file stream. This wrapper disposes it.</param>
            public CountingStream(Stream stream)
            {
                _stream = stream;
                CountedLength = stream.Length;
            }

            /// <summary>Gets the bytes already in the file plus bytes written through this wrapper.</summary>
            public long CountedLength { get; private set; }

            /// <summary>Gets <see langword="false"/>. This stream does not read.</summary>
            public override bool CanRead => false;

            /// <summary>Gets whether the inner stream can seek.</summary>
            public override bool CanSeek => _stream.CanSeek;

            /// <summary>Gets <see langword="true"/>.</summary>
            public override bool CanWrite => true;

            /// <summary>Gets the inner stream length.</summary>
            public override long Length => _stream.Length;

            /// <summary>Gets the inner position. Setting it is not supported.</summary>
            /// <exception cref="NotSupportedException">The setter is called.</exception>
            public override long Position
            {
                get => _stream.Position;
                set => throw new NotSupportedException();
            }

            /// <summary>Writes <paramref name="count"/> bytes and adds that count to <see cref="CountedLength"/>.</summary>
            /// <param name="buffer">Source buffer.</param>
            /// <param name="offset">Start offset in <paramref name="buffer"/>.</param>
            /// <param name="count">Number of bytes to write.</param>
            public override void Write(byte[] buffer, int offset, int count)
            {
                _stream.Write(buffer, offset, count);
                CountedLength += count;
            }

            /// <summary>Writes <paramref name="buffer"/> and adds its length to <see cref="CountedLength"/>.</summary>
            /// <param name="buffer">Bytes to write.</param>
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                _stream.Write(buffer);
                CountedLength += buffer.Length;
            }

            /// <summary>Flushes the inner stream.</summary>
            public override void Flush() => _stream.Flush();

            /// <summary>Seeking is not supported.</summary>
            /// <param name="offset">Ignored.</param>
            /// <param name="origin">Ignored.</param>
            /// <returns>This method does not return.</returns>
            /// <exception cref="NotSupportedException">Always thrown.</exception>
            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            /// <summary>Forwards <paramref name="value"/> to the inner stream.</summary>
            /// <param name="value">New length.</param>
            public override void SetLength(long value) => _stream.SetLength(value);

            /// <summary>Reading is not supported.</summary>
            /// <param name="buffer">Ignored.</param>
            /// <param name="offset">Ignored.</param>
            /// <param name="count">Ignored.</param>
            /// <returns>This method does not return.</returns>
            /// <exception cref="NotSupportedException">Always thrown.</exception>
            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            /// <summary>Disposes the inner stream when <paramref name="disposing"/> is <see langword="true"/>.</summary>
            /// <param name="disposing"><see langword="true"/> when called from <see cref="Dispose"/>.</param>
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
