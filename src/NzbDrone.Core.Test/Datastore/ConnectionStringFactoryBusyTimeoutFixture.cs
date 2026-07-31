using System;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Test.Datastore
{
    [TestFixture]
    public class ConnectionStringFactoryBusyTimeoutFixture
    {
        private string _previous;

        [SetUp]
        public void SetUp()
        {
            _previous = Environment.GetEnvironmentVariable("SQLITE_BUSY_TIMEOUT");
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("SQLITE_BUSY_TIMEOUT", _previous);
        }

        [Test]
        public void busy_timeout_defaults_to_1000_and_honors_env_and_clamps_below_100()
        {
            // Default when unset.
            Environment.SetEnvironmentVariable("SQLITE_BUSY_TIMEOUT", null);
            ConnectionStringFactory.GetBusyTimeout().Should().Be(1000);

            // Honors a valid override.
            Environment.SetEnvironmentVariable("SQLITE_BUSY_TIMEOUT", "5000");
            ConnectionStringFactory.GetBusyTimeout().Should().Be(5000);

            // Clamps anything below the 100 ms floor up to 100.
            Environment.SetEnvironmentVariable("SQLITE_BUSY_TIMEOUT", "10");
            ConnectionStringFactory.GetBusyTimeout().Should().Be(100);

            // Non-numeric falls back to the default.
            Environment.SetEnvironmentVariable("SQLITE_BUSY_TIMEOUT", "not-a-number");
            ConnectionStringFactory.GetBusyTimeout().Should().Be(1000);
        }
    }
}
