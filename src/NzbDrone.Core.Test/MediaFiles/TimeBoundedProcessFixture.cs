using System;
using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class TimeBoundedProcessFixture : TestBase
    {
        [Test]
        public void kills_a_process_that_overruns_the_deadline()
        {
            // A real child that would run ~30s, with a 1s deadline and NO pool involvement: TimeBoundedProcess
            // itself must SIGKILL it at the deadline. This is the off-pool guarantee fork8 adds (the media-info
            // refresh path never goes through ImportProbePool, so the self-bound is its only deadline).
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1")
                : new ProcessStartInfo("/bin/sh", "-c \"sleep 30\"");

            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            var exitedWhenRunReturned = false;
            var stopwatch = Stopwatch.StartNew();

            // onFinished runs after the kill+reap and before the Process is disposed, so HasExited is valid here.
            TimeBoundedProcess.Run(startInfo, TimeSpan.FromSeconds(1), null, p => exitedWhenRunReturned = p.HasExited);

            stopwatch.Stop();

            exitedWhenRunReturned.Should().BeTrue("TimeBoundedProcess must kill and reap a process that overruns the deadline before returning");
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "it returned near the 1s deadline, not the child's ~30s runtime, proving the kill fired");
        }

        [Test]
        public void returns_stdout_of_a_process_that_finishes_within_the_deadline()
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/c echo hello-bounded")
                : new ProcessStartInfo("/bin/sh", "-c \"echo hello-bounded\"");

            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            var output = TimeBoundedProcess.Run(startInfo, TimeSpan.FromSeconds(30));

            output.Should().Contain("hello-bounded", "a process that finishes within the deadline returns its stdout unchanged");
        }
    }
}
