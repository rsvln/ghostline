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
- [Telegram and ntfy](#telegram-and-ntfy)
- [Web interface](#web-interface)
- [Storage and reliability](#storage-and-reliability)
- [Hardware](#hardware)
- [License](#license)

## Features

**SMS** (`mode: sms`, default)

- Gateways: Yeastar (AMI), GoIP (SMTP + HTTP), Quectel/SIMCom modules
  through `chan_quectel` and Huawei 3G sticks through `chan_dongle` in
  Asterisk/FreePBX (AMI). Several gateways of the same type are supported.
- Two notification channels, both optional and usable together: Telegram and
  [ntfy](https://ntfy.sh) (a self-hosted push server, works without Google
  services and without access to Telegram). Each line goes to its own
  chats/topic or to common ones.
- Incoming SMS are forwarded with the sender's name resolved from a Google
  Contacts export.
- Outgoing SMS from a Telegram chat, an ntfy topic or the web interface,
  through a persistent queue.
- SMS history in SQLite, with search and export.
- Line monitoring (`quectel`, `dongle`): registration, operator, network type, signal on
  the Status tab and in `/status`; a message when a line loses the network
  and when it is back.

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
- Telegram and ntfy: the recording right after the call; the transcript is
  added to the same message when it is ready. Optional notifications about
  missed calls.
- Transcript download as PDF (same look as in the web interface) or plain
  text; full-text search across transcripts.

Interface and messages are available in English and Russian.

## How it works

```
GSM gateway / Asterisk+chan_quectel ──AMI──▶ ghostline ◀─▶ Telegram, ntfy
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
- A Telegram bot token and/or an ntfy server (at least one, to get the
  notifications).
- Linux x64 (systemd service) or Docker.
- For `mode: full`: FreePBX/Asterisk with call recording enabled, `ffmpeg` on
  the ghostline host, and optionally a transcription service.

## Installation

Every release on the [Releases](https://github.com/rsvln/ghostline/releases)
page has self-contained archives for `linux-x64` and `linux-arm64` and a
Docker image on `ghcr.io/rsvln/ghostline`.

### systemd: LXC container, VM or bare metal (recommended)

An LXC container (for example on Proxmox) is an ordinary Linux system for
ghostline, so the same archive works there, in a VM and on a physical host.
The binary is self-contained: no .NET is needed on the machine.

```bash
curl -fsSL https://github.com/rsvln/ghostline/releases/latest/download/ghostline-linux-x64.tar.gz | tar xz
sudo ./ghostline/install.sh
```

On the first run [install.sh](deploy/install.sh) creates the `ghostline`
user, `/etc/ghostline/ghostline.yaml` from
[the example](deploy/ghostline.example.yaml), `/var/log/ghostline` and the
systemd unit. Edit the config and start the service:

```bash
systemctl start ghostline
journalctl -u ghostline -f
```

To update, unpack a newer archive and run `install.sh` again: it replaces
`/opt/ghostline/app`, keeps the previous version in `/opt/ghostline/app.old`
and rolls back automatically if the new version does not start.

For `mode: full` install `ffmpeg` (`apt install ffmpeg`).

### Docker

```yaml
services:
  ghostline:
    image: ghcr.io/rsvln/ghostline:latest
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

Put `ghostline.yaml` into the config directory first
([example](deploy/ghostline.example.yaml)). The image runs as a non-root user
(`$APP_UID`); the mounted directories must be writable by it.

### Building from source

Requires the .NET 10 SDK:

```bash
dotnet publish ghostline/ghostline.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -o publish
docker build -t ghostline -f ghostline/Dockerfile .
```

GitHub Actions ([.github/workflows](.github/workflows)) build both on every
push to `master`, start each with the example config as a smoke test, push the
image and, when `ghostline/version.txt` has a new version, create a tag and a
release with the archives.

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

clients:                      # notification channels, both optional
  telegram:                   # empty token — Telegram is off
    token: 123456789:AAExampleTelegramBotToken
    chat_ids:                 # common chats, for lines without their own
      - "100000001"
  ntfy:                       # empty url — ntfy is off
    url: https://ntfy.example.org
    token: tk_exampleaccesstoken
    topic:                    # common topic, for lines without their own

web:                          # empty user and password disable authentication
  port: 8889
  user: admin
  password: change-me

logger:
  level: info                 # system log: error | warning | info | debug
  file: false                 # also write SMS and calls to /var/log/ghostline/ (text files)

gateways:
  - id: pbx
    type: asterisk
    ip: 10.0.0.10
    ami_port: 5038
    user: ghostline
    password: ami-secret
    modems:                   # as named in quectel.conf / dongle.conf
      - name: gsm1
        driver: quectel

lines:
  - name: personal
    gateway: pbx
    pattern: gsm1
    modem: gsm1
    number: "+79001234567"    # optional; used when the SIM does not report its own number
    clients:                  # optional; without it the common chats and topic
      telegram:
        chat_ids: []          # empty — clients.telegram.chat_ids
      ntfy:
        topic: gl-personal    # empty — clients.ntfy.topic
    calls:
      voice: true
      transcript: true
      missed: true
      chat_ids: []            # chats for calls only; empty — the line's chats

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
| `clients.telegram.token` | Bot token; empty — Telegram is off |
| `clients.telegram.chat_ids` | Common chats, for lines without their own |
| `clients.ntfy.url` | ntfy server; empty — ntfy is off |
| `clients.ntfy.token` | ntfy access token with read and write access to the topics |
| `clients.ntfy.topic` | Common topic, for lines without their own |
| `web.port` | Web interface port, default `8889` |
| `web.user` / `web.password` | HTTP Basic authentication; both empty — no authentication |
| `logger.level` | System log level: `error`, `warning`, `info` (default) or `debug`. The log goes to journald with the level (`journalctl -p warning -u ghostline`), to `/var/log/ghostline/system-YYYY-MM-DD.log` (14 days) and to the System tab |
| `logger.file` | Also write SMS and calls to `/var/log/ghostline/ghostline_YYYY-MM-DD.log`, text files for other systems. The PBX tab does not need them |
| `goip.smtp.enabled` / `goip.smtp.port` | Built-in SMTP server for GoIP gateways that deliver SMS by e-mail |
| `monitor.alert_minutes` | A monitored line without network this long gets a message in its chats/topic, and another when it is back; `0` — no messages. Default `2` |

A client is on when its section is there (with a token / url); remove the
section to turn it off. A top-level `telegram` section of older configs is
still read as `clients.telegram` and is moved there when the config is saved
from the web interface. Files of versions before the managed PBX (`channels`,
gateway types `quectel` / `dongle`, a top-level `pbx` section) are converted
once on start, keeping the comments; the old file stays as
`ghostline.yaml.bak-v2`.

`gateways[]`:

| Field | Description |
|---|---|
| `id` | Unique name of the gateway, shown in the web interface |
| `type` | `asterisk` (with chan_quectel / chan_dongle modems), `yeastar` or `goip` |
| `ip` | Gateway address |
| `ami_port` | AMI port (`asterisk`, `yeastar`) |
| `http_port` | HTTP port used for sending SMS (`yeastar`, `goip`) |
| `user` / `password` | AMI credentials (`asterisk`, `yeastar`) or HTTP credentials (`goip`) |
| `monitor` | `asterisk`: poll the modems' registration, operator, network type and signal; default `true`. The AMI user needs `reporting` in `write`, see [docs/quectel-setup.md](docs/quectel-setup.md#4-line-monitoring) |
| `pbx` | `asterisk`: `freepbx` (default): FreePBX owns the PBX, ghostline only connects. `managed`: ghostline writes the PJSIP, dialplan and modem files of a plain Asterisk, see [docs/managed-pbx.md](docs/managed-pbx.md); one gateway at most |
| `managed.*` | `pbx: managed` only: `conf_dir`, `asterisk_dir`, `recordings_path`, `sip.tls`, `sip.udp_port`, `sip.external_address`, `sip.local_nets`, see [docs/managed-pbx.md](docs/managed-pbx.md#settings) |
| `modems[]` | `asterisk`: its modems. `name` (device name in quectel.conf / dongle.conf) and `driver` (`quectel` / `dongle`); with `pbx: managed` also the ports `data`, `audio` or `alsadev`, and `noqpcmv` |

`lines[]` (SIM lines):

| Field | Description |
|---|---|
| `name` | Line name, used in bot commands and in the interface; unique |
| `gateway` | `gateways[].id` this line belongs to |
| `pattern` | String incoming events are matched against |
| `modem` | `asterisk`: one of the gateway's `modems[].name` |
| `port` | `yeastar`, `goip`: port number |
| `number` | The line's own phone number, for the Status tab and transcript headers; optional. A number the SIM reports itself (`asterisk` modems) takes precedence |
| `clients` | Clients of the line: notifications go only to the ones listed. No `clients` section — every client that is on; `clients: {}` — nowhere |
| `clients.telegram.chat_ids` | Telegram chats of the line (SMS and calls); empty — the common ones |
| `clients.ntfy.topic` | ntfy topic of the line; empty — the common one |
| `calls.voice` / `calls.transcript` / `calls.missed` | What to send for calls on this line |
| `calls.chat_ids` | Telegram chats for this line's calls only; empty — the line's chats |
| `phone.extension` / `dial` / `encryption` | A line on the gateway with `pbx: managed` only: the line's SIP phone, see [docs/managed-pbx.md](docs/managed-pbx.md#settings) |

`calls` (mode `full`):

| Field | Description |
|---|---|
| `cdr_db` | MySQL connection to `asteriskcdrdb`; a user with `SELECT` on `cdr` is enough |
| `cdr_sqlite` | Instead of `cdr_db`: the SQLite file Asterisk writes with `cdr_sqlite3_custom`, in a directory shared with the PBX ([docs/cdr-sqlite.md](docs/cdr-sqlite.md)); wins over `cdr_db` |
| `recordings_url` | HTTP URL of `/var/spool/asterisk/monitor` on the PBX |
| `recordings_dir` | Instead of `recordings_url`: that directory itself, shared with the PBX; wins over `recordings_url` |
| `poll_seconds` | How often the CDR table is polled |
| `transcribe.url` / `model` / `language` | Transcription service |
| `transcribe.prompt` | Hint for the speech model: names and terms that are often misrecognized. The contact name of the caller is added automatically |

PBX-side setup for `mode: full` (database user, HTTP access to recordings,
recording each side of the call, transcription service) is described in
[docs/pbx-setup-calls.md](docs/pbx-setup-calls.md). AMI and dialplan setup for
`chan_quectel` is in [docs/quectel-setup.md](docs/quectel-setup.md), for
`chan_dongle` in [docs/dongle-setup.md](docs/dongle-setup.md). A plain
Asterisk without FreePBX, for `pbx: managed`, is set up by
[scripts/asterisk-install.sh](scripts/asterisk-install.sh), see
[docs/asterisk-setup.md](docs/asterisk-setup.md).

### Contacts

Put a Google Contacts export (Google CSV) at `/etc/ghostline/contacts.csv`.
Numbers are matched regardless of formatting (`+7`, `8`, spaces, dashes).
All numbers are shown in E.164 form.

## Telegram and ntfy

Both channels work the same way. Each line goes to its own Telegram chats and
ntfy topic, or to the common ones; a chat or a topic can serve one line or
several.

Incoming SMS, sent SMS and calls come to the chats and topics of their line.
For a call with a recording, the recording comes first and the transcript is
added to the same message when it is ready (in ntfy the notification is
replaced).

To send an SMS, write a message in a chat or topic:

- when it serves **one line**, two lines: the recipient and the text;

  ```
  +79001234567
  Message text
  ```

- when it serves **several lines**, three lines: the line `name`, the
  recipient and the text.

  ```
  personal
  +79001234567
  Message text
  ```

Commands: `/help` (the format above for this chat or topic, the lines),
`/status` (state of gateways, channels and queues). Telegram shows them in the
bot menu. Only chats and topics of the configured lines are served.

### ntfy

Setting up the ntfy server (users and access, HTTPS behind a reverse proxy,
the phone app) and how ghostline uses it is described in
[docs/ntfy.md](docs/ntfy.md).

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
- **SMS** — history with the same filters (line, direction, search by number,
  contact or text, dates); a click on a row shows the whole text. Export, sending.
- **PBX** — SMS and calls of all lines in time order, newest first (from the database),
  with the same filters plus the kind (SMS / calls); recordings and transcripts open here too.

All three lists share the toolbar and the row look, load the newest 500 matching rows
and can refresh themselves (off / 5 / 10 / 30 s).
- **System** — what ghostline itself does: start, connections, lines losing network, PBX changes,
  failures. Filters by level (errors / warnings / info / debug) and area.
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
