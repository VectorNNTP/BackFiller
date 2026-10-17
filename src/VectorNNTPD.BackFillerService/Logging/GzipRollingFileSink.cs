using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Sinks.File;

namespace VectorNNTPD.BackFillerService.Logging
{
    /// <summary>
    /// Daily (or other interval) rolling file sink that gzip-compresses a file when it becomes inactive
    /// and applies <c>retainedFileCountLimit</c> to both <c>.log</c> and <c>.log.gz</c> days.
    /// </summary>
    /// <remarks>
    /// When the interval rolls, this sink closes the active file, compresses that path, then opens the next file.
    /// The active path is never compressed or deleted. One lock covers roll, compression, and retention.
    /// </remarks>
    internal sealed class GzipRollingFileSink : ILogEventSink, IFlushableFileSink, IDisposable, ISetLoggingFailureListener
    {
        /// <summary>Serializes roll, compression, retention, emit, and dispose. Callers outside this lock are not synchronized.</summary>
        private readonly object _sync = new();

        /// <summary>Absolute directory that receives active and archived logs.</summary>
        private readonly string _directory;

        /// <summary>File name without extension, taken from the configured rolling path.</summary>
        private readonly string _filenamePrefix;

        /// <summary>Extension of the active file, including the leading dot.</summary>
        private readonly string _filenameSuffix;

        /// <summary>Matches one period and optional sequence in a file name under <see cref="_directory"/>.</summary>
        private readonly Regex _filenameMatcher;

        /// <summary>How <see cref="Checkpoint"/> and <see cref="NextCheckpoint"/> truncate the clock.</summary>
        private readonly RollingInterval _interval;

        /// <summary>Invariant format used for the period token. Empty when the interval is infinite.</summary>
        private readonly string _periodFormat;

        /// <summary>Formatter for the active file only.</summary>
        private readonly ITextFormatter _textFormatter;

        /// <summary>Active-file size cap. Null is unlimited.</summary>
        private readonly long? _fileSizeLimitBytes;

        /// <summary>Maximum retained period/sequence slots, including the active file. Null keeps every slot.</summary>
        private readonly int? _retainedFileCountLimit;

        /// <summary>Whether the active writer buffers until flush.</summary>
        private readonly bool _buffered;

        /// <summary>Whether a full active file rolls to the next sequence inside the same period.</summary>
        private readonly bool _rollOnFileSizeLimit;

        /// <summary>Text encoding passed to the active writer. Null selects UTF-8 without a BOM.</summary>
        private readonly Encoding? _encoding;

        /// <summary>Compression used when a file becomes inactive. The active path is never compressed.</summary>
        private readonly ILogFileCompressor _compressor;

        /// <summary>Called with the uncompressed path after it is closed and before compression. Exceptions are logged and do not skip compression.</summary>
        private readonly Action<string>? _onCompletedFile;

        /// <summary>Clock for roll checkpoints.</summary>
        private readonly TimeProvider _time;

        /// <summary>Serilog failure listener. Assigned under <see cref="_sync"/> and may be null.</summary>
        private ILoggingFailureListener? _failureListener;

        /// <summary>Open active file. Null before the first emit and after dispose.</summary>
        private ActiveLogFileWriter? _current;

        /// <summary>Path of <see cref="_current"/>. Compared ordinal-ignore-case so the active file is not archived or deleted.</summary>
        private string? _currentPath;

        /// <summary>Instant at which the current period ends. Null when the interval is infinite or no file is open.</summary>
        private DateTime? _nextCheckpoint;

        /// <summary>Size-roll sequence of the active file. Null is the unsequenced file for the period.</summary>
        private int? _currentSequence;

        /// <summary>True after the first <see cref="Dispose"/>. Later calls return immediately.</summary>
        private bool _disposed;

