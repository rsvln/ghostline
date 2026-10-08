using System.Buffers;
using SmtpServer;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace ghostline
{
    internal partial class Program
    {
        private static void DeliverSms(string gwt, string gatewayId, Channel chan, string sender, string recvTime, string telegramText, string logText)
        {
            HealthStatus.MarkActivity(gatewayId);

            string contactName = "";
            var people = FindContact(sender);
            if (people != null)
                contactName = (people.LastName + " " + people.FirstName + " " + people.MiddleName).Trim();
            string ppl = string.IsNullOrEmpty(contactName) ? "" : "\n" + contactName;

            string body = sender + " " + chan.name + " " + recvTime + ppl + "\n------------------\n\n" + telegramText;
            EnqueueTelegramBroadcast(body);

            if (settings.logger.file)
                Log(gwt, "in", recvTime, chan.name, sender, logText);
            Console.WriteLine(recvTime + "\t" + gwt + "\tin" + "\t" + chan.name + "\t" + sender + "\t" + logText);

            Store.Insert(new SmsRecord
            {
                Ts = recvTime,
                Direction = "in",
                Gateway = gwt,
                Channel = chan.name,
                Peer = sender,
                Note = contactName,
                Content = logText
            });
        }

        public static void SMTPReadData(string xxx)
        {
            Channel chan = getChannel(xxx);

            if (chan != null)
            {
                HealthStatus.MarkActivity(chan.gateway);
                List<string> xxxl = xxx.Replace(chan.pattern, "").Trim().Split(',').ToList();
                if (xxxl.Count > 3)
                {
                    for (int i = 3; i < xxxl.Count; i++)
                    {
                        xxxl[2] = xxxl[2] + ", " + xxxl[i];
                    }
                }
                string sender = ToE164(xxxl[1]);
                string contactName = "";
                var people = FindContact(sender);
                if (people != null)
                    contactName = (people.LastName + " " + people.FirstName + " " + people.MiddleName).Trim();
                string ppl = string.IsNullOrEmpty(contactName) ? "" : "\n" + contactName;
                string recvTime = DateTime.Now.ToString("yyyy-MM-dd") + " " + xxxl[0].Substring(xxxl[0].Length - 8, 8);
                // Some senders (for example automated SMS of delivery services) put literal
                // "+" or "\n" into the text instead of real line breaks; both are turned
                // into a real line break for readability.
                string content = xxxl[2].Replace("+", "\n").Replace("\\n", "\n");

                EnqueueTelegramBroadcast(sender + " " + chan.name + " " + recvTime + ppl + "\n------------------\n\n" + content);
                if (settings.logger.file)
                    Log("g", "in", recvTime, chan.name, sender, content);
                Console.WriteLine(recvTime + "\t" + "g\tin" + "\t" + chan.name + "\t" + sender + "\t" + content);

                Store.Insert(new SmsRecord
                {
                    Ts = recvTime,
                    Direction = "in",
                    Gateway = "g",
                    Channel = chan.name,
                    Peer = sender,
                    Note = contactName,
                    Content = content
                });
            }
        }
    }

    public class SampleMessageStore : MessageStore
    {
        public override async Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
        {
            await using var stream = new MemoryStream();
            var position = buffer.GetPosition(0);
            while (buffer.TryGet(ref position, out var memory))
            {
                await stream.WriteAsync(memory, cancellationToken);
            }
            stream.Position = 0;
            var message = await MimeKit.MimeMessage.LoadAsync(stream, cancellationToken);
            {
                Program.SMTPReadData(message.TextBody);
            }
            return SmtpResponse.Ok;
        }
    }
}
