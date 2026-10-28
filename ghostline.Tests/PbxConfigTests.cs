using ghostline;
using Xunit;

namespace ghostline.Tests
{
    // The generated Asterisk files are compared with golden copies in golden/. After an intended change
    // run the tests with GHOSTLINE_UPDATE_GOLDEN=1: the golden files are rewritten from the output,
    // then review the diff before committing.
    public class PbxConfigTests
    {
        static Settings Sample() => SettingsFile.Parse("""
            mode: full
            gateways:
              - id: pbx
                type: quectel
                ip: 10.0.0.10
                ami_port: 5038
                user: ghostline
                password: secret
              - id: sticks
                type: dongle
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
                  dial: ru+intl
              - name: test
                gateway: sticks
                pattern: hw1
                line: hw1
                phone:
                  extension: "13"
                  secret: Zz9Yy8Xx7Ww6Vv5Uu4Tt3Ss2
                  encryption: "no"
              - name: nophone
                gateway: pbx
                pattern: gsm2
                line: gsm2
            pbx:
              mode: managed
              asterisk: pbx
              conf_dir: /srv/asterisk-conf
              asterisk_dir: /etc/asterisk/ghostline
              tls:
                cert: keys/sip.crt
                key: keys/sip.key
              udp_port: 5060
              external_address: sip.example.org
              local_nets: [192.0.2.0/24, 10.0.0.0/8]
            """);

        static string GoldenDir => Path.Combine(AppContext.BaseDirectory, "golden");

        static void AssertGolden(string name, string actual)
        {
            if (Environment.GetEnvironmentVariable("GHOSTLINE_UPDATE_GOLDEN") == "1")
            {
                // The source directory, not the copy next to the test binaries.
                string src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "golden"));
                Directory.CreateDirectory(src);
                File.WriteAllText(Path.Combine(src, name), actual);
                return;
            }
            Assert.Equal(File.ReadAllText(Path.Combine(GoldenDir, name)).Replace("\r\n", "\n"), actual);
        }

        [Fact]
        public void SampleIsValid() => Assert.Empty(PbxConfig.Validate(Sample()));

        [Fact]
        public void Pjsip() => AssertGolden(PbxConfig.PjsipFile, PbxConfig.Generate(Sample())[PbxConfig.PjsipFile]);

        [Fact]
        public void Dialplan() => AssertGolden(PbxConfig.DialplanFile, PbxConfig.Generate(Sample())[PbxConfig.DialplanFile]);

        [Fact]
        public void LinesWithoutPhoneAreLeftOut()
        {
            var lines = PbxConfig.Lines(Sample());
            Assert.Equal(new[] { "home", "test" }, lines.Select(l => l.Name));
            Assert.Equal(new[] { "Quectel", "Dongle" }, lines.Select(l => l.Tech));
        }

        [Fact]
        public void ValidationCatchesUnsafeValues()
        {
            var s = Sample();
            s.channels[0].phone.secret = "short";
            s.channels[1].phone.extension = "11";
            s.pbx.externalAddress = "evil\nhost";
            s.pbx.localNets = new() { "not a net" };
            var errors = PbxConfig.Validate(s);
            Assert.Contains(errors, e => e.Contains("no phone secret"));
            Assert.Contains(errors, e => e.Contains("extension 11 is used by more than one line"));
            Assert.Contains(errors, e => e.Contains("external_address"));
            Assert.Contains(errors, e => e.Contains("local_nets"));
        }

        [Fact]
        public void FreepbxModeIsNotGenerated()
        {
            var s = Sample();
            s.pbx.mode = "freepbx";
            Assert.Contains(PbxConfig.Validate(s), e => e.Contains("managed"));
        }

        [Fact]
        public void NewSecretIsValid()
        {
            var s = Sample();
            s.channels[0].phone.secret = PbxConfig.NewSecret();
            Assert.Empty(PbxConfig.Validate(s));
            Assert.NotEqual(PbxConfig.NewSecret(), PbxConfig.NewSecret());
        }
    }
}
