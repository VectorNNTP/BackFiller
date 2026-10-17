namespace VectorNNTPD.BackFillerService.Configuration
{
    /// <summary>
    /// Resolves application-local runtime filesystem paths against the binary directory.
    /// </summary>
    /// <remarks>
    /// Relative paths resolve against <see cref="AppContext.BaseDirectory"/> (or an explicit
    /// application-base argument). The process working directory, IDE project folder, and
    /// host content root are never used as an implicit fallback.
    /// </remarks>
    internal static class ApplicationLocalPath
    {
        /// <summary>
        /// Resolves a configured application-local filesystem path.
        /// </summary>
        /// <param name="path">Configured path (relative or absolute). Null or whitespace is rejected.</param>
        /// <param name="applicationBaseDirectory">
        /// Application binary directory. Production callers must pass
        /// <see cref="AppContext.BaseDirectory"/>. When omitted or whitespace,
        /// <see cref="AppContext.BaseDirectory"/> is used. Do not pass an IDE/project content root.
        /// </param>
        /// <returns>
        /// A fully qualified path. A trailing separator is removed except on a path root.
        /// Rooted <paramref name="path"/> values stay rooted, including paths outside the application base.
        /// A relative path is rejected when its canonical form leaves that base.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="path"/> is null or whitespace, or when a relative path
        /// canonicalizes outside the application base.
        /// </exception>
        internal static string ResolveApplicationLocalPath(string path, string? applicationBaseDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var trimmed = path.Trim();
            var root = string.IsNullOrWhiteSpace(applicationBaseDirectory)
                ? AppContext.BaseDirectory
                : applicationBaseDirectory;
            var fullRoot = PreservePathRoot(Path.GetFullPath(root));
            var fullPath = PreservePathRoot(Path.GetFullPath(trimmed, fullRoot));
            if (!Path.IsPathRooted(trimmed) && !IsInsideDirectory(fullRoot, fullPath))
            {
                throw new ArgumentException(
                    "A relative path must resolve inside the application base directory.",
                    nameof(path));
            }

            return fullPath;
        }

        /// <summary>
        /// Returns whether <paramref name="candidate"/> is <paramref name="directory"/> or a descendant of it.
        /// </summary>
        /// <param name="directory">Canonical directory boundary.</param>
        /// <param name="candidate">Canonical path to test.</param>
        /// <returns><see langword="true"/> when the candidate is the directory or a path beneath it.</returns>
        private static bool IsInsideDirectory(string directory, string candidate)
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.Equals(directory, candidate, comparison))
            {
                return true;
            }

            var prefix = directory.EndsWith(Path.DirectorySeparatorChar) || directory.EndsWith(Path.AltDirectorySeparatorChar)
                ? directory
                : directory + Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, comparison);
        }

        /// <summary>Removes a trailing separator without turning a path root such as <c>C:\</c> into <c>C:</c>.</summary>
        /// <param name="path">Canonical path.</param>
        /// <returns>The path without a redundant trailing separator.</returns>
        private static string PreservePathRoot(string path)
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root)
                && string.Equals(
                    path,
                    root,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return path;
            }

            return Path.TrimEndingDirectorySeparator(path);
        }
    }
}
