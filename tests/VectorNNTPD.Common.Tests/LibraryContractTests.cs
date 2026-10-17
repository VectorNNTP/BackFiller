using VectorNNTPD.Common.Hosting;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Tests
{
    /// <summary>
    /// Locks the VectorNNTPD.Common assembly identity: a class library with no entry point and no reference to the NNTPD or BackFiller hosts.
    /// </summary>
    public sealed class LibraryContractTests
    {
        /// <summary>
        /// Asserts that the assembly containing <see cref="AcmeCloudflareOptions"/> is named VectorNNTPD.Common, exposes no entry point, and does not reference VectorNNTP.NNTPD or VectorNNTPD.BackFillerService.
        /// </summary>
        [Fact]
        public void Common_IsAClassLibrary_WithNoEntryPoint()
        {
            var assembly = typeof(AcmeCloudflareOptions).Assembly;
            Assert.Equal("VectorNNTPD.Common", assembly.GetName().Name);
            Assert.Null(assembly.EntryPoint);
            Assert.DoesNotContain(
                assembly.GetReferencedAssemblies().Select(static name => name.Name),
                static name => name is "VectorNNTP.NNTPD" or "VectorNNTPD.BackFillerService");
        }

        /// <summary>
        /// Asserts that the assembly containing <see cref="AcmeCloudflareServiceCollectionExtensions"/> does not reference VectorNNTP.NNTPD or VectorNNTPD.BackFillerService.
        /// </summary>
        [Fact]
        public void Common_DoesNotReferenceApplicationHosts()
        {
            var referenced = typeof(AcmeCloudflareServiceCollectionExtensions).Assembly
                .GetReferencedAssemblies()
                .Select(static name => name.Name);
            Assert.DoesNotContain("VectorNNTP.NNTPD", referenced);
            Assert.DoesNotContain("VectorNNTPD.BackFillerService", referenced);
        }
    }
}
