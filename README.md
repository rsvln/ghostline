# ghostline

ghostline connects the GSM lines of a gateway or an Asterisk/FreePBX system
to Telegram and to a small web interface. It forwards SMS in both directions
and, optionally, handles phone calls: a call journal, recordings, speech
transcription and notifications.

It started as **yetgsms** (SMS only). The SMS functionality is unchanged and
is what runs in the default `sms` mode.

- [Features](#features)
- [How it works](#how-it-works)
- [Requirements](#requirements)
- [Installation](#installation)
- [Configuration](#configuration)
- [Telegram bot](#telegram-bot)
- [Web interface](#web-interface)
- [Storage and reliability](#storage-and-reliability)
- [Hardware](#hardware)
- [License](#license)

## Features

**SMS** (`mode: sms`, default)

- Gateways: Yeastar (AMI), GoIP (SMTP + HTTP), Quectel modules through
  `chan_quectel` in Asterisk/FreePBX (AMI). Several gateways of the same type
  are supported.
- Incoming SMS are forwarded to one or more Telegram chats, with the sender's
  name resolved from a Google Contacts export.
- Outgoing SMS from a Telegram command or from the web interface, through a
  persistent queue.
- SMS history in SQLite, with search and export.

**Calls** (`mode: full`, Asterisk/FreePBX only)

- Call journal read from the PBX CDR database: direction, line, contact name,
  duration, missed calls.
- Playback and download of recordings in the browser. Recordings stay on the
  PBX and are fetched over HTTP when needed.
- Loudness balancing for playback and transcription (the remote side usually
  comes from the GSM module much quieter than the local phone).
- Transcription through any OpenAI-compatible
  `/v1/audio/transcriptions` service, for example
  [speaches](https://github.com/speaches-ai/speaches) (faster-whisper).
  If the PBX records each side of the call separately, the transcript is a
  dialog with speakers and timestamps.
- Telegram: a voice message right after the call; the transcript is added to
  the same message when it is ready. Optional notifications about missed
  calls. Each line can have its own chats.
- Transcript download as PDF (same look as in the web interface) or plain
  text; full-text search across transcripts.

Interface and messages are available in English and Russian.

## How it works

```
GSM gateway / Asterisk+chan_quectel ──AMI──▶ ghostline ──▶ Telegram
                                                 │
Asterisk CDR (MySQL, read-only) ─────────────────┤
Recordings (HTTP, read-only) ────────────────────┤──▶ web interface
Transcription service (HTTP) ◀───────────────────┘
```

ghostline is a single self-contained Linux binary. It keeps its state in one
SQLite file next to the configuration file.

## Requirements

- A supported gateway: Yeastar, GoIP, or Asterisk/FreePBX with
  `chan_quectel` and a Quectel or SIMCom module.
- A Telegram bot token.
- Linux x64 (systemd service) or Docker.
- For `mode: full`: FreePBX/Asterisk with call recording enabled, `ffmpeg` on
  the ghostline host, and optionally a transcription service.

## Installation

### systemd (recommended)

Build a self-contained binary (requires the .NET 10 SDK):

```bash
dotnet publish ghostline/ghostline.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -o publish
```

Copy the contents of `publish/` (binary, `web/`, `locales/`, native
libraries) to `/opt/ghostline/app`, create the user and directories, and
install the unit from [deploy/ghostline.service](deploy/ghostline.service):

```bash
useradd --system --no-create-home ghostline
mkdir -p /etc/ghostline /var/log/ghostline
chown ghostline: /etc/ghostline /var/log/ghostline
cp deploy/ghostline.service /etc/systemd/system/
systemctl enable --now ghostline
```

For `mode: full` install `ffmpeg` (`apt install ffmpeg`).

### Docker

```bash
docker build -t ghostline -f ghostline/Dockerfile .
```

```yaml
services:
  ghostline:
    image: ghostline:latest
    restart: unless-stopped
    environment:
      - TZ=Europe/Berlin            # your time zone
    ports:
      - "8889:8889"                 # web interface
      # - "25:25"                   # only for goip.smtp
    volumes:
      - /srv/ghostline/config:/etc/ghostline
      - /srv/ghostline/log:/var/log/ghostline
```

The image runs as a non-root user (`$APP_UID`); the mounted directories must
be writable by it.

## Configuration

The configuration file is `/etc/ghostline/ghostline.yaml` (or a path passed as
the first argument). Keys are in `snake_case`. An unknown key stops the
service at startup, so a typo cannot silently disable an option. A legacy
`ghostline.json` from yetgsms next to it is converted automatically on first
start.

The configuration can also be edited in the web interface.

### Example

```yaml
mode: full                    # sms | full

locale:                       # en | ru; empty values fall back to default
  default: en
  web:
  telegram:
  transcript:                 # language of downloaded transcripts

telegram:
  token: 123456789:AAExampleTelegramBotToken
  chat_ids:
    - "100000001"

web:                          # empty user and password disable authentication
  port: 8889
  user: admin
  password: change-me

logger:
  file: true                  # also write SMS and calls to /var/log/ghostline/

gateways:
  - id: pbx
    type: quectel
    ip: 10.0.0.10
    ami_port: 5038
    user: ghostline
    password: ami-secret

channels:
  - name: personal
    gateway: pbx
    pattern: gsm1
    line: gsm1
    number: "+79001234567"    # shown in transcript headers
    calls:
      voice: true
      transcript: true
      missed: true
      chat_ids: []            # empty — telegram.chat_ids

calls:                        # mode: full only
  cdr_db:
    host: 10.0.0.10
    port: 3306
    database: asteriskcdrdb
    user: ghostline
    password: cdr-secret
  recordings_url: http://10.0.0.10/ghostline-rec/
  poll_seconds: 15
  transcribe:
    enabled: true
    url: http://10.0.0.20:8000/v1/audio/transcriptions
    model: deepdml/faster-whisper-large-v3-turbo-ct2
    language: ru
    prompt: "Names and terms that often occur in your calls."

goip:
  smtp:
    enabled: false
    port: 25
```

### Reference

| Key | Description |
|---|---|
| `mode` | `sms` (default) or `full` |
| `locale.default` | `en` (default) or `ru` |
| `locale.web` / `locale.telegram` / `locale.transcript` | Language of the web interface, Telegram messages and downloaded transcripts; empty — `locale.default` |
| `telegram.token` | Bot token |
| `telegram.chat_ids` | Chats that receive SMS and, unless a line has its own, calls. Only these chats can send SMS through the bot |
| `web.port` | Web interface port, default `8889` |
| `web.user` / `web.password` | HTTP Basic authentication; both empty — no authentication |
| `logger.file` | Write SMS and calls to `/var/log/ghostline/ghostline_YYYY-MM-DD.log` |
| `goip.smtp.enabled` / `goip.smtp.port` | Built-in SMTP server for GoIP gateways that deliver SMS by e-mail |

`gateways[]`:

| Field | Description |
|---|---|
| `id` | Unique name of the gateway, shown in the web interface |
| `type` | `yeastar`, `goip` or `quectel` |
| `ip` | Gateway address |
| `ami_port` | AMI port (`yeastar`, `quectel`) |
| `http_port` | HTTP port used for sending SMS (`yeastar`, `goip`) |
| `user` / `password` | AMI credentials (`yeastar`, `quectel`) or HTTP credentials (`goip`) |

`channels[]` (SIM lines):

| Field | Description |
|---|---|
| `name` | Line name, used in bot commands and in the interface; unique |
| `gateway` | `gateways[].id` this line belongs to |
| `pattern` | String incoming events are matched against |
| `line` | Port number (`yeastar`, `goip`) or `chan_quectel` device name (`gsm1`, …) |
| `number` | The line's own phone number, shown in transcript headers; optional |
| `calls.voice` / `calls.transcript` / `calls.missed` | What to send to Telegram for calls on this line |
| `calls.chat_ids` | Chats for this line's calls; empty — `telegram.chat_ids` |

`calls` (mode `full`):

| Field | Description |
|---|---|
| `cdr_db` | MySQL connection to `asteriskcdrdb`; a user with `SELECT` on `cdr` is enough |
| `recordings_url` | HTTP URL of `/var/spool/asterisk/monitor` on the PBX |
| `poll_seconds` | How often the CDR table is polled |
| `transcribe.url` / `model` / `language` | Transcription service |
| `transcribe.prompt` | Hint for the speech model: names and terms that are often misrecognized. The contact name of the caller is added automatically |

PBX-side setup for `mode: full` (database user, HTTP access to recordings,
recording each side of the call, transcription service) is described in
[docs/pbx-setup-calls.md](docs/pbx-setup-calls.md). AMI and dialplan setup for
`chan_quectel` is in [docs/quectel-setup.md](docs/quectel-setup.md).

### Contacts

Put a Google Contacts export (Google CSV) at `/etc/ghostline/contacts.csv`.
Numbers are matched regardless of formatting (`+7`, `8`, spaces, dashes).
All numbers are shown in E.164 form.

## Telegram bot

Only chats from `telegram.chat_ids` are accepted. To send an SMS, write three
lines:

```
personal
+79001234567
Message text
```

The first line is the line `name`, the second the recipient, the rest is the
text.

## Web interface

`http://<host>:8889`. Keep it inside a trusted network or behind
authentication: it gives access to messages, recordings and to the
configuration, which contains credentials.

Every tab has its own address (`/calls`, `/sms`, `/log`, `/status`,
`/config`, `/about`) with the filters in the query string, so a view can be
reloaded, bookmarked or shared, and the browser's Back and Forward buttons
work. `/` opens the tab used last.

- **Calls** (`mode: full`) — journal with filters (line, direction,
  recorded, missed, dates) and search by name, number or words from the
  conversation. A click on a row opens the transcript. Buttons: play, download
  the recording, download the transcript (PDF; plain text inside the opened
  transcript). **Transcribe again** re-runs transcription, for example after
  changing `transcribe.prompt`.
- **SMS** — history, filters, export, sending.
- **Log** — the text log, newest first.
- **Status** — state of gateways, Telegram and queues.
- **Config** — two editors for the same file:
  - **Form**: sections in a tree (general, Telegram, web, gateways, lines,
    calls, transcription, GoIP); gateways and lines can be added and removed.
    Only changed values are written, in place, so comments and the layout of
    the file stay as they are.
  - **YAML** (`/config/yaml`): the whole file with syntax highlighting.

  The file is checked before saving and the previous version is kept in
  `ghostline.yaml.bak`. **Save and restart** and **Restart** restart the
  service (relies on `Restart=always` in systemd or a restart policy in
  Docker); the page reloads when it is back.
- **About** — version and this README.

The footer shows the version and the build date. The version is
`Major.Minor.Patch` in `ghostline/version.txt`;
[scripts/bump-version.ps1](scripts/bump-version.ps1) raises the patch number
before a build when the sources changed since the previous one.

## Storage and reliability

- State is kept in `ghostline.db` (SQLite) next to the configuration file:
  SMS history, call journal, transcripts and queues.
- Outgoing Telegram messages and SMS go through persistent queues and are
  retried until they succeed, including across restarts.
- AMI connections reconnect automatically; a silent connection loss is
  detected by a keepalive and a receive timeout.
- Transcription has its own queue. If the service is unavailable, calls wait
  and are processed later. Requests made with **Transcribe again** are
  handled by a separate worker and do not wait for the background queue.
- When a Telegram chat does not accept voice messages, the recording is sent
  as an MP3 file.

## Hardware

[docs/hardware.md](docs/hardware.md) describes a working setup: a Topton X2E
mini PC (Intel N100) running Proxmox, with a Quectel EC200A-EU and a SIMCom
SIM7600G-H, Asterisk with a
patched `chan_quectel`, USB audio, the problems solved along the way, and a
cost and feature comparison with an off-the-shelf two-SIM gateway.

## License

Apache-2.0. The bundled IBM Plex Sans font is licensed under the SIL Open
Font License ([ghostline/web/fonts/OFL.txt](ghostline/web/fonts/OFL.txt)).
