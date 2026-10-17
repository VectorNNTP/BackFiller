using System.Security.Cryptography;
using VectorNNTPD.Common.Tests.TestDoubles;
using VectorNNTPD.Common.Acme;
using VectorNNTPD.Common.Configuration;

namespace VectorNNTPD.Common.Tests.Acme
{
    /// <summary>
    /// Account and certificate persistence: PKCS#8 key reuse, registration that skips an existing account,
    /// and certificate replacement that does not replace the current PFX when the new material is invalid.
    /// </summary>
    public sealed class AcmeAccountAndCertificateTests
    {
        /// <summary>
        /// Saving an <see cref="AcmeAccountState"/> then loading it returns the same PKCS#8 DER private key,
        /// and that key is not PEM-encoded.
        /// </summary>
        [Fact]
        public void Account_SaveThenLoad_ReusesKey()
        {
            using var dir = new TempStateDir();
            var store = new AccountStore(dir.Path);
            Assert.Null(store.Load());

            using var rsa = RSA.Create(2048);
            var key = rsa.ExportPkcs8PrivateKey();
            store.Save(new AcmeAccountState(
                "https://acme.example/acme/acct/1",
                AcmeCloudflareOptions.DefaultAcmeDirectoryUrl,
                key));

            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.True(key.AsSpan().SequenceEqual(loaded.PrivateKeyDer));
            Assert.False(DerCrypto.LooksLikePem(loaded.PrivateKeyDer));
        }

        /// <summary>
        /// <see cref="AccountStore.EnsureRegistered"/> returns the already stored account URI and does not
        /// invoke the key generator or the register callback when the directory URL matches the saved account.
        /// </summary>
        [Fact]
        public void Account_EnsureRegistered_ReusesExistingWithoutCallingRegister()
        {
            using var dir = new TempStateDir();
            var store = new AccountStore(dir.Path);
            using var rsa = RSA.Create(2048);
            var key = rsa.ExportPkcs8PrivateKey();
            store.Save(new AcmeAccountState("https://acme.example/acme/acct/1", "https://dir.example/directory", key));

            var registerCalls = 0;
            var result = store.EnsureRegistered(
                "https://dir.example/directory",
                () => throw new InvalidOperationException("should not generate"),
                _ =>
                {
                    registerCalls++;
                    return ("x", "");
                });

            Assert.Equal(0, registerCalls);
            Assert.Equal("https://acme.example/acme/acct/1", result.AccountUri);
        }

        /// <summary>
        /// <see cref="AccountStore.EnsureRegistered"/> persists a newly registered account URI and key,
        /// and removes the pending account key file after success.
        /// </summary>
        [Fact]
        public void Account_EnsureRegistered_PersistsNewAccount()
        {
            using var dir = new TempStateDir();
            var store = new AccountStore(dir.Path);
            using var rsa = RSA.Create(2048);
            var key = rsa.ExportPkcs8PrivateKey();

            var result = store.EnsureRegistered(
                "https://dir.example/directory",
                () => key,
                _ => ("https://acme.example/acme/acct/9", "body"));

            Assert.Equal("https://acme.example/acme/acct/9", result.AccountUri);
            Assert.NotNull(store.Load());
            Assert.False(File.Exists(AcmePaths.AccountPendingKeyPath(dir.Path)));
        }

        /// <summary>
        /// A saved certificate reloads from a new <see cref="CertificateStore"/> with the same domain list,
        /// and that list does not include <see cref="CertificateIdentities.NewsHostname"/>.
        /// </summary>
        [Fact]
        public void Certificate_SaveLoad_SurvivesRestart()
        {
            using var dir = new TempStateDir();
            var names = CertificateIdentities.ForFqdn("backfiller01.usenet.ninja", includeNewsHostname: false);
            var store = new CertificateStore(dir.Path, "backfiller01.usenet.ninja", TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            var material = TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60));

            store.Save(material);
            var reloaded = new CertificateStore(dir.Path, "backfiller01.usenet.ninja", TestCertificateFactory.Password, names, TimeSpan.FromDays(30))
                .Load();
            Assert.NotNull(reloaded);
            Assert.Equal(names, reloaded.Domains);
            Assert.DoesNotContain(CertificateIdentities.NewsHostname, reloaded.Domains);
        }

        /// <summary>
        /// Saving certificate material whose PFX bytes do not load throws, and the previously saved PFX file is unchanged.
        /// </summary>
        [Fact]
        public void Certificate_FailedReplacement_PreservesCurrent()
        {
            using var dir = new TempStateDir();
            var names = CertificateIdentities.ForFqdn("backfiller01.usenet.ninja", includeNewsHostname: false);
            var store = new CertificateStore(dir.Path, "backfiller01.usenet.ninja", TestCertificateFactory.Password, names, TimeSpan.FromDays(30));
            var current = TestCertificateFactory.CreateMaterial(names, DateTimeOffset.UtcNow.AddDays(60));
            store.Save(current);
            var before = File.ReadAllBytes(store.Paths().PfxPath);

            var invalid = current with { PfxBytes = [0x00, 0x01] };
            Assert.ThrowsAny<Exception>(() => store.Save(invalid));

            var after = new CertificateStore(dir.Path, "backfiller01.usenet.ninja", TestCertificateFactory.Password, names, TimeSpan.FromDays(30))
                .Load();
            Assert.NotNull(after);
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(store.Paths().PfxPath)));
        }

    }
}
