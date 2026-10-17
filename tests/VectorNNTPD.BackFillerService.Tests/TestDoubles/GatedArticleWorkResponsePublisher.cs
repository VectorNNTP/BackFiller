using VectorNNTPD.BackFillerService.ArticleWork;

namespace VectorNNTPD.BackFillerService.Tests.TestDoubles
{
    /// <summary>
    /// <see cref="IArticleWorkResponsePublisher"/> double with explicit gates for publish and post-confirm holds.
    /// Records intents in memory; does not talk to RabbitMQ. Tests drive timing via <see cref="Gate"/> and <see cref="AfterConfirmHold"/>.
    /// </summary>
    internal sealed class GatedArticleWorkResponsePublisher : IArticleWorkResponsePublisher
    {
        /// <summary>Recorded response intents in publish order.</summary>
        private readonly List<ArticleWorkResponseIntent> _published = [];

        /// <summary>When false, the publisher still records intents but simulates a non-completing success path for shutdown tests.</summary>
        public bool CompletesSuccessPublication { get; set; } = true;

        /// <summary>Completes when <see cref="PublishAsync"/> enters, before optional gating.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>When set, <see cref="PublishAsync"/> waits on this task (honors cancellation) before recording or throwing.</summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>When set, awaited after the intent is recorded to hold the caller through confirm/shutdown races.</summary>
        public TaskCompletionSource? AfterConfirmHold { get; set; }

        /// <summary>When set, thrown after gates and before recording the intent.</summary>
        public Exception? PublishException { get; set; }

        /// <summary>Captured intents in publish order (thread-safe snapshot).</summary>
        internal IReadOnlyList<ArticleWorkResponseIntent> Published
        {
            get
            {
                lock (_published)
                {
                    return [.. _published];
                }
            }
        }

        /// <inheritdoc />
        public async Task PublishAsync(ArticleWorkResponseIntent intent, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(intent);
            cancellationToken.ThrowIfCancellationRequested();
            Started.TrySetResult();
            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (PublishException is not null)
            {
                throw PublishException;
            }

            lock (_published)
            {
                _published.Add(intent);
            }

            if (AfterConfirmHold is not null)
            {
                await AfterConfirmHold.Task.ConfigureAwait(false);
            }
        }
    }
}
