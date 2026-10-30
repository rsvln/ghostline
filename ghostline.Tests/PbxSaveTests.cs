using ghostline;
using Xunit;

namespace ghostline.Tests
{
    // Phone secrets of the managed Asterisk: a phone saved with an extension gets one, a new one replaces
    // it; comments in the config stay.
    [Collection("config file")]   // Program.ConfigPath is static: one test at a time
    public class PbxSaveTests : IDisposable
    {
        const string Yaml = """
            mode: full
            gateways:
              - id: pbx
                type: asterisk
                ip: 10.0.0.10
                ami_port: 5038
                user: ghostline
                password: secret
                pbx: managed
                managed:
                  conf_dir: /srv/asterisk-conf
                  sip:
                    tls:
                      cert: keys/sip.crt
                      key: keys/sip.key
                modems:
                  - name: gsm1
                    driver: quectel
                  - name: gsm2
                    driver: quectel
              - id: old
                type: asterisk
                ip: 10.0.0.99
                modems:
                  - name: gsm9
                    driver: quectel
            lines:
              - name: home          # line 1
                gateway: pbx
                pattern: gsm1
                modem: gsm1
                phone:
                  extension: "11"
                  secret: Aa1Bb2Cc3Dd4Ee5Ff6Gg7Hh8
              - name: work          # line 2
                gateway: pbx
                pattern: gsm2
                modem: gsm2
                phone:
                  extension: "12"
              - name: other         # FreePBX
                gateway: old
                pattern: gsm9
                modem: gsm9
                phone:
                  extension: "19"
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

        [Fact]
        public void NewPhoneGetsSecret()
        {
            var secrets = Program.AddMissingPhoneSecrets();
            // Only the phone on the managed Asterisk that has none.
            Assert.Equal(new[] { "work" }, secrets.Keys);
            Assert.Matches("^[A-Za-z0-9]{24}$", secrets["work"]);

            string text = File.ReadAllText(_path);
            Assert.Contains("# line 2", text);
            var s = SettingsFile.Parse(text);
            Assert.Equal(secrets["work"], s.lines.Single(c => c.name == "work").phone.secret);
            Assert.Equal("Aa1Bb2Cc3Dd4Ee5Ff6Gg7Hh8", s.lines.Single(c => c.name == "home").phone.secret);
            Assert.Null(s.lines.Single(c => c.name == "other").phone.secret);

            Assert.Empty(Program.AddMissingPhoneSecrets());
        }

        [Fact]
        public void NewSecretReplacesTheOld()
        {
            string secret = Program.NewPhoneSecret("home");
            Assert.Matches("^[A-Za-z0-9]{24}$", secret);
            Assert.Equal(secret, SettingsFile.Parse(File.ReadAllText(_path)).lines.Single(c => c.name == "home").phone.secret);
            Assert.Null(Program.NewPhoneSecret("nosuchline"));
        }

        [Fact]
        public void LineOnAnotherPbxIsAnError()
        {
            Program.AddMissingPhoneSecrets();
            var errors = PbxConfig.Validate(SettingsFile.Parse(File.ReadAllText(_path)));
            Assert.Contains(errors, e => e.Contains("line other") && e.Contains("another PBX"));
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
    }
}
