using ghostline;
using Xunit;

namespace ghostline.Tests
{
    // Saving the PBX tab into the config file: values change in place, comments stay, a new phone
    // gets a secret, an empty extension removes the phone, a dry run writes nothing.
    [Collection("config file")]   // Program.ConfigPath is static: one test at a time
    public class PbxSaveTests : IDisposable
    {
        const string Yaml = """
            mode: full
            gateways:
              - id: pbx
                type: quectel
                ip: 10.0.0.10
                ami_port: 5038
                user: ghostline
                password: secret
            channels:
              - name: home          # line 1
                gateway: pbx
                pattern: gsm1
                line: gsm1
                phone:
                  extension: "11"
                  secret: Aa1Bb2Cc3Dd4Ee5Ff6Gg7Hh8
              - name: work          # line 2
                gateway: pbx
                pattern: gsm2
                line: gsm2
            pbx:
              mode: managed
              asterisk: pbx
              conf_dir: /srv/asterisk-conf
              tls:
                cert: keys/sip.crt
                key: keys/sip.key
                port: 5061
            """;

        readonly string _path = Path.Combine(Path.GetTempPath(), "gl-pbxsave-" + Guid.NewGuid().ToString("N") + ".yaml");

        public PbxSaveTests()
        {
            File.WriteAllText(_path, Yaml);
            Program.ConfigPath = _path;
        }

        public void Dispose()
        {
            File.Delete(_path);
            File.Delete(_path + ".bak");
        }

        static Program.PbxEdit Edit(bool dryRun = false, Program.PbxSipEdit sip = null, params Program.PbxLineEdit[] lines) =>
            new(sip, lines.ToList(), dryRun);

        [Fact]
        public void AddsPhoneWithSecretAndKeepsComments()
        {
            var r = Program.SavePbx(Edit(lines: new Program.PbxLineEdit("work", "12", "ru+intl", "sdes")));
            Assert.True(r.ok, string.Join("; ", r.errors));
            Assert.Matches("^[A-Za-z0-9]{24}$", r.secrets["work"]);

            string text = File.ReadAllText(_path);
            Assert.Contains("# line 2", text);
            var work = SettingsFile.Parse(text).channels.Single(c => c.name == "work");
            Assert.Equal("12", work.phone.extension);
            Assert.Equal("ru+intl", work.phone.dial);
            Assert.Equal(r.secrets["work"], work.phone.secret);
        }

        [Fact]
        public void EmptyExtensionRemovesPhone()
        {
            var r = Program.SavePbx(Edit(lines: new Program.PbxLineEdit("home", "", null, null)));
            // No line has a phone then: refused, nothing written.
            Assert.False(r.ok);
            Assert.Equal(Yaml, File.ReadAllText(_path));

            Program.SavePbx(Edit(lines: new Program.PbxLineEdit("work", "12", "ru", "sdes")));
            r = Program.SavePbx(Edit(lines: new Program.PbxLineEdit("home", "", null, null)));
            Assert.True(r.ok, string.Join("; ", r.errors));
            Assert.Null(SettingsFile.Parse(File.ReadAllText(_path)).channels.Single(c => c.name == "home").phone);
        }

        [Fact]
        public void SipFieldsAndDryRun()
        {
            var sip = new Program.PbxSipEdit(5062, null, "sip.example.org", "10.0.0.0/8, 192.0.2.0/24", null, null);
            var r = Program.SavePbx(Edit(dryRun: true, sip: sip));
            Assert.True(r.ok, string.Join("; ", r.errors));
            Assert.Contains("bind=0.0.0.0:5062", r.files[PbxConfig.PjsipFile]);
            Assert.Equal(Yaml, File.ReadAllText(_path));

            r = Program.SavePbx(Edit(sip: sip));
            Assert.True(r.ok);
            var p = SettingsFile.Parse(File.ReadAllText(_path)).pbx;
            Assert.Equal(5062, p.tls.port);
            Assert.Equal(new[] { "10.0.0.0/8", "192.0.2.0/24" }, p.localNets);
        }

        [Fact]
        public void LineOnAnotherPbxGetsNoPhone()
        {
            File.WriteAllText(_path, Yaml.Replace("    password: secret\n", """
                    password: secret
                  - id: old
                    type: quectel
                    ip: 10.0.0.99
                    ami_port: 5038
                    user: ghostline
                    password: secret

                """).Replace("    gateway: pbx\n    pattern: gsm2", "    gateway: old\n    pattern: gsm2"));
            var r = Program.SavePbx(Edit(lines: new Program.PbxLineEdit("work", "12", "ru", "sdes")));
            Assert.False(r.ok);
            Assert.Contains(r.errors, e => e.Contains("another PBX"));
        }

        [Fact]
        public void AddsAndRemovesLine()
        {
            var r = Program.AddLine(new("line4", "pbx", "gsm4", "14", "ru", "sdes"));
            Assert.True(r.ok, r.error);
            Assert.Matches("^[A-Za-z0-9]{24}$", r.secret);
            var ch = SettingsFile.Parse(File.ReadAllText(_path)).channels.Single(c => c.name == "line4");
            Assert.Equal(("pbx", "gsm4", "gsm4", "14"), (ch.gateway, ch.pattern, ch.line, ch.phone.extension));

            Assert.False(Program.AddLine(new("line5", "pbx", "gsm4", "", null, null)).ok);   // device taken
            Assert.False(Program.AddLine(new("line4", "pbx", "gsm5", "", null, null)).ok);   // name taken
            Assert.False(Program.AddLine(new("line6", "pbx", "gsm6", "11", null, null)).ok); // extension taken

            Assert.True(Program.RemoveLine("line4").ok);
            Assert.DoesNotContain(SettingsFile.Parse(File.ReadAllText(_path)).channels, c => c.name == "line4");
            Assert.Contains("# line 2", File.ReadAllText(_path));
        }

        [Fact]
        public void ParsesDeviceTable()
        {
            const string text = """
                ID           Group State      RSSI Mode Submode Provider Name  Model      Firmware          IMEI             IMSI             Number
                hw1          3     Free       16   0    0       t2 rus         E1550      11.608.14.15.311  000000000000000  000000000000000  Unknown
                gsm1         1     Ring       27   0    0       MTS 5G         EC200A     EC200AEUHAR01A30M 000000000000000  000000000000000  +79001234567
                """;
            var rows = Program.ParseDeviceTable(text);
            Assert.Equal(2, rows.Count);
            Assert.Equal(("hw1", "Free", "t2 rus", "E1550"), rows[0]);
            Assert.Equal(("gsm1", "Ring", "MTS 5G", "EC200A"), rows[1]);
        }

        [Fact]
        public void BadValuesAreRefused()
        {
            var r = Program.SavePbx(Edit(lines: new Program.PbxLineEdit("home", "1x", "ru", "sdes")));
            Assert.False(r.ok);
            Assert.Equal(Yaml, File.ReadAllText(_path));
        }
    }
}
