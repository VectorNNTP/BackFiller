namespace VectorNNTP.BackFiller.Tests.Fixtures
{
    /// <summary>
    /// Appends relative path segments and rejects a result that leaves the parent.
    /// </summary>
    internal static class RelativePath
    {
        /// <summary>
        /// Returns <paramref name="root"/> with each relative segment appended.
        /// </summary>
        /// <param name="root">Parent path. It remains the start of the returned path.</param>
        /// <param name="relativeSegments">Segments that must not be rooted.</param>
        /// <returns>The parent path with the relative segments appended.</returns>
        /// <exception cref="ArgumentException">
        /// A segment is empty or rooted, or the canonical result leaves <paramref name="root"/>.
        /// </exception>
        internal static string Under(string root, params string[] relativeSegments)
        {
            ArgumentException.ThrowIfNullOrEmpty(root);
            if (relativeSegments.Length == 0)
            {
                throw new ArgumentException("At least one relative segment is required.", nameof(relativeSegments));
            }

            var path = root;
            foreach (var segment in relativeSegments)
            {
                if (string.IsNullOrEmpty(segment) || Path.IsPathRooted(segment))
                {
                    throw new ArgumentException(
                        "Each path segment must be relative so it cannot replace the parent path.",
                        nameof(relativeSegments));
                }

                path = Path.Join(path, segment);
            }

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var fullRoot = Path.GetFullPath(root);
            var fullPath = Path.GetFullPath(path);
            if (string.Equals(fullRoot, fullPath, comparison))
            {
                throw new ArgumentException(
                    "Each path segment must stay inside the parent directory.",
                    nameof(relativeSegments));
            }

            var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) || fullRoot.EndsWith(Path.AltDirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, comparison))
            {
                throw new ArgumentException(
                    "Each path segment must stay inside the parent directory.",
                    nameof(relativeSegments));
            }

            return path;
        }
    }
}
