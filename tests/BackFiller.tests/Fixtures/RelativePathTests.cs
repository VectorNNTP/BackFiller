namespace VectorNNTP.BackFiller.Tests.Fixtures
{
    /// <summary>
    /// RelativePath.Under containment and rejection of rooted or traversing segments.
    /// </summary>

    public sealed class RelativePathTests
    {
        /// <summary>
        /// Contract: Under appends relative segments beneath the parent.
        /// </summary>

        [Fact]
        public void Under_appends_relative_segments_beneath_the_parent()
        {
            var root = Directory.CreateTempSubdirectory("bf-rel-").FullName;
            try
            {
                var child = RelativePath.Under(root, "src", "BackFiller", "Nntp");

                Assert.Equal(Path.Join(root, "src", "BackFiller", "Nntp"), child);
                Assert.StartsWith(root + Path.DirectorySeparatorChar, child, StringComparison.Ordinal);
                Assert.EndsWith(
                    Path.Join("src", "BackFiller", "Nntp"),
                    child,
                    StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(root);
            }
        }

        /// <summary>
        /// Invalid path or argument is rejected before resolution proceeds.
        /// </summary>

        [Fact]
        public void Under_rejects_a_rooted_segment_instead_of_discarding_the_parent()
        {
            var root = Directory.CreateTempSubdirectory("bf-rel-rooted-").FullName;
            try
            {
                var rooted = Path.Join(Path.GetPathRoot(root)!, "not-a-child");
                Assert.True(Path.IsPathRooted(rooted));

                var error = Assert.Throws<ArgumentException>(() => RelativePath.Under(root, "src", rooted));

                Assert.Equal("relativeSegments", error.ParamName);
            }
            finally
            {
                Directory.Delete(root);
            }
        }

        /// <summary>
        /// Invalid path or argument is rejected before resolution proceeds.
        /// </summary>

        [Fact]
        public void Under_rejects_parent_segment_traversal()
        {
            var root = Directory.CreateTempSubdirectory("bf-rel-dotdot-").FullName;
            try
            {
                var error = Assert.Throws<ArgumentException>(() => RelativePath.Under(root, ".."));
                Assert.Equal("relativeSegments", error.ParamName);
                Assert.Throws<ArgumentException>(() => RelativePath.Under(root, "child", "..", ".."));
            }
            finally
            {
                Directory.Delete(root);
            }
        }

        /// <summary>
        /// Invalid path or argument is rejected before resolution proceeds.
        /// </summary>

        [Fact]
        public void Under_rejects_a_sibling_prefix_escape()
        {
            var parent = Directory.CreateTempSubdirectory("bf-rel-prefix-").FullName;
            var root = Directory.CreateDirectory(Path.Join(parent, "bf")).FullName;
            try
            {
                Assert.Throws<ArgumentException>(() => RelativePath.Under(root, "..", "bf-extra"));
            }
            finally
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }
}
