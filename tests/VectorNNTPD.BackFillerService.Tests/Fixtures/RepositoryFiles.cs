namespace VectorNNTPD.BackFillerService.Tests.Fixtures
{
    /// <summary>
    /// Locates repository files from the test output directory.
    /// </summary>
    internal static class RepositoryFiles
    {
        /// <summary>
        /// Walks parents of <see cref="AppContext.BaseDirectory"/> until <c>VectorNNTPD.slnx</c> is found.
        /// </summary>
        /// <returns>The repository root.</returns>
        internal static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Join(dir.FullName, "VectorNNTPD.slnx")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException(
                "VectorNNTPD.slnx was not found above " + AppContext.BaseDirectory);
        }

        /// <summary>Returns a path under the repository root.</summary>
        /// <param name="relativeSegments">Relative segments.</param>
        /// <returns>The combined path.</returns>
        internal static string Under(params string[] relativeSegments)
            => RelativePath.Under(Root(), relativeSegments);
    }
}
