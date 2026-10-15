using VectorNNTP.BackFiller.Hosting.Systemd;

namespace VectorNNTP.BackFiller.Tests.Hosting.Systemd
{
    public sealed class SystemdWatchdogIntervalTests
    {
        [Fact]
        public void Calculate_UsesConfiguredFraction_AndLeavesMargin()
        {
            var interval = SystemdWatchdogInterval.Calculate(TimeSpan.FromSeconds(10), fraction: 0.5);

            Assert.Equal(TimeSpan.FromSeconds(5), interval);
            Assert.True(interval < TimeSpan.FromSeconds(10));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(1)]
        [InlineData(1.5)]
        [InlineData(double.NaN)]
        public void Calculate_RejectsInvalidFraction(double fraction)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SystemdWatchdogInterval.Calculate(TimeSpan.FromSeconds(10), fraction));
        }

        [Fact]
        public void Calculate_RejectsNonPositiveTimeout()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SystemdWatchdogInterval.Calculate(TimeSpan.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SystemdWatchdogInterval.Calculate(TimeSpan.FromMilliseconds(-1)));
        }

        [Theory]
        [InlineData(0.001)]
        [InlineData(1)]
        public void Calculate_RejectsDeadlinesShorterThanTwoMilliseconds(double milliseconds)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SystemdWatchdogInterval.Calculate(TimeSpan.FromMilliseconds(milliseconds)));
        }

        [Fact]
        public void Calculate_RejectsOneTickBelowTheMinimumTimeout()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SystemdWatchdogInterval.Calculate(SystemdWatchdogInterval.MinimumTimeout - TimeSpan.FromTicks(1)));
        }

        [Fact]
        public void Calculate_MinimumTimeout_StaysOneMillisecondBeforeTheDeadline()
        {
            var interval = SystemdWatchdogInterval.Calculate(SystemdWatchdogInterval.MinimumTimeout, fraction: 0.99);

            Assert.Equal(TimeSpan.FromMilliseconds(1), interval);
            Assert.True(interval < SystemdWatchdogInterval.MinimumTimeout);
            Assert.Equal(TimeSpan.FromMilliseconds(1), SystemdWatchdogInterval.MinimumTimeout - interval);
        }

        [Fact]
        public void Calculate_RoundsFractionalTicks_AndKeepsTheMargin()
        {
            var timeout = TimeSpan.FromMilliseconds(3);
            var interval = SystemdWatchdogInterval.Calculate(timeout, fraction: 0.9);

            Assert.Equal(TimeSpan.FromMilliseconds(2), interval);
            Assert.True(timeout - interval >= TimeSpan.FromMilliseconds(1));
        }

        [Fact]
        public void Calculate_RaisesASubMillisecondFraction_ToOneMillisecond()
        {
            var timeout = TimeSpan.FromMilliseconds(10);
            var interval = SystemdWatchdogInterval.Calculate(timeout, fraction: 0.01);

            Assert.Equal(TimeSpan.FromMilliseconds(1), interval);
            Assert.True(interval < timeout);
        }
    }
}
