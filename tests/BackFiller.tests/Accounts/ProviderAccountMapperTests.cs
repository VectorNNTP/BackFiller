using VectorNNTP.BackFiller.Accounts;
using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Accounts
{
    /// <summary>
    /// Maps database rows to BackFillerProviderDefinition with expected defaults.
    /// </summary>

    public sealed class ProviderAccountMapperTests
    {
        /// <summary>
        /// Contract: Map projects a valid row onto the phase4 provider definition.
        /// </summary>

        [Fact]
        public void Map_projects_a_valid_row_onto_the_phase4_provider_definition()
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create()]);

            var provider = Assert.Single(mapped.Providers);
            Assert.Empty(mapped.Rejected);
            Assert.Equal("Giganews", provider.Backbone);
            Assert.Equal("news.example.test", provider.Host);
            Assert.Equal(563, provider.Port);
            Assert.True(provider.UseTls);
            Assert.Equal("nntp-user", provider.Username);
            Assert.Equal(ProviderAccountTestRows.SecretPassword, provider.Password);
            Assert.Equal(0, provider.MinSessions);
            Assert.Equal(4, provider.MaxSessions);
            Assert.Equal(0, provider.KeepAliveSeconds);
        }

        /// <summary>
        /// Contract: Map preserves mysql keepalive on the provider definition.
        /// </summary>

        [Fact]
        public void Map_preserves_mysql_keepalive_on_the_provider_definition()
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(keepAlive: 42)]);

            var provider = Assert.Single(mapped.Providers);
            Assert.Equal((byte)42, provider.KeepAliveSeconds);
            Assert.True(provider.DateKeepAliveEnabled);
        }

        /// <summary>
        /// Contract: Map canonicalizes backbone case.
        /// </summary>

        [Theory]
        [InlineData("giganews", "Giganews")]
        [InlineData(" Eweka ", "Eweka")]
        public void Map_canonicalizes_backbone_case(string raw, string canonical)
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(backbone: raw)]);

            Assert.Equal(canonical, Assert.Single(mapped.Providers).Backbone);
            Assert.Empty(mapped.Rejected);
        }

        /// <summary>
        /// Contract: Map rejects unknown backbone without publishing it.
        /// </summary>

        [Fact]
        public void Map_rejects_unknown_backbone_without_publishing_it()
        {
            var mapped = ProviderAccountMapper.Map(
            [
                ProviderAccountTestRows.Create(backbone: "UnknownProvider"),
                ProviderAccountTestRows.Create(backbone: "Eweka", hostname: "eweka.example.test"),
            ]);

            Assert.Equal("Eweka", Assert.Single(mapped.Providers).Backbone);
            var rejected = Assert.Single(mapped.Rejected);
            Assert.Equal("UnknownProvider", rejected.Backbone);
            Assert.Equal("Unknown or unsupported backbone.", rejected.Reason);
        }

        /// <summary>
        /// Contract: Map keeps the first valid row for a duplicate backbone.
        /// </summary>

        [Fact]
        public void Map_keeps_the_first_valid_row_for_a_duplicate_backbone()
        {
            var mapped = ProviderAccountMapper.Map(
            [
                ProviderAccountTestRows.Create(hostname: "first.example.test", maxConnections: 2),
                ProviderAccountTestRows.Create(hostname: "second.example.test", maxConnections: 8),
            ]);

            var provider = Assert.Single(mapped.Providers);
            Assert.Equal("first.example.test", provider.Host);
            Assert.Equal(2, provider.MaxSessions);
            Assert.Equal("Giganews", Assert.Single(mapped.Rejected).Backbone);
            Assert.Contains("Duplicate", Assert.Single(mapped.Rejected).Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// Contract: Map rejects missing hostname.
        /// </summary>

        [Theory]
        [InlineData("", "Hostname is required.")]
        [InlineData("   ", "Hostname is required.")]
        public void Map_rejects_missing_hostname(string hostname, string reason)
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(hostname: hostname)]);

            Assert.Empty(mapped.Providers);
            Assert.Equal(reason, Assert.Single(mapped.Rejected).Reason);
        }

        /// <summary>
        /// Contract: Map rejects invalid port.
        /// </summary>

        [Theory]
        [InlineData(0)]
        [InlineData(65536)]
        public void Map_rejects_invalid_port(int port)
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(port: port)]);

            Assert.Empty(mapped.Providers);
            Assert.Equal("Port must be between 1 and 65535.", Assert.Single(mapped.Rejected).Reason);
        }

        /// <summary>
        /// Contract: Map rejects max connections below one.
        /// </summary>

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Map_rejects_max_connections_below_one(int maxConnections)
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(maxConnections: maxConnections)]);

            Assert.Empty(mapped.Providers);
            Assert.Equal("MaxConnections must be at least 1.", Assert.Single(mapped.Rejected).Reason);
        }

        /// <summary>
        /// Contract: Map rejects missing username.
        /// </summary>

        [Fact]
        public void Map_rejects_missing_username()
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(username: " ")]);

            Assert.Empty(mapped.Providers);
            Assert.Equal("Username is required.", Assert.Single(mapped.Rejected).Reason);
        }

        /// <summary>
        /// Contract: Map rejects username with control characters.
        /// </summary>

        [Theory]
        [InlineData("user\rname")]
        [InlineData("user\nname")]
        [InlineData("user\0name")]
        [InlineData("user\x7Fname")]
        public void Map_rejects_username_with_control_characters(string username)
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(username: username)]);

            Assert.Empty(mapped.Providers);
            Assert.Equal("Username contains invalid control characters.", Assert.Single(mapped.Rejected).Reason);
        }

        /// <summary>
        /// Contract: Map rejects null password without including it in the reason.
        /// </summary>

        [Fact]
        public void Map_rejects_null_password_without_including_it_in_the_reason()
        {
            var mapped = ProviderAccountMapper.Map(
                [ProviderAccountTestRows.Create() with { Password = null! }]);

            Assert.Empty(mapped.Providers);
            Assert.Equal("Password is required.", Assert.Single(mapped.Rejected).Reason);
            Assert.DoesNotContain(
                ProviderAccountTestRows.SecretPassword,
                Assert.Single(mapped.Rejected).Reason,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Contract: Map rejects password with control characters.
        /// </summary>

        [Theory]
        [InlineData("pass\rword")]
        [InlineData("pass\nword")]
        [InlineData("pass\0word")]
        [InlineData("pass\x7Fword")]
        public void Map_rejects_password_with_control_characters(string password)
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(password: password)!]);

            Assert.Empty(mapped.Providers);
            Assert.Equal("Password contains invalid control characters.", Assert.Single(mapped.Rejected).Reason);
        }

        /// <summary>
        /// Contract: Map projects usessl onto tls.
        /// </summary>

        [Theory]
        [InlineData("y", true)]
        [InlineData("n", false)]
        public void Map_projects_usessl_onto_tls(string useSsl, bool useTls)
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(useSsl: useSsl, port: 119)]);

            Assert.Equal(useTls, Assert.Single(mapped.Providers).UseTls);
        }

        /// <summary>
        /// Contract: Map rejects invalid usessl.
        /// </summary>

        [Theory]
        [InlineData("Y")]
        [InlineData("yes")]
        [InlineData("1")]
        [InlineData("")]
        public void Map_rejects_invalid_usessl(string useSsl)
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(useSsl: useSsl)]);

            Assert.Empty(mapped.Providers);
            Assert.Equal("UseSsl must be 'y' or 'n'.", Assert.Single(mapped.Rejected).Reason);
        }

        /// <summary>
        /// Contract: Map allows an empty provider result.
        /// </summary>

        [Fact]
        public void Map_allows_an_empty_provider_result()
        {
            var mapped = ProviderAccountMapper.Map([]);

            Assert.Empty(mapped.Providers);
            Assert.Empty(mapped.Rejected);
        }

        /// <summary>
        /// Contract: Map rejects a blank backbone.
        /// </summary>

        [Fact]
        public void Map_rejects_a_blank_backbone()
        {
            var mapped = ProviderAccountMapper.Map([ProviderAccountTestRows.Create(backbone: " ")]);

            Assert.Empty(mapped.Providers);
            var rejected = Assert.Single(mapped.Rejected);
            Assert.Equal(" ", rejected.Backbone);
            Assert.Equal("Unknown or unsupported backbone.", rejected.Reason);
        }
    }
}
