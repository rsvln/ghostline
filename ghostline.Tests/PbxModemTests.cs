using ghostline;
using Xunit;

namespace ghostline.Tests
{
    // Modems added and removed on the PBX tab: ports must come from the USB list of the Asterisk host
    // and be open to Asterisk; a missing gateway of the driver is added; the modem files are generated.
    [Collection("config file")]
    public class PbxModemTests : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), "gl-pbxmodem-" + Guid.NewGuid().ToString("N"));
        string ConfigFile => Path.Combine(_dir, "ghostline.yaml");

        const string Usb = """
            {"devices": [
             {"usb": "3-7", "vid": "12d1", "pid": "140c", "manufacturer": "HUAWEI", "product": "HUAWEI Mobile",
              "ports": [{"path": "/dev/serial/by-path/pci-0000:00:14.0-usb-0:7:1.0-port0", "tty": "ttyUSB8", "interface": 0, "access": true},
                        {"path": "/dev/serial/by-path/pci-0000:00:14.0-usb-0:7:1.2-port0", "tty": "ttyUSB9", "interface": 2, "access": true},
                        {"path": "/dev/serial/by-path/pci-0000:00:14.0-usb-0:7:1.3-port0", "tty": "ttyUSB10", "interface": 3, "access": true}],
              "sound": []},
             {"usb": "3-1", "vid": "1e0e", "pid": "9001", "manufacturer": "SimTech", "product": "SimTech",
              "ports": [{"path": "/dev/serial/by-path/pci-0000:00:14.0-usb-0:1:1.3-port0", "tty": "ttyUSB3", "interface": 3, "access": false}],
              "sound": []}
            ]}
            """;

        public PbxModemTests()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "usb.json"), Usb);
            File.WriteAllText(ConfigFile, $"""
                mode: full
                gateways:
                  - id: pbx
                    type: quectel
                    ip: 10.0.0.10
                    ami_port: 5038
                    user: ghostline
                    password: secret
                channels:
                  - name: home
                    gateway: pbx
                    pattern: gsm1
                    line: gsm1
                    phone:
                      extension: "11"
                      secret: Aa1Bb2Cc3Dd4Ee5Ff6Gg7Hh8
                pbx:
                  mode: managed
                  asterisk: pbx
                  conf_dir: {_dir}
                  tls:
                    cert: keys/sip.crt
                    key: keys/sip.key
                """);
            Program.ConfigPath = ConfigFile;
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        const string Data = "/dev/serial/by-path/pci-0000:00:14.0-usb-0:7:1.3-port0";
        const string Audio = "/dev/serial/by-path/pci-0000:00:14.0-usb-0:7:1.2-port0";

        [Fact]
        public void AddsModemAndItsGateway()
        {
            var r = Program.EditModem(new("add", "hw1", "dongle", Data, Audio, null, false));
            Assert.True(r.ok, r.error);
            Assert.True(r.restart);   // no dongle gateway on that Asterisk yet

            var s = SettingsFile.Parse(File.ReadAllText(ConfigFile));
            var m = Assert.Single(s.pbx.modems);
            Assert.Equal(("hw1", "dongle", Data, Audio), (m.name, m.driver, m.data, m.audio));
            var gw = s.gateways.Single(g => g.id == "pbx-dongle");
            Assert.Equal(("dongle", "10.0.0.10", 5038, "ghostline"), (gw.type, gw.ip, gw.amiPort, gw.user));

            string conf = PbxConfig.Generate(s)[PbxConfig.DongleFile];
            Assert.Contains("[hw1]\naudio=" + Audio + "\ndata=" + Data + "\ncontext=gl-in-hw1\n", conf);
            // No phone on hw1: a stub context, so SMS still work and calls are not answered.
            Assert.Contains("[gl-in-hw1]\nexten => sms,1,Hangup()", PbxConfig.Generate(s)[PbxConfig.DialplanFile]);

            // The same ports again, a port Asterisk may not open, a port not on the host: refused.
            Assert.False(Program.EditModem(new("add", "hw2", "dongle", Data, Audio, null, false)).ok);
            Assert.False(Program.EditModem(new("add", "gsm2", "quectel", "/dev/serial/by-path/pci-0000:00:14.0-usb-0:1:1.3-port0", Audio, null, false)).ok);
            Assert.False(Program.EditModem(new("add", "gsm3", "quectel", "/dev/serial/by-path/nowhere-port0", null, "hw:CARD=X,DEV=0", false)).ok);

            Assert.True(Program.EditModem(new("remove", "hw1", null, null, null, null, false)).ok);
            Assert.Empty(SettingsFile.Parse(File.ReadAllText(ConfigFile)).pbx.modems);
        }

        [Fact]
        public void ModemOfALineIsNotRemoved()
        {
            Assert.True(Program.EditModem(new("add", "gsm1", "quectel", Data, Audio, null, false)).ok);
            var r = Program.EditModem(new("remove", "gsm1", null, null, null, null, false));
            Assert.False(r.ok);
            Assert.Contains("home", r.error);
        }
    }
}
