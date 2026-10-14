namespace VectorNNTP.BackFiller.Tests.Fixtures
{
    public sealed class RelativePathTests
    {
        [Fact]
        public void Under_appends_relative_segments_beneath_the_parent()
        {
            var root = Directory.CreateTempSubdirectory("bf-rel-").FullName;
            try
            {
                var child = RelativePath.Under(root, "src", "BackFiller", "Nntp");

                Assert.Equal(Path.Join(root, "src", "BackFiller", "Nntp"), child);
                Assert.StartsWith(root, child, StringComparison.Ordinal);
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
    }
}