        /// <summary>Initializes a rolling gzip file sink.</summary>
        /// <param name="path">Serilog rolling path such as <c>logs/app-.log</c>.</param>
        /// <param name="textFormatter">Formatter for the active file. Not used to rewrite archived files.</param>
        /// <param name="fileSizeLimitBytes">Active-file size cap. <see langword="null"/> is unlimited.</param>
        /// <param name="retainedFileCountLimit">
        /// Maximum daily files including the active file. <see langword="null"/> keeps every day.
        /// A <c>.log</c> and <c>.log.gz</c> for the same period count as one file.
        /// </param>
        /// <param name="encoding">File encoding. <see langword="null"/> uses the File sink default.</param>
        /// <param name="buffered">Whether the active file buffers writes.</param>
        /// <param name="rollingInterval">How the path period is chosen.</param>
        /// <param name="rollOnFileSizeLimit">Whether a full active file rolls to the next sequence.</param>
        /// <param name="compressor">Compression used when a file becomes inactive. Defaults to gzip.</param>
        /// <param name="onCompletedFile">
        /// Invoked with the uncompressed path after it is closed and before compression.
        /// Exceptions are logged and do not skip compression.
        /// </param>
        /// <param name="timeProvider">Clock for roll checkpoints. Defaults to <see cref="TimeProvider.System"/>.</param>
        internal GzipRollingFileSink(
            string path,
            ITextFormatter textFormatter,
            long? fileSizeLimitBytes,
            int? retainedFileCountLimit,
            Encoding? encoding,
            bool buffered,
            RollingInterval rollingInterval,
            bool rollOnFileSizeLimit,
            ILogFileCompressor? compressor = null,
            Action<string>? onCompletedFile = null,
            TimeProvider? timeProvider = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(textFormatter);
            if (fileSizeLimitBytes is < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(fileSizeLimitBytes));
            }

            if (retainedFileCountLimit is < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(retainedFileCountLimit));
            }

            _textFormatter = textFormatter;
            _fileSizeLimitBytes = fileSizeLimitBytes;
            _retainedFileCountLimit = retainedFileCountLimit;
            _encoding = encoding;
            _buffered = buffered;
            _interval = rollingInterval;
            _rollOnFileSizeLimit = rollOnFileSizeLimit;
            _compressor = compressor ?? new GzipLogFileCompressor();
            _onCompletedFile = onCompletedFile;
            _time = timeProvider ?? TimeProvider.System;
            _periodFormat = PeriodFormat(rollingInterval);

