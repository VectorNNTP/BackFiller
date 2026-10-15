using System.Diagnostics;
using System.IO.Hashing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using VectorNNTP.BackFiller.Articles;
using VectorNNTP.BackFiller.Articles.Parsing;
using VectorNNTP.BackFiller.Articles.Processing;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Articles
{
    /// <summary>
    /// One-pass measurement of the local <c>.artifacts/articles</c> corpus.
    /// The corpus is raw stored articles. This bench does not frame them as NNTP and does not
    /// claim provider or RabbitMQ throughput. Timings are not assertions.
    /// </summary>
    public sealed class ArticleCorpusBench
    {
        [Fact]
        public void Measure_local_article_corpus()
        {
            var corpus = RepositoryFiles.Under(".artifacts", "articles");
            if (!Directory.Exists(corpus))
            {
                Console.WriteLine("corpus bench not run: .artifacts/articles is absent from this workspace");
                return;
            }

            var files = Directory.EnumerateFiles(corpus, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var parser = new NntpArticleParser("backfiller.test");
            const int warmupFiles = 32;
            for (var i = 0; i < Math.Min(warmupFiles, files.Length); i++)
            {
                Process(parser, File.ReadAllBytes(files[i]), out _, out _, out _, out _, out _, out _);
            }

            // Labeled measurement boundary. The corpus report records gcCollectBeforeMeasuredPass=true.
            // Removing it would change the published allocation baseline, so the calls stay in this diagnostic fact.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var sizes = new List<long>(files.Length);
            var samples = new List<long>(files.Length);
            var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
            var rejectedPaths = new List<string>();
            long inputBytes = 0;
            long outputBytes = 0;
            long validExtraDots = 0;
            long dotLines = 0;
            var dotArticles = 0;
            var valid = 0;
            var invalid = 0;
            var unsupported = 0;
            long parseTicks = 0;
            long materializeTicks = 0;
            long hashTicks = 0;
            var contentHashes = new HashSet<ulong>();
            var duplicateContent = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var total = Stopwatch.StartNew();

            for (var i = 0; i < files.Length; i++)
            {
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(files[i]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unsupported++;
                    var readReason = "read " + ex.GetType().Name;
                    Add(reasons, readReason);
                    rejectedPaths.Add(readReason + " " + Path.GetRelativePath(corpus, files[i]));
                    continue;
                }

                sizes.Add(bytes.Length);
                inputBytes += bytes.Length;
                if (!names.Add(Path.GetFileName(files[i])))
                {
                    Add(reasons, "duplicate filename");
                }

                if (!contentHashes.Add(XxHash3.HashToUInt64(bytes)))
                {
                    duplicateContent++;
                }

                if (bytes.Length == 0)
                {
                    unsupported++;
                    Add(reasons, "empty file");
                    rejectedPaths.Add("empty file " + Path.GetRelativePath(corpus, files[i]));
                    continue;
                }

                var outcome = Process(
                    parser,
                    bytes,
                    out var parseElapsed,
                    out var materializeElapsed,
                    out var hashElapsed,
                    out var canonicalBytes,
                    out var stuffedDots,
                    out var reason);
                samples.Add(parseElapsed + materializeElapsed + hashElapsed);
                parseTicks += parseElapsed;
                materializeTicks += materializeElapsed;
                hashTicks += hashElapsed;
                if (outcome == CorpusOutcome.Valid)
                {
                    valid++;
                    outputBytes += canonicalBytes;
                    validExtraDots += stuffedDots;
                }
                else if (outcome == CorpusOutcome.Unsupported)
                {
                    unsupported++;
                    Add(reasons, reason);
                    rejectedPaths.Add(reason + " " + Path.GetRelativePath(corpus, files[i]));
                }
                else
                {
                    invalid++;
                    Add(reasons, reason);
                    rejectedPaths.Add(reason + " " + Path.GetRelativePath(corpus, files[i]));
                }

                if (stuffedDots > 0)
                {
                    dotArticles++;
                    dotLines += stuffedDots;
                }
            }

            total.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            var measured = samples.ToArray();
            var sizeArray = sizes.ToArray();
            Array.Sort(sizeArray);
            Array.Sort(measured);
            var processingTicks = parseTicks + materializeTicks + hashTicks;
            var processingSeconds = TicksToSeconds(processingTicks);
            var seconds = total.Elapsed.TotalSeconds;
            var report = new StringBuilder();
            report.AppendLine("article corpus bench");
            report.AppendLine("corpus=" + corpus);
            report.AppendLine("representation=raw stored articles (headers and body). Not NNTP-framed, so this pass does not destuff. Materialize writes the retained wire image, including body dot-stuffing.");
            report.AppendLine("pipeline=NntpArticleParser.Parse + NntpArticleCanonicalMaterializer.Materialize(Traverse) + ArticleFieldTable.Locate + ArticleId + XXH3-64. yEnc validation runs inside Parse when a section is detected.");
            report.AppendLine("not measured=live provider, RabbitMQ, retention admission");
            report.AppendLine($"runtime={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription} arch={RuntimeInformation.ProcessArchitecture} cpus={Environment.ProcessorCount} processor={Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}");
            report.AppendLine($"optimizerDisabled={typeof(ArticleRecordFactory).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled} config={typeof(ArticleRecordFactory).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration}");
            report.AppendLine($"warmupFiles={Math.Min(warmupFiles, files.Length)} measuredPasses=1 gcCollectBeforeMeasuredPass=true");
            report.AppendLine($"files={files.Length} uniqueNames={names.Count} duplicateContentXxh3={duplicateContent}");
            report.AppendLine($"valid={valid} invalid={invalid} unsupported={unsupported}");
            report.AppendLine($"inputBytes={inputBytes} outputBytes={outputBytes} validStuffingExtraDots={validExtraDots}");
            report.AppendLine($"dotArticles={dotArticles} dotArticleProportion={(files.Length == 0 ? 0 : (double)dotArticles / files.Length):R} dotLeadingBodyLines={dotLines}");
            report.AppendLine($"sizeBytes min={At(sizeArray, 0)} median={At(sizeArray, 0.50)} p90={At(sizeArray, 0.90)} p95={At(sizeArray, 0.95)} p99={At(sizeArray, 0.99)} max={At(sizeArray, 1)}");
            report.AppendLine($"wallSeconds={seconds:R} processingSeconds={processingSeconds:R} articlesPerSecond={(processingSeconds <= 0 ? 0 : samples.Count / processingSeconds):R} inputMiBPerSecond={(processingSeconds <= 0 ? 0 : inputBytes / 1024d / 1024d / processingSeconds):R}");
            report.AppendLine($"perArticleMs min={Ms(At(measured, 0)):R} median={Ms(At(measured, 0.50)):R} p90={Ms(At(measured, 0.90)):R} p95={Ms(At(measured, 0.95)):R} p99={Ms(At(measured, 0.99)):R} max={Ms(At(measured, 1)):R} samples={measured.Length}");
            report.AppendLine($"stageSeconds parse={TicksToSeconds(parseTicks):R} materialize={TicksToSeconds(materializeTicks):R} hashAndId={TicksToSeconds(hashTicks):R}");
            report.AppendLine($"allocatedBytes={allocated} allocatedBytesPerArticle={(files.Length == 0 ? 0 : allocated / (double)files.Length):R} allocatedBytesPerInputMiB={(inputBytes == 0 ? 0 : allocated / (inputBytes / 1024d / 1024d)):R}");
            report.AppendLine("allocatedBytes includes each file's input array plus canonical output and parser scratch. One file is resident at a time.");
            report.AppendLine("p99 is an order statistic of the per-article samples in this single pass, not a confidence interval across runs.");
            foreach (var pair in reasons.OrderByDescending(pair => pair.Value))
            {
                report.AppendLine($"reason {pair.Value} {pair.Key}");
            }

            foreach (var rejected in rejectedPaths)
            {
                report.AppendLine("rejected " + rejected);
            }

            var text = report.ToString();
            var reportPath = RepositoryFiles.Under(".artifacts", "article-corpus-bench-2026-10-15.txt");
            File.WriteAllText(reportPath, text);
            Console.WriteLine(text);
            Assert.Equal(files.Length, valid + invalid + unsupported);
        }

        private static CorpusOutcome Process(
            NntpArticleParser parser,
            byte[] bytes,
            out long parseElapsed,
            out long materializeElapsed,
            out long hashElapsed,
            out int canonicalBytes,
            out int stuffedDots,
            out string reason)
        {
            parseElapsed = 0;
            materializeElapsed = 0;
            hashElapsed = 0;
            canonicalBytes = 0;
            stuffedDots = 0;
            reason = "";
            try
            {
                var parseStart = Stopwatch.GetTimestamp();
                var parsed = parser.Parse(bytes);
                parseElapsed = Stopwatch.GetTimestamp() - parseStart;
                if (parsed.BodyBytes.Length > 0)
                {
                    stuffedDots = ArticleBodyWireStuffer.CountExtraDots(parsed.BodyBytes.Span);
                }

                if (!parsed.IsAccepted)
                {
                    reason = "parse " + parsed.FailureCode;
                    return CorpusOutcome.Invalid;
                }

                var materializeStart = Stopwatch.GetTimestamp();
                var materialized = NntpArticleCanonicalMaterializer.Materialize(
                    in parsed,
                    ArticlePathMode.Traverse);
                materializeElapsed = Stopwatch.GetTimestamp() - materializeStart;
                if (!materialized.IsAccepted || materialized.ArticleBytes is not { } artData)
                {
                    reason = "materialize " + materialized.FailureCode;
                    return CorpusOutcome.Invalid;
                }

                var hashStart = Stopwatch.GetTimestamp();
                var fields = ArticleFieldTable.Locate(artData, parsed.SelectedDateHeaderName);
                _ = ArticleId.FromMessageId(fields.MessageId.Slice(artData));
                _ = XxHash3.HashToUInt64(artData.AsSpan(0, materialized.CanonicalLength));
                hashElapsed = Stopwatch.GetTimestamp() - hashStart;
                canonicalBytes = materialized.CanonicalLength;
                return CorpusOutcome.Valid;
            }
            // codeql[cs/catch-of-all-exceptions] One article must be recorded as invalid so the corpus pass continues.
            catch (Exception ex)
            {
                reason = ex.GetType().Name + ": " + ex.Message;
                return CorpusOutcome.Invalid;
            }
        }

        private static void Add(Dictionary<string, int> reasons, string reason)
        {
            reasons.TryGetValue(reason, out var count);
            reasons[reason] = count + 1;
        }

        private static long At(long[] sorted, double fraction)
        {
            if (sorted.Length == 0)
            {
                return 0;
            }

            if (fraction >= 1)
            {
                return sorted[^1];
            }

            var index = (int)Math.Ceiling(sorted.Length * fraction) - 1;
            if (index < 0)
            {
                index = 0;
            }

            return sorted[index];
        }

        private static double Ms(long ticks)
            => ticks * 1000d / Stopwatch.Frequency;

        private static double TicksToSeconds(long ticks)
            => ticks / (double)Stopwatch.Frequency;

        private enum CorpusOutcome
        {
            Valid,
            Invalid,
            Unsupported,
        }
    }
}
