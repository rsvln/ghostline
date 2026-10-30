using ghostline;
using Xunit;

namespace ghostline.Tests
{
    // The format before 2026-10-30 (channels, a top-level pbx section, gateways of type quectel / dongle)
    // is read as the current one and rewritten in place, comments kept.
    public class SettingsMigrationTests : IDisposable
    {
        readonly string _path = Path.Combine(Path.GetTempPath(), "gl-migrate-" + Guid.NewGuid().ToString("N") + ".yaml");

        public void Dispose()
        {
            File.Delete(_path);
            File.Delete(_path + ".bak-v2");
        }

        // Two PBXs during a move: FreePBX with two modems, the managed Asterisk with a Huawei stick.
        const string Old = """
            mode: full

            gateways:
              - id: quectel1
                type: quectel
                ip: 10.0.0.200
                ami_port: 5038
                user: ghostline
                password: secret
              - id: dongle1             # Huawei sticks via chan_dongle
                type: dongle
                ip: 10.0.0.218
                ami_port: 5038
                user: ghostline
                password: secret

            channels:
              - name: home            # line 1
                gateway: quectel1
                pattern: gsm1
                line: gsm1
                number: "+79001234567"
              - name: work            # line 2
                gateway: quectel1
                pattern: gsm2
                line: gsm2
              - name: test            # line 3
                gateway: dongle1
                pattern: hw1
                line: hw1
                phone:
                  extension: "13"
                  secret: Aa1Bb2Cc3Dd4Ee5Ff6Gg7Hh8
                  dial: ru
                  encryption: sdes

            pbx:
              mode: managed
              asterisk: dongle1
              conf_dir: /srv/asterisk-conf
              asterisk_dir: /etc/asterisk/ghostline
              tls:
                cert: keys/sip.crt
                key: keys/sip.key
                port: 5062
              external_address: sip.example.org
              local_nets:
                - 192.0.2.0/24
                - 10.0.0.0/8
              modems:
                - name: hw1
                  driver: dongle
                  data: "/dev/serial/by-path/pci-0000:00:14.0-usb-0:7:1.3-port0"
                  audio: "/dev/serial/by-path/pci-0000:00:14.0-usb-0:7:1.2-port0"

            calls:
              cdr_sqlite: /srv/asterisk-cdr/cdr.db
              recordings_dir: /srv/asterisk-rec
            """;

        [Fact]
        public void ReadsTheOldFormat()
        {
            var s = SettingsFile.Parse(Old);
            Assert.NotNull(s.Migration);
            Assert.Equal(new[] { "quectel1", "dongle1" }, s.gateways.Select(g => g.id));
            Assert.All(s.gateways, g => Assert.Equal("asterisk", g.type));

            var freepbx = s.gateways[0];
            Assert.False(freepbx.Managed);
            Assert.Equal(new[] { ("gsm1", "quectel"), ("gsm2", "quectel") }, freepbx.modems.Select(m => (m.name, m.driver)));

            var managed = s.ManagedGateway;
            Assert.Equal("dongle1", managed.id);
            Assert.Equal(("/srv/asterisk-conf", 5062, "sip.example.org"), (managed.managed.confDir, managed.managed.sip.tls.port, managed.managed.sip.externalAddress));
            var hw1 = Assert.Single(managed.modems);
            Assert.Equal(("hw1", "dongle"), (hw1.name, hw1.driver));
            Assert.EndsWith("7:1.2-port0", hw1.audio);

            Assert.Equal(new[] { "gsm1", "gsm2", "hw1" }, s.lines.Select(l => l.modem));
            Assert.All(s.lines, l => Assert.Null(l.port));
            Assert.Equal("Dongle", Program.LineFlavor(s, s.lines[2]).Tech);
            Assert.Empty(PbxConfig.Validate(s));
        }

        [Fact]
        public void RewritesTheFileKeepingComments()
        {
            File.WriteAllText(_path, Old);
            SettingsFile.MigrateToV2(_path);

            string text = File.ReadAllText(_path);
            Assert.Equal(Old, File.ReadAllText(_path + ".bak-v2"));
            Assert.DoesNotContain("Converted", text);              // rewritten in place, not written out in full
            Assert.Contains("# line 1", text);
            Assert.Contains("# Huawei sticks via chan_dongle", text);
            Assert.DoesNotContain("channels:", text);
            Assert.DoesNotContain("\npbx:", text.Replace("\r\n", "\n"));
            Assert.DoesNotContain("line:", text);

            var s = SettingsFile.Parse(text);
            Assert.Null(s.Migration);
            Assert.Equal(SettingsFile.Serialize(SettingsFile.Parse(Old)), SettingsFile.Serialize(s));

            // Once only.
            File.Delete(_path + ".bak-v2");
            SettingsFile.MigrateToV2(_path);
            Assert.False(File.Exists(_path + ".bak-v2"));
            Assert.Equal(text, File.ReadAllText(_path));
        }

        [Fact]
        public void GeneratesTheSameFilesAfterTheRewrite()
        {
            File.WriteAllText(_path, Old);
            var before = PbxConfig.Generate(SettingsFile.Parse(Old));
            SettingsFile.MigrateToV2(_path);
            Assert.Equal(before, PbxConfig.Generate(SettingsFile.Parse(File.ReadAllText(_path))));
        }

        [Fact]
        public void MergesGatewaysOfOneAsterisk()
        {
            const string yaml = """
                gateways:
                  - id: modems
                    type: quectel
                    ip: 10.0.0.10
                    ami_port: 5038
                    monitor: false
                  - id: sticks
                    type: dongle
                    ip: 10.0.0.10
                    ami_port: 5038
                  - id: goip1
                    type: goip
                    ip: 10.0.0.50
                    http_port: 80
                channels:
                  - name: a
                    gateway: modems
                    pattern: gsm1
                    line: gsm1
                  - name: b
                    gateway: sticks
                    pattern: hw1
                    line: hw1
                  - name: c
                    gateway: goip1
                    pattern: "3"
                    line: "3"
                """;
            File.WriteAllText(_path, yaml);
            SettingsFile.MigrateToV2(_path);
            var s = SettingsFile.Parse(File.ReadAllText(_path));
            Assert.Null(s.Migration);

            Assert.Equal(new[] { "modems", "goip1" }, s.gateways.Select(g => g.id));
            var ast = s.gateways[0];
            Assert.True(ast.monitor);   // on when any of the merged gateways had it on
            Assert.Equal(new[] { ("gsm1", "quectel"), ("hw1", "dongle") }, ast.modems.Select(m => (m.name, m.driver)));
            Assert.Equal(new[] { "modems", "modems", "goip1" }, s.lines.Select(l => l.gateway));
            Assert.Equal(("hw1", null, "3"), (s.lines[1].modem, s.lines[2].modem, s.lines[2].port));
            Assert.Null(s.ManagedGateway);
        }

        [Fact]
        public void LeavesTheCurrentFormatAlone()
        {
            const string yaml = """
                gateways:
                  - id: pbx
                    type: asterisk
                    ip: 10.0.0.10
                    modems:
                      - name: gsm1
                        driver: quectel
                lines:
                  - name: home
                    gateway: pbx
                    modem: gsm1
                """;
            File.WriteAllText(_path, yaml);
            SettingsFile.MigrateToV2(_path);
            Assert.False(File.Exists(_path + ".bak-v2"));
            Assert.Equal(yaml, File.ReadAllText(_path));
            Assert.Null(SettingsFile.Parse(yaml).Migration);
        }
    }
}
