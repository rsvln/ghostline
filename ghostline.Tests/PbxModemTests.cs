using ghostline;
using Xunit;

namespace ghostline.Tests
{
    // Modems added and removed in the settings: on the managed Asterisk the ports must come from the USB
    // list of its host and be open to Asterisk, and the modem files are generated; under FreePBX a modem
    // is its name and driver only.
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
                    type: asterisk
                    ip: 10.0.0.10
                    ami_port: 5038
                    user: ghostline
                    password: secret
                    pbx: managed
                    managed:
                      conf_dir: {_dir}
                      sip:
                        tls:
                          cert: keys/sip.crt
                          key: keys/sip.key
                    modems:
                      - name: gsm1
                        driver: quectel
                        data: /dev/serial/by-path/pci-0000:00:14.0-usb-0:9:1.3-port0
                        audio: /dev/serial/by-path/pci-0000:00:14.0-usb-0:9:1.2-port0
                  - id: freepbx
                    type: asterisk
                    ip: 10.0.0.99
                lines:
                  - name: home
                    gateway: pbx
                    pattern: gsm1
                    modem: gsm1
                    phone:
                      extension: "11"
                      secret: Aa1Bb2Cc3Dd4Ee5Ff6Gg7Hh8
                """);
            Program.ConfigPath = ConfigFile;
        }

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        const string Data = "/dev/serial/by-path/pci-0000:00:14.0-usb-0:7:1.3-port0";
        const string Audio = "/dev/serial/by-path/pci-0000:00:14.0-usb-0:7:1.2-port0";

        static Program.PbxModemEdit Add(string gateway, string name, string driver, string data = null, string audio = null, string alsadev = null) =>
            new("add", gateway, name, driver, data, audio, alsadev, false);

        static Program.PbxModemEdit Remove(string gateway, string name) => new("remove", gateway, name, null, null, null, null, false);

        [Fact]
        public void AddsModemToTheManagedAsterisk()
        {
            var r = Program.EditModem(Add("pbx", "hw1", "dongle", Data, Audio));
            Assert.True(r.ok, r.error);

            var s = SettingsFile.Parse(File.ReadAllText(ConfigFile));
            var m = s.ManagedGateway.Modem("hw1");
            Assert.Equal(("hw1", "dongle", Data, Audio), (m.name, m.driver, m.data, m.audio));
            Assert.Equal(new[] { "pbx", "freepbx" }, s.gateways.Select(g => g.id));   // no extra gateway

            string conf = PbxConfig.Generate(s)[PbxConfig.DongleFile];
            Assert.Contains("[hw1]\naudio=" + Audio + "\ndata=" + Data + "\ncontext=gl-in-hw1\n", conf);
            // No phone on hw1: a stub context, so SMS still work and calls are not answered.
            Assert.Contains("[gl-in-hw1]\nexten => sms,1,Hangup()", PbxConfig.Generate(s)[PbxConfig.DialplanFile]);

            // The same ports again, a port Asterisk may not open, a port not on the host: refused.
            Assert.False(Program.EditModem(Add("pbx", "hw2", "dongle", Data, Audio)).ok);
            Assert.False(Program.EditModem(Add("pbx", "gsm2", "quectel", "/dev/serial/by-path/pci-0000:00:14.0-usb-0:1:1.3-port0", Audio)).ok);
            Assert.False(Program.EditModem(Add("pbx", "gsm3", "quectel", "/dev/serial/by-path/nowhere-port0", null, "hw:CARD=X,DEV=0")).ok);

            Assert.True(Program.EditModem(Remove("pbx", "hw1")).ok);
            Assert.Null(SettingsFile.Parse(File.ReadAllText(ConfigFile)).ManagedGateway.Modem("hw1"));
        }

        [Fact]
        public void FreepbxModemIsNameAndDriver()
        {
            Assert.True(Program.EditModem(Add("freepbx", "gsm9", "quectel", Data, Audio)).ok);
            var m = SettingsFile.Parse(File.ReadAllText(ConfigFile)).gateways.Single(g => g.id == "freepbx").Modem("gsm9");
            Assert.Equal(("gsm9", "quectel", null, null), (m.name, m.driver, m.data, m.audio));
            Assert.False(Program.EditModem(Add("nosuch", "gsm8", "quectel")).ok);
        }

        [Fact]
        public void ModemOfALineIsNotRemoved()
        {
            var r = Program.EditModem(Remove("pbx", "gsm1"));
            Assert.False(r.ok);
            Assert.Contains("home", r.error);
        }
    }
}