            var pathDirectory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(pathDirectory))
            {
                pathDirectory = Directory.GetCurrentDirectory();
            }

            _directory = Path.GetFullPath(pathDirectory);
            _filenamePrefix = Path.GetFileNameWithoutExtension(path);
            _filenameSuffix = Path.GetExtension(path);
            if (string.IsNullOrEmpty(_filenamePrefix)
                || _filenamePrefix is "." or ".."
                || _filenamePrefix.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
                || _filenameSuffix.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            {
                throw new ArgumentException("The log path file name must be a single relative segment.", nameof(path));
            }
            _filenameMatcher = new Regex(
                "^" +
                Regex.Escape(_filenamePrefix) +
                "(?<period>\\d{" + _periodFormat.Length + "})" +
                "(?<sequence>_[0-9]{3,})?" +
                Regex.Escape(_filenameSuffix) +
                "(\\." + "gz" + ")?$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        /// <inheritdoc />
        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var now = _time.GetLocalNow().DateTime;
                Align(now, nextSequence: false);
                while (_current is { } file
                    && !file.TryEmit(logEvent)
                    && _rollOnFileSizeLimit)
                {
                    Align(now, nextSequence: true);
                }
            }
        }

        /// <inheritdoc />
        public void FlushToDisk()
        {
            lock (_sync)
            {
                _current?.FlushToDisk();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _current?.Dispose();
                _current = null;
                _currentPath = null;
                _disposed = true;
            }
        }

        /// <inheritdoc />
        public void SetFailureListener(ILoggingFailureListener failureListener)
        {
            ArgumentNullException.ThrowIfNull(failureListener);
            lock (_sync)
            {
                _failureListener = failureListener;
            }
        }

        /// <summary>
        /// Opens the file for <paramref name="now"/> when none is open, the period has elapsed, or <paramref name="nextSequence"/> requests the next size-roll file.
        /// </summary>
        /// <param name="now">Clock reading used for the period checkpoint.</param>
        /// <param name="nextSequence">When true, increments the sequence instead of waiting for the period boundary.</param>
        private void Align(DateTime now, bool nextSequence)
        {
            if (_current is null || nextSequence || (_nextCheckpoint.HasValue && now >= _nextCheckpoint.Value))
            {
                var sequence = _currentSequence;
                if (nextSequence)
                {
                    sequence = sequence is null ? 1 : sequence.Value + 1;
                }
                else if (_current is not null)
                {
                    sequence = null;
                }

                CloseCurrent();
                Open(now, sequence);
            }
        }

        /// <summary>Disposes the active writer and clears its path, checkpoint, and writer. Does not clear the sequence.</summary>
        private void CloseCurrent()
        {
            _current?.Dispose();
            _current = null;
            _currentPath = null;
            _nextCheckpoint = null;
        }

        /// <summary>
        /// Creates the directory, opens the active file for <paramref name="now"/> and <paramref name="sequence"/>, then archives inactive logs and applies retention.
        /// </summary>
        /// <param name="now">Clock reading truncated to the rolling interval.</param>
        /// <param name="sequence">Size-roll sequence, or null for the unsequenced period file.</param>
        private void Open(DateTime now, int? sequence)
        {
            Directory.CreateDirectory(_directory);
            var checkpoint = Checkpoint(now);
            _nextCheckpoint = NextCheckpoint(now);
            _currentSequence = sequence;
            _currentPath = LogPath(checkpoint, sequence);
            _current = new ActiveLogFileWriter(_currentPath, _textFormatter, _fileSizeLimitBytes, _encoding, _buffered);
            ArchiveInactive();
            ApplyRetention();
        }

        /// <summary>
        /// Deletes leftover <c>.gz.partial</c> files, compresses other <c>.log</c> files, and leaves the active path untouched.
        /// A <c>.log</c> whose <c>.gz</c> already exists is deleted instead of compressed again. Compression failure keeps the uncompressed file.
        /// </summary>
        private void ArchiveInactive()
        {
            if (Directory.Exists(_directory))
            {
                foreach (var partial in Directory.GetFiles(_directory, _filenamePrefix + "*.gz.partial"))
                {
                    TryDelete(partial);
                }
            }

            foreach (var path in MatchingFiles(".log"))
            {
                if (IsCurrent(path) || path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var gzip = path + ".gz";
                if (File.Exists(gzip))
                {
                    TryDelete(path);
                    continue;
                }

                try
                {
                    _onCompletedFile?.Invoke(path);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SelfLog.WriteLine("Completed-log handler failed for {0}: {1}", path, ex);
                }

                var compressed = false;
                try
                {
                    compressed = _compressor.TryCompressAndReplace(path);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SelfLog.WriteLine("Log compression threw for {0}: {1}", path, ex);
                }

                if (!compressed)
                {
                    Report($"log compression failed for {path}; the uncompressed file was kept");
                }
            }
        }

        /// <summary>
        /// Deletes files outside the newest <see cref="_retainedFileCountLimit"/> period/sequence slots.
        /// A <c>.log</c> and <c>.log.gz</c> for the same slot count as one. The active path is never deleted. A null limit keeps every slot.
        /// </summary>
        private void ApplyRetention()
        {
            if (_retainedFileCountLimit is null)
            {
                return;
            }

            var slots = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in MatchingFiles(".log").Concat(MatchingFiles(".log.gz")))
            {
                var name = Path.GetFileName(path);
                var match = _filenameMatcher.Match(name);
                if (!match.Success)
                {
                    continue;
                }

                var period = match.Groups["period"].Value;
                var sequenceText = match.Groups["sequence"].Value;
                var sequence = sequenceText.Length == 0
                    ? -1
                    : int.Parse(sequenceText[1..], CultureInfo.InvariantCulture);
                var key = period + "\n" + sequence.ToString(CultureInfo.InvariantCulture);
                if (!slots.TryGetValue(key, out var slot))
                {
                    slot = new Slot(period, sequence);
                    slots.Add(key, slot);
                }

                if (!slot.Paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    slot.Paths.Add(path);
                }
            }

            var ranked = slots.Values
                .OrderByDescending(static slot => slot.Period, StringComparer.Ordinal)
                .ThenByDescending(static slot => slot.Sequence)
                .ToList();
            var currentIndex = ranked.FindIndex(slot => slot.Paths.Exists(IsCurrent));
            if (currentIndex > 0)
            {
                var current = ranked[currentIndex];
                ranked.RemoveAt(currentIndex);
                ranked.Insert(0, current);
            }

            for (var index = _retainedFileCountLimit.Value; index < ranked.Count; index++)
            {
                foreach (var path in ranked[index].Paths.Where(candidate => !IsCurrent(candidate)))
                {
                    TryDelete(path);
                }
            }
        }

        /// <summary>Returns directory files whose names match the rolling pattern and end with <paramref name="suffix"/>.</summary>
        /// <param name="suffix">Extension filter such as <c>.log</c> or <c>.log.gz</c>.</param>
        /// <returns>An empty sequence when the directory does not exist.</returns>
        private IEnumerable<string> MatchingFiles(string suffix)
        {
            if (!Directory.Exists(_directory))
            {
                return [];
            }

            return Directory.GetFiles(_directory, _filenamePrefix + "*" + suffix)
                .Where(path => _filenameMatcher.IsMatch(Path.GetFileName(path)));
        }

        /// <summary>Builds the active path from the period token and an optional <c>_NNN</c> sequence.</summary>
        /// <param name="checkpoint">Period start. Null omits the period token.</param>
        /// <param name="sequence">Size-roll sequence formatted as three digits, or null when unsequenced.</param>
        /// <returns>Full path under <see cref="_directory"/>.</returns>
        private string LogPath(DateTime? checkpoint, int? sequence)
        {
            var token = checkpoint?.ToString(_periodFormat, CultureInfo.InvariantCulture) ?? string.Empty;
            if (sequence is not null)
            {
                token += "_" + sequence.Value.ToString("000", CultureInfo.InvariantCulture);
            }

            return Path.Join(_directory, _filenamePrefix + token + _filenameSuffix);
        }

        /// <summary>Returns whether <paramref name="path"/> is the open active file, compared ordinal-ignore-case.</summary>
        /// <param name="path">Candidate log path.</param>
        /// <returns><see langword="false"/> when no file is open.</returns>
        private bool IsCurrent(string path) =>
            _currentPath is not null && string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase);

        /// <summary>Truncates <paramref name="instant"/> to the start of the rolling period, preserving <see cref="DateTime.Kind"/>.</summary>
        /// <param name="instant">Clock reading.</param>
        /// <returns>Null when the interval is <see cref="RollingInterval.Infinite"/>.</returns>
        /// <exception cref="InvalidOperationException">The interval is not year, month, day, hour, minute, or infinite.</exception>
        internal DateTime? Checkpoint(DateTime instant) => _interval switch
        {
            RollingInterval.Infinite => null,
            RollingInterval.Year => new DateTime(instant.Year, 1, 1, 0, 0, 0, instant.Kind),
            RollingInterval.Month => new DateTime(instant.Year, instant.Month, 1, 0, 0, 0, instant.Kind),
            RollingInterval.Day => instant.Date,
            RollingInterval.Hour => new DateTime(instant.Year, instant.Month, instant.Day, instant.Hour, 0, 0, instant.Kind),
            RollingInterval.Minute => new DateTime(
                instant.Year,
                instant.Month,
                instant.Day,
                instant.Hour,
                instant.Minute,
                0,
                instant.Kind),
            _ => throw new InvalidOperationException($"Unsupported rolling interval {_interval}."),
        };

        /// <summary>Returns the exclusive end of the period that contains <paramref name="instant"/>.</summary>
        /// <param name="instant">Clock reading.</param>
        /// <returns>Null when the interval is <see cref="RollingInterval.Infinite"/>.</returns>
        /// <exception cref="InvalidOperationException">The interval is not year, month, day, hour, minute, or infinite.</exception>
        internal DateTime? NextCheckpoint(DateTime instant) => _interval switch
        {
            RollingInterval.Infinite => null,
            RollingInterval.Year => new DateTime(instant.Year, 1, 1, 0, 0, 0, instant.Kind).AddYears(1),
            RollingInterval.Month => new DateTime(instant.Year, instant.Month, 1, 0, 0, 0, instant.Kind).AddMonths(1),
            RollingInterval.Day => instant.Date.AddDays(1),
            RollingInterval.Hour => new DateTime(instant.Year, instant.Month, instant.Day, instant.Hour, 0, 0, instant.Kind).AddHours(1),
            RollingInterval.Minute => new DateTime(
                instant.Year,
                instant.Month,
                instant.Day,
                instant.Hour,
                instant.Minute,
                0,
                instant.Kind).AddMinutes(1),
            _ => throw new InvalidOperationException($"Unsupported rolling interval {_interval}."),
        };

        /// <summary>Writes <paramref name="message"/> to Serilog <c>SelfLog</c> and the failure listener as a permanent failure.</summary>
        /// <param name="message">Failure text. It is not an exception and is not thrown.</param>
        private void Report(string message)
        {
            SelfLog.WriteLine(message);
            _failureListener?.OnLoggingFailed(
                this,
                LoggingFailureKind.Permanent,
                message,
                events: null!,
                exception: null!);
        }

        /// <summary>
        /// Deletes <paramref name="path"/> when it exists. <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/> are written to <c>SelfLog</c> and not thrown.
        /// </summary>
        /// <param name="path">Log, archive, or partial path.</param>
        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SelfLog.WriteLine("Could not delete log file {0}: {1}", path, ex);
            }
        }

        /// <summary>Returns the invariant period format for <paramref name="interval"/>.</summary>
        /// <param name="interval">Configured rolling interval.</param>
        /// <returns>An empty string for <see cref="RollingInterval.Infinite"/>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> is not a supported rolling interval.</exception>
        private static string PeriodFormat(RollingInterval interval) => interval switch
        {
            RollingInterval.Infinite => string.Empty,
            RollingInterval.Year => "yyyy",
            RollingInterval.Month => "yyyyMM",
            RollingInterval.Day => "yyyyMMdd",
            RollingInterval.Hour => "yyyyMMddHH",
            RollingInterval.Minute => "yyyyMMddHHmm",
            _ => throw new ArgumentOutOfRangeException(nameof(interval)),
        };

        /// <summary>One retained period and sequence, with every <c>.log</c> and <c>.log.gz</c> path that belongs to it.</summary>
        /// <param name="period">Period token from the file name.</param>
        /// <param name="sequence">Size-roll sequence, or <c>-1</c> when the file name has no sequence.</param>
        private sealed class Slot(string period, int sequence)
        {
            /// <summary>Period token used to rank newer files first.</summary>
            internal string Period { get; } = period;

            /// <summary>Sequence used to rank files inside one period. <c>-1</c> is the unsequenced file.</summary>
            internal int Sequence { get; } = sequence;

            /// <summary>Paths in this slot. A log and its gzip archive share one slot.</summary>
            internal List<string> Paths { get; } = [];
        }
    }
}
