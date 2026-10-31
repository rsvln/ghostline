using ghostline;
using Xunit;

namespace ghostline.Tests
{
    public class SysLogTests
    {
        [Fact]
        public void KeepsLevelAndMoreSevereNewestFirst()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gl-syslog-" + Guid.NewGuid().ToString("N"));
            SysLog.Configure("info", dir);
            string tag = Guid.NewGuid().ToString("N");
            SysLog.Debug(tag, "d");
            SysLog.Info(tag, "i");
            SysLog.Warn(tag, "w");
            SysLog.Error(tag, "e");

            // debug is below logger.level: not kept anywhere.
            Assert.Equal(new[] { "e", "w", "i" }, SysLog.Read("debug", tag, 10).Select(x => x.message));
            Assert.Equal(new[] { "e", "w" }, SysLog.Read("warning", tag, 10).Select(x => x.message));
            Assert.Equal(new[] { "e" }, SysLog.Read("error", tag, 10).Select(x => x.message));
            Assert.Single(SysLog.Read("info", tag, 1));

            var file = Directory.GetFiles(dir, "system-*.log").Single();
            Assert.Equal(3, File.ReadAllLines(file).Count(l => l.Contains("\t" + tag + "\t")));
            Directory.Delete(dir, true);
        }
    }
}
