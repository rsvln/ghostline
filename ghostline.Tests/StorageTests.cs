using ghostline;
using Xunit;

namespace ghostline.Tests
{
    // storage.json from gl-storage on the Asterisk host: whether Asterisk can write the recordings and the
    // CDR. ghostline mounts both read-only, so this report is the only way it learns that they are lost.
    public class StorageTests : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), "gl-storage-" + Guid.NewGuid().ToString("N"));
        static readonly DateTime Now = new(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc);

        public StorageTests() => Directory.CreateDirectory(_dir);
        public void Dispose() => Directory.Delete(_dir, recursive: true);

        Settings Managed() => SettingsFile.Parse($"""
            mode: full
            gateways:
              - id: pbx
                type: asterisk
                ip: 10.0.0.10
                pbx: managed
                managed:
                  conf_dir: {_dir}
            lines: []
            """);

        // As gl-storage writes it, the 2026-10-30 case: the shared directories still owned by FreePBX's uid.
        const string Broken = """
            {
             "at": "2026-11-09T08:59:30Z",
             "recordings": {"path": "/var/spool/asterisk/monitor", "ok": false,
                            "error": "/var/spool/asterisk/monitor: cannot write: Permission denied", "free_mb": 71000},
             "cdr": {"path": "/var/spool/asterisk/gl-cdr/cdr.db", "ok": false,
                     "error": "/var/spool/asterisk/gl-cdr: cannot write: Permission denied", "free_mb": 71000}
            }
            """;

        [Fact]
        public void ReadsTheHostReport()
        {
            File.WriteAllText(Path.Combine(_dir, "storage.json"), Broken);
            var st = Program.ReadStorage(Managed());
            Assert.NotNull(st);
            Assert.False(st.recordings.ok);
            Assert.Equal(71000, st.cdr.free_mb);
            Assert.Equal(DateTimeKind.Utc, st.at.Kind);
        }

        [Fact]
        public void NoReportIsNoProblem()
        {
            Assert.Null(Program.ReadStorage(Managed()));
            Assert.Empty(Program.StorageProblems(null, Now));
        }

        [Fact]
        public void BrokenWritesAreProblems()
        {
            File.WriteAllText(Path.Combine(_dir, "storage.json"), Broken);
            var p = Program.StorageProblems(Program.ReadStorage(Managed()), Now);
            Assert.Equal(new[] { "recordings/write", "cdr/write" }, p.Select(x => x.part + "/" + x.kind));
            Assert.Contains("Permission denied", p[0].detail);
        }

        static Program.StorageCheck Ok(long? free) => new("/x", true, null, free);

        [Fact]
        public void HealthyReportHasNoProblems() =>
            Assert.Empty(Program.StorageProblems(new(Now.AddSeconds(-50), Ok(70000), Ok(70000)), Now));

        [Fact]
        public void LowSpaceIsAProblem()
        {
            var p = Program.StorageProblems(new(Now, Ok(120), Ok(null)), Now);
            Assert.Equal("recordings/space/120", string.Join(";", p.Select(x => $"{x.part}/{x.kind}/{x.detail}")));
        }

        // A stopped helper must not keep an old "all fine" on the screen.
        [Fact]
        public void StaleReportIsAProblemOnItsOwn()
        {
            var p = Program.StorageProblems(new(Now.AddMinutes(-11), Ok(70000), new("/y", false, "no", 1)), Now);
            Assert.Equal("report/stale", string.Join(";", p.Select(x => x.part + "/" + x.kind)));
        }
    }
}
