using ghostline;
using Xunit;

namespace ghostline.Tests
{
    // Capy as a client: who gets a line's messages, which lines a chat the bot is in serves, and the
    // pieces that go over the wire (markup, the idempotency key, the voice note's waveform).
    [Collection("config file")]
    public class CapyTests : IDisposable
    {
        readonly Settings _saved = Program.settings;
        static readonly Guid Group = Guid.Parse("0193aaaa-0000-7000-8000-000000000001");

        public CapyTests() => Program.settings = SettingsFile.Parse($"""
            clients:
              telegram:
                token: "1:x"
                chat_ids: ["100"]
              capy:
                url: https://chat.example.com/
                token: rb_test
                to: []
            gateways:
              - id: pbx
                type: asterisk
                ip: 10.0.0.10
            lines:
              - name: home
                gateway: pbx
                modem: gsm1
              - name: work
                gateway: pbx
                modem: gsm2
                clients:
                  telegram:
                    chat_ids: []
                  capy:
                    to: ["@Alice", "{Group}", 13]
              - name: test
                gateway: pbx
                modem: hw1
                clients:
                  telegram:
                    chat_ids: []
            """);

        public void Dispose() => Program.settings = _saved;

        Channel Line(string name) => Program.settings.lines.Single(c => c.name == name);

        [Fact]
        public void ConfigSectionIsRead()
        {
            var c = Program.settings.clients.capy;
            Assert.Equal("rb_test", c.token);
            Assert.True(Program.CapyEnabled);
            Assert.Equal(new[] { "@Alice", Group.ToString(), "13" }, Line("work").clients.capy.to);
        }

        [Fact]
        public void RecipientsAreNormalized()
        {
            Assert.Equal("@alice", Program.CapyRecipient(" @Alice "));
            Assert.Equal("@bob", Program.CapyRecipient("bob"));
            Assert.Equal(Group.ToString(), Program.CapyRecipient(Group.ToString().ToUpperInvariant()));
            Assert.Null(Program.CapyRecipient(" "));
            // The number in a chat's link: capy.example.com/chat/13.
            Assert.Equal("#13", Program.CapyRecipient("13"));
            Assert.Equal("#13", Program.CapyRecipient("#013"));
        }

        // No common recipients: the bot's owner; a line with its own list: that list; a line whose
        // clients do not include capy: nobody.
        [Fact]
        public void LineRecipients()
        {
            Assert.Equal(new[] { Program.CapyOwner }, Program.LineCapy(Line("home")));
            Assert.Equal(new[] { "@alice", Group.ToString(), "#13" }, Program.LineCapy(Line("work")));
            Assert.Empty(Program.LineCapy(Line("test")));
        }

        [Fact]
        public void ChatsServeTheirLines()
        {
            Assert.Equal(new[] { "home" }, Program.LinesOfCapyChat(Guid.NewGuid(), 5, true, "owner_name", isOwner: true).Select(c => c.name));
            Assert.Equal(new[] { "work" }, Program.LinesOfCapyChat(Guid.NewGuid(), 5, true, "ALICE", isOwner: false).Select(c => c.name));
            Assert.Equal(new[] { "work" }, Program.LinesOfCapyChat(Group, 5, false, "mallory", isOwner: false).Select(c => c.name));
            // A group by its number, as the user copies it from the link.
            Assert.Equal(new[] { "work" }, Program.LinesOfCapyChat(Guid.NewGuid(), 13, false, "someone", isOwner: true).Select(c => c.name));
            // A stranger in a direct chat, and a username in a group, serve nothing.
            Assert.Empty(Program.LinesOfCapyChat(Guid.NewGuid(), 5, true, "mallory", isOwner: false));
            Assert.Empty(Program.LinesOfCapyChat(Guid.NewGuid(), 5, false, "alice", isOwner: false));
        }

        [Fact]
        public void OffWithoutToken()
        {
            Program.settings.clients.capy.token = "";
            Assert.False(Program.CapyEnabled);
            Assert.Empty(Program.LineCapy(Line("home")));
        }

        [Fact]
        public void HelpHtmlBecomesCapyMarkup() =>
            Assert.Equal("**ghostline** `home` +7900\nghostline https://github.com/x/y",
                Program.HtmlToCapy("<b>ghostline</b> <code>home</code> +7900\n<a href=\"https://github.com/x/y\">ghostline</a>"));

        // Retries of one queued item must reach Capy with the same clientMsgId, or the message is sent twice.
        [Fact]
        public void MessageIdIsStablePerItem()
        {
            Assert.Equal(Program.CapyMsgId("q42"), Program.CapyMsgId("q42"));
            Assert.NotEqual(Program.CapyMsgId("q42"), Program.CapyMsgId("q43"));
        }

        [Fact]
        public void WaveformHas64BarsScaledToTheLoudest()
        {
            var pcm = new short[8000];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(i < 4000 ? 1000 : 10000);
            var data = pcm.SelectMany(BitConverter.GetBytes).ToArray();
            var wav = new byte[44 + data.Length];
            System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
            BitConverter.GetBytes(wav.Length - 8).CopyTo(wav, 4);
            System.Text.Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
            BitConverter.GetBytes(16).CopyTo(wav, 16);
            BitConverter.GetBytes((short)1).CopyTo(wav, 20);
            BitConverter.GetBytes((short)1).CopyTo(wav, 22);
            BitConverter.GetBytes(8000).CopyTo(wav, 24);
            BitConverter.GetBytes(16000).CopyTo(wav, 28);
            BitConverter.GetBytes((short)2).CopyTo(wav, 32);
            BitConverter.GetBytes((short)16).CopyTo(wav, 34);
            System.Text.Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
            BitConverter.GetBytes(data.Length).CopyTo(wav, 40);
            data.CopyTo(wav, 44);

            var bars = Convert.FromBase64String(Recordings.Waveform(wav));
            Assert.Equal(64, bars.Length);
            Assert.Equal(25, bars[0]);     // 1000 of 10000
            Assert.Equal(255, bars[63]);
            Assert.Null(Recordings.Waveform(new byte[10]));
        }
    }
}
