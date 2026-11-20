# Capy

Capy is a self-hosted messenger with a bot API. ghostline is a bot there: it
writes SMS, calls and alerts to the recipients and takes SMS to send and
commands from the messages written to it, the same way it works with Telegram
and ntfy.

## The bot

In Capy: **Settings → Боты и API → Мои боты**, create a bot (a username such
as `sms_bot` and a display name) and copy its token (`rb_...`, shown once).
The person who created the bot is its owner.

```yaml
clients:
  capy:
    url: https://chat.example.com     # the Capy server
    token: rb_...                     # the bot's token
    to: []                            # empty: the bot's owner
```

`to` lists the recipients:

- `@username`: the direct chat with that person (the bot opens it itself);
- a chat id: a group the bot has been added to.

Empty `to`: the bot's owner only. A line can have its own recipients
(`lines[].clients.capy.to`); empty, it uses the common ones. As with Telegram
and ntfy, a line with a `clients` section goes to Capy only when `capy` is in
it.

## What comes

- Incoming and sent SMS, with the same text as in Telegram.
- Calls: a voice note with the recording right after the call (with a
  waveform), and the transcript added to the same message by editing it when
  it is ready. Missed calls as a text message (`calls.missed`).
- Line monitoring: no network, and back.
- PBX alerts (password guessing, the certificate, call storage) go to the
  common recipients.

Messages are queued in ghostline's database and retried until they go out.
Each one carries a fixed `clientMsgId`, so a retry after a lost answer does not
send it twice.

## Sending SMS and commands

Write to the bot in a chat it serves, as in Telegram:

- a chat of **one line**: two lines, the recipient and the text;
- a chat of **several lines**: the line name, the recipient and the text.

Commands: `/help` (this format and the lines of the chat), `/status` (the
state of gateways, channels and queues). They are in the bot's command menu
above the message box.

A direct chat is served when its person is a recipient of some line (or is the
owner, with empty `to`). A stranger who writes to the bot gets a refusal. A
group is served when its chat id is a recipient; other groups are ignored.

## How ghostline talks to Capy

Only outgoing HTTPS from ghostline to the Capy server (`/api/v1`, the bot
token in `Authorization: Bearer`). No webhook is used: ghostline polls the
bot's chats every 3 seconds (`GET /chats`, then
`GET /chats/{id}/events?after=<seq>` for chats with new events), so it needs
no address the Capy server can reach. The last seen `seq` of each chat is kept
in the database, so messages written while ghostline was down are handled
after its start; a chat seen for the first time starts from its current state,
its history is not replayed.

Capy is "connected" on the Status tab while `GET /bot/me` (once a minute) and
the polling work.
