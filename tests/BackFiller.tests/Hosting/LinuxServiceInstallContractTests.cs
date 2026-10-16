using VectorNNTP.BackFiller.Tests.Fixtures;

namespace VectorNNTP.BackFiller.Tests.Hosting
{
    /// <summary>
    /// Checks the documented install procedure against the shipped unit.
    /// This does not start systemd.
    /// </summary>
    public sealed class LinuxServiceInstallContractTests
    {
        /// <summary>README install steps and the shipped unit file agree on account, paths, permissions, and systemd enablement without world-writable modes.</summary>
        [Fact]
        public void Readme_creates_the_service_account_and_limits_write_access_to_logs_and_certs()
        {
            var readme = File.ReadAllText(RepositoryFiles.Under("README.md"));
            var unit = File.ReadAllText(RepositoryFiles.Under("deploy", "systemd", "vectornntp-backfiller.service"));

            Assert.Contains("groupadd --system vectornntp", readme, StringComparison.Ordinal);
            Assert.Contains("useradd --system --gid vectornntp", readme, StringComparison.Ordinal);
            Assert.Contains("/opt/vectornntp/backfiller/logs", readme, StringComparison.Ordinal);
            Assert.Contains("/opt/vectornntp/backfiller/certs", readme, StringComparison.Ordinal);
            Assert.Contains("-o vectornntp -g vectornntp -m 0750", readme, StringComparison.Ordinal);
            Assert.Contains("-o root -g root -m 0755 VectorNNTP.BackFiller", readme, StringComparison.Ordinal);
            Assert.Contains("-m 0644 VectorNNTP.BackFiller.json", readme, StringComparison.Ordinal);
            Assert.Contains("/etc/systemd/system/vectornntp-backfiller.service", readme, StringComparison.Ordinal);
            Assert.Contains("systemctl daemon-reload", readme, StringComparison.Ordinal);
            Assert.Contains("systemctl enable --now", readme, StringComparison.Ordinal);
            Assert.Contains("do not use mode `0777`", readme, StringComparison.Ordinal);
            Assert.DoesNotContain("chmod 0777", readme, StringComparison.Ordinal);
            Assert.DoesNotContain("chmod -R", readme, StringComparison.Ordinal);
            Assert.Contains("does not change Unix ownership", readme, StringComparison.Ordinal);

            Assert.Contains("User=vectornntp", unit, StringComparison.Ordinal);
            Assert.Contains("Group=vectornntp", unit, StringComparison.Ordinal);
            Assert.Contains("WorkingDirectory=/opt/vectornntp/backfiller", unit, StringComparison.Ordinal);
            Assert.Contains("ExecStart=/opt/vectornntp/backfiller/VectorNNTP.BackFiller", unit, StringComparison.Ordinal);
            Assert.Contains(
                "ReadWritePaths=/opt/vectornntp/backfiller/logs /opt/vectornntp/backfiller/certs",
                unit,
                StringComparison.Ordinal);
            Assert.DoesNotContain("ReadWritePaths=/opt/vectornntp/backfiller\n", unit, StringComparison.Ordinal);
        }
    }
}
