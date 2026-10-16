namespace VectorNNTP.BackFiller.Tests.Fixtures
{
    /// <summary>
    /// Serializes tests that mutate the process-wide <c>Serilog.Log.Logger</c> static.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class SerilogCollection
    {
        /// <summary>Collection name passed to <see cref="CollectionDefinitionAttribute"/>.</summary>
        internal const string Name = "Serilog";
    }
}
