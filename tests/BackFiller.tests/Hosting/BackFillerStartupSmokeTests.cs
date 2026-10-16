using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using VectorNNTP.BackFiller.Hosting;
using VectorNNTP.BackFiller.Logging;

namespace VectorNNTP.BackFiller.Tests.Hosting
{
    /// <summary>Regresses BackFillerStartupSmoke gating, isolated configuration, and Native AOT publish analysis settings.</summary>
    public sealed class BackFillerStartupSmokeTests
    {
        /// <summary>IsRequested is false for null, empty, and values that are not the exact enabled token.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("true")]
        [InlineData("0")]
        public void Smoke_is_requested_only_for_the_exact_enabled_value(string? value)
        {
            Assert.False(BackFillerStartupSmoke.IsRequested(value));
        }
        /// <summary>IsRequested is true only when the value equals BackFillerStartupSmoke.EnabledValue.</summary>

        [Fact]
        public void Smoke_is_requested_for_the_enabled_value()
        {
            Assert.True(BackFillerStartupSmoke.IsRequested(BackFillerStartupSmoke.EnabledValue));
        }
        /// <summary>Isolated smoke binds fixture configuration and Complete returns 0 with the checkpoint message.</summary>

        [Fact]
        public void Isolated_configuration_binds_and_writes_the_checkpoint()
        {
            var fixture = FindFixture();
            var builder = Host.CreateApplicationBuilder([]);
            Environment.SetEnvironmentVariable(BackFillerStartupSmoke.ConfigurationPathVariable, fixture);
            try
            {
                BackFillerStartupSmoke.UseIsolatedConfiguration(builder.Configuration);
            }
            finally
            {
                Environment.SetEnvironmentVariable(BackFillerStartupSmoke.ConfigurationPathVariable, null);
            }

            builder.ConfigureBackFillerLogging();
            builder.ConfigureBackFillerPlatformHosting();
            builder.AddBackFillerHosting();
            using var host = builder.Build();
            using var output = new StringWriter();

            var code = BackFillerStartupSmoke.Complete(host, output);

            Assert.Equal(0, code);
            Assert.Contains(
                "BackFiller startup smoke: configuration serverId=1 fqdn=backfiller01.usenet.ninja",
                output.ToString(),
                StringComparison.Ordinal);
        }
        /// <summary>UseIsolatedConfiguration throws FileNotFoundException for a missing path before host construction.</summary>

        [Fact]
        public void Missing_smoke_configuration_file_fails_before_host_construction()
        {
            var builder = Host.CreateApplicationBuilder([]);
            var missing = Path.Join(Path.GetTempPath(), "bf-missing-smoke-" + Guid.NewGuid().ToString("N") + ".json");
            Environment.SetEnvironmentVariable(BackFillerStartupSmoke.ConfigurationPathVariable, missing);
            try
            {
                var error = Assert.Throws<FileNotFoundException>(
                    () => BackFillerStartupSmoke.UseIsolatedConfiguration(builder.Configuration));
                Assert.Equal(missing, error.FileName);
            }
            finally
            {
                Environment.SetEnvironmentVariable(BackFillerStartupSmoke.ConfigurationPathVariable, null);
            }
        }
        /// <summary>win-x64 Release publish enables AOT without suppressing IL analysis; unsupported RID fails the guard target.</summary>

        [Fact]
        public void Native_aot_publish_keeps_trim_analysis_enabled()
        {
            var project = FindProjectFile();
            var publishAot = Evaluate(project, "PublishAot");
            var selfContained = Evaluate(project, "SelfContained");
            Assert.True(string.IsNullOrEmpty(publishAot));
            Assert.Equal("false", selfContained, StringComparer.OrdinalIgnoreCase);

            var aotPublish = Evaluate(project, "PublishAot", "win-x64");
            var aotSelfContained = Evaluate(project, "SelfContained", "win-x64");
            var suppress = Evaluate(project, "SuppressAotAnalysisWarnings", "win-x64");
            var noWarn = Evaluate(project, "NoWarn", "win-x64");
            var linkErrors = Evaluate(project, "ILLinkTreatWarningsAsErrors", "win-x64");
            var ilcErrors = Evaluate(project, "IlcTreatWarningsAsErrors", "win-x64");
            Assert.Equal("true", aotPublish, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("true", aotSelfContained, StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual("true", suppress, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("IL2026", noWarn, StringComparison.Ordinal);
            Assert.DoesNotContain("IL3050", noWarn, StringComparison.Ordinal);
            Assert.DoesNotContain("IL3000", noWarn, StringComparison.Ordinal);
            Assert.NotEqual("false", linkErrors, StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual("false", ilcErrors, StringComparer.OrdinalIgnoreCase);

            var testProject = Path.Join(Path.GetDirectoryName(FindFixture())!, "..", "BackFiller.Tests.csproj");
            var testPublishAot = Evaluate(Path.GetFullPath(testProject), "PublishAot", "win-x64");
            Assert.True(string.IsNullOrEmpty(testPublishAot));

            var rejected = Run(
                "dotnet",
                [
                    "msbuild",
                    project,
                    "-nologo",
                    "-t:RejectUnsupportedProductionRuntimeIdentifier",
                    "-p:Platform=x64",
                    "-p:Configuration=Release",
                    "-p:RuntimeIdentifier=osx-arm64",
                ]);
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains("win-x64 and linux-x64", rejected.Output, StringComparison.Ordinal);
        }

        /// <summary>Runs msbuild -getProperty for a Release x64 project evaluation.</summary>
        private static string Evaluate(string project, string property, string? runtimeIdentifier = null)
        {
            var arguments = new List<string>
            {
                "msbuild",
                project,
                "-nologo",
                "-getProperty:" + property,
                "-p:Platform=x64",
                "-p:Configuration=Release",
            };
            if (!string.IsNullOrEmpty(runtimeIdentifier))
            {
                arguments.Add("-p:RuntimeIdentifier=" + runtimeIdentifier);
            }

            var result = Run("dotnet", arguments);
            Assert.True(result.ExitCode == 0, result.Output);
            return result.Output.Trim();
        }

        /// <summary>Runs a process from the repository root and captures combined stdout/stderr.</summary>
        private static (int ExitCode, string Output) Run(string fileName, IReadOnlyList<string> arguments)
        {
            using var process = new Process();
            process.StartInfo.FileName = fileName;
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.WorkingDirectory = FindRepositoryRoot();
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            Assert.True(process.Start());
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)));
            return (process.ExitCode, stdout + stderr);
        }

        /// <summary>Locates startup-smoke.json under tests/BackFiller.tests/Fixtures.</summary>
        private static string FindFixture()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Join(
                    directory.FullName,
                    "tests",
                    "BackFiller.tests",
                    "Fixtures",
                    "startup-smoke.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("startup-smoke.json was not found.");
        }

        /// <summary>Locates src/BackFiller/BackFiller.csproj from the test base directory.</summary>
        private static string FindProjectFile()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Join(directory.FullName, "src", "BackFiller", "BackFiller.csproj");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("BackFiller.csproj was not found.");
        }

        /// <summary>Repository root (parent of src) from BackFiller.csproj location.</summary>
        private static string FindRepositoryRoot() =>
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(FindProjectFile())))!;
    }
}
