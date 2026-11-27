# Capy

Capy is a self-hosted messenger with a bot API. ghostline is a bot there: it
writes SMS and alerts to the recipients and takes SMS to send and commands
from the messages written to it, the same way it works with Telegram and ntfy.
Calls Capy records itself; ghostline adds the transcript to its records.

## The bot

In Capy: **Settings → Боты и API → Мои боты**, create a bot (a username such
as `sms_bot` and a display name) and copy its token (`rb_...`, shown once).
The person who created the bot is its owner.

```yaml
clients:
  capy:
    url: https://chat.example.com     # the Capy server
    token: rb_...                     # the main bot's token; optional when every target has its own
    targets:
      - name: me
        to: ""                        # empty: the bot's owner
        sms: [home, work]             # SMS of these lines; "*": every line
        alerts: true                  # no network on a line, PBX attacks, storage, certificate
      - name: home-phone              # the phone line's bot of the "home" line
        token: rb_...
        transcripts: [home]
```

Each target gets what it subscribes to. `to`:

- `@username`: the direct chat with that person (the bot opens it itself);
- a group the bot has been added to: its number from the link
  (`chat.example.com/chat/13` → `#13`) or its id;
- empty: the bot's owner (for a phone line's bot: its own chat).

## Calls: the transcript under Capy's record

Capy records calls itself: its SIP bridge registers on the line's phone
account (a second client next to the phone, `max_contacts=2`), and when a call
ends Capy posts a call record into the phone line's chat. ghostline does not
send calls to Capy; it adds the transcript under that record, as a cut
(`>>> 📝 Transcript` … `<<<`).

Only the chat's own bot may do that: in Capy, the phone line's settings show
its bot (`@<chat>_phonebot`) and its token. Put that token into a target with
`transcripts: [line]`. ghostline then, once the transcript is ready, sends
`PATCH /chats/{chat}/messages/0` with the call's `pbxCallId`; Capy finds its
record by that id and replaces its note. A record not there yet (Capy posts
it at the end of the call) is tried again after 1, 2, 5, 10 and 15 minutes.

The id both sides know is the `X-Asterisk-CallId` SIP header:

- a call to the phones: the dialplan adds it (the call's `linkedid`) to the
  INVITE of every ringing client (`b(gl-callid)`); Capy's bridge keeps it;
- a call placed from Capy: Asterisk cannot add headers to its answers, so
  Capy's bridge sends its own id in the INVITE; the dialplan keeps it in
  `CDR(userfield)`, and ghostline reads it from there. The CDR needs the
  `userfield` column ([cdr-sqlite.md](cdr-sqlite.md)).

## What comes

- Incoming and sent SMS, with the same text as in Telegram.
- The transcript of each call, under Capy's own record of it.
- Alerts: a line without network and back, password guessing, the
  certificate, call storage.

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

A direct chat is served when its person is a target with lines in `sms` (or
is the owner, with empty `to`). A stranger who writes to the bot gets a
refusal. A group is served when it is such a target; other groups are
ignored.

A target may have its own bot (`token`): it then writes from that bot, and
the bot is polled too when the target has lines in `sms`, so SMS and commands
written to it work the same way; its own owner is its `to` when `to` is empty.
The main token is optional then. A phone line's bot that only adds
transcripts is not polled.

## How ghostline talks to Capy

Only outgoing HTTPS from ghostline to the Capy server (`/api/v1`, the bot
token in `Authorization: Bearer`). No webhook is used: ghostline polls the
bots' chats every 3 seconds, one bot after another (`GET /chats`, then
`GET /chats/{id}/events?after=<seq>` for chats with new events), so it needs
no address the Capy server can reach. The last seen `seq` of each chat is kept
in the database, so messages written while ghostline was down are handled
after its start; a chat seen for the first time starts from its current state,
its history is not replayed.

Every bot is checked once in `check_minutes` (default 5, at least 1), all of
them in one window: one request at a time, spaced evenly over it, `GET /bot/me` for every bot, polled
or not. Capy is "connected" on the Status tab
while every bot works, "partly connected" (yellow, the failing bots named)
while some do, "no connection" while none does. A bot whose polling fails is
polled again after 30 seconds.
