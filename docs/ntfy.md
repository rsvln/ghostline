# ntfy: notifications and SMS without Telegram

[ntfy](https://ntfy.sh) is a small self-hosted push server: one binary, plain
HTTP, an Android app that connects to your own server directly (no Google
services). ghostline uses it as a second notification channel next to
Telegram, or instead of it:

- incoming and sent SMS, calls with the recording (MP3) and, a little later,
  the transcript in the same notification;
- messages typed in a topic in the ntfy app are sent back to ghostline: SMS to
  send and commands (`/status`, `/help`).

In the examples the ntfy server is `10.0.0.30` inside the network and
`https://ntfy.example.org` from outside.

## 1. Server

Any Linux host works; a small LXC container or VM is enough (ntfy uses about
20 MB of RAM). On Debian or Ubuntu, from the official repository
([other options](https://docs.ntfy.sh/install/), including Docker):

```bash
mkdir -p /etc/apt/keyrings
curl -fsSL -o /etc/apt/keyrings/ntfy.gpg https://archive.ntfy.sh/apt/keyring.gpg
echo "deb [arch=amd64 signed-by=/etc/apt/keyrings/ntfy.gpg] https://archive.ntfy.sh/apt stable main" \
  > /etc/apt/sources.list.d/ntfy.list
apt update && apt install ntfy
```

`/etc/ntfy/server.yml`:

```yaml
base-url: "https://ntfy.example.org"   # the external address; used in attachment links
listen-http: ":80"
behind-proxy: true                     # HTTPS is terminated by a reverse proxy

cache-file: "/var/cache/ntfy/cache.db"
cache-duration: "24h"                  # lets ghostline catch up after a restart

attachment-cache-dir: "/var/cache/ntfy/attachments"
attachment-file-size-limit: "15M"      # call recordings
attachment-total-size-limit: "1G"
attachment-expiry-duration: "72h"

auth-file: "/var/lib/ntfy/user.db"
auth-default-access: "deny-all"        # nothing is readable or writable without a login
```

```bash
mkdir -p /var/cache/ntfy/attachments /var/lib/ntfy
chown -R ntfy:ntfy /var/cache/ntfy /var/lib/ntfy
systemctl enable --now ntfy
```

### Users and access

Two users, both with read and write access to the ghostline topics. The
examples use the topic prefix `gl-` (`gl-home`, `gl-work`); any names work as
long as the access pattern matches them.

```bash
ntfy user add ghostline            # asks for a password; ghostline uses a token instead
ntfy user add phone                # for the ntfy app
ntfy access ghostline 'gl-*' rw
ntfy access phone 'gl-*' rw
ntfy token add --label ghostline ghostline   # prints tk_...; goes to clients.ntfy.token
```

Topics do not have to be created: a topic exists as soon as something is
published to it or someone subscribes. A topic outside the access pattern is
refused with 403.

Check: without a login the server answers 403, with the token it accepts a
message.

```bash
curl -s -o /dev/null -w "%{http_code}\n" -d test http://10.0.0.30/gl-test
curl -H "Authorization: Bearer tk_..." -d test http://10.0.0.30/gl-test
```

## 2. HTTPS and the reverse proxy

The phone reaches the server from outside, so it needs a public name, HTTPS
and a forwarded port. Any reverse proxy works; what matters for ntfy:

- **no response buffering** — the app and ghostline keep a long-lived
  connection; with buffering notifications arrive late or in batches;
- **long read timeouts** — ntfy sends a keepalive every 45 s;
- **WebSocket support** — used by the Android app;
- **request body size** — call recordings are uploaded as attachments.

Nginx Proxy Manager: a Proxy Host for `ntfy.example.org` → `http`,
`10.0.0.30`, port `80`; turn on *Websockets Support*; on the SSL tab request a
Let's Encrypt certificate with *Force SSL* and *HTTP/2*; on the Advanced tab:

```nginx
proxy_buffering off;
proxy_request_buffering off;
proxy_read_timeout 3m;
proxy_send_timeout 3m;
client_max_body_size 20m;
```

Plain nginx: the same directives in the `location`, plus
`proxy_http_version 1.1;` and the `Upgrade`/`Connection` headers for
WebSockets.

ghostline itself may use the internal address (`http://10.0.0.30`): it does
not depend on the proxy then. Attachment links in the notifications are built
by ntfy from `base-url`, so they are external either way.

## 3. Phone

1. Install the ntfy app (Google Play or F-Droid; both work with your own
   server, the F-Droid build has no Google services at all).
2. Settings → Manage users → add the user `phone` for
   `https://ntfy.example.org`.
3. "+" → Subscribe to topic → the topic name, *Use another server* with the
   same address. Repeat for every topic.
4. Keep *instant delivery* on for your server (the app shows a permanent
   notification for its connection service): otherwise messages may come
   minutes late.

## 4. ghostline

```yaml
clients:
  ntfy:
    url: http://10.0.0.30            # or https://ntfy.example.org
    token: tk_...
    topic:                           # common topic, for lines without their own

channels:
  - name: home
    ...
    clients:
      ntfy:
        topic: gl-home               # empty: clients.ntfy.topic
```

Each line goes to its own topic or to the common one, the same way as
Telegram chats (`clients.telegram.chat_ids`). Both channels can be on at the
same time; an empty `url` turns ntfy off. The same settings are in the web
interface: Config → Clients → ntfy, and Lines → line → Clients → ntfy.

The state of the connection is on the Status tab and in `/status`.

## 5. Using it

What comes to a topic:

- **SMS** — the title is the sender (with the contact name), the text is the
  message;
- **sent SMS** — `📤 <number>`, the text and where it was sent from;
- **calls** — the title is the call (direction, line, time), the recording is
  attached as MP3; when the transcript is ready, the same notification is
  replaced with the transcript, the recording stays;
- **missed calls** — a short message;
- replies to commands.

ghostline's own messages carry the tag `ghost`, shown as 👻; this is how
ghostline tells them apart from yours in the same topic.

To send an SMS, type in the topic:

- the topic serves **one line** — two lines, the recipient and the text:

  ```
  +79001234567
  Message text
  ```

- the topic serves **several lines** — three lines, the line name first:

  ```
  home
  +79001234567
  Message text
  ```

Commands: `/help` (the format for this topic and its lines), `/status`.

Messages typed while ghostline was not running are not lost: on start it
reads the topics from the last message it saw (ntfy keeps them for
`cache-duration`).

## Troubleshooting

| Symptom | Cause |
|---|---|
| 403 for ghostline or the app | The topic is outside the access pattern (`ntfy access ...`), or a wrong token/password |
| Notifications come late or in batches | Response buffering in the reverse proxy; or instant delivery is off in the app |
| ghostline's ntfy status goes red every few minutes | The proxy closes long connections: raise `proxy_read_timeout` |
| A recording is not attached | The request body is limited by the proxy (`client_max_body_size`) or by `attachment-file-size-limit` |
| Attachment links point to an internal address | `base-url` in `server.yml` is not the external address |
