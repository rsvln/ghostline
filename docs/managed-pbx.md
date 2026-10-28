# Managed PBX (`pbx.mode: managed`)

By default (`pbx.mode: freepbx`) FreePBX owns the PBX and ghostline only
connects to it. In `managed` mode ghostline writes the PBX part of a plain
Asterisk itself, from its own lines:

- every line with a `phone` gets a SIP phone (PJSIP endpoint);
- the SIM rings that phone;
- the phone calls out through that SIM;
- calls are recorded and land in the call journal as with FreePBX.

The PBX tab is where this is set up:

- modems: "+ Modem" picks a USB device of the Asterisk host from a list (model,
  USB port, VID:PID) and its AT and audio ports from combo boxes. Known models
  (Huawei E1550, Quectel EC200A and EC25, SIMCom SIM7600) are filled in.
  Only ports Asterisk may open and no other modem uses are offered. "✕"
  removes a modem that no line uses. The driver reloads gracefully, after the
  calls on it end;
- per modem line: the phone's extension (empty: no phone), dial set and media
  encryption, its registration and a "New secret" button. A new phone gets a
  secret on Apply; it is shown once;
- the SIP transport: TLS and UDP ports, external address, local networks,
  certificate and key;
- "+ Line": a new line on a modem the managed Asterisk already knows (chosen
  from a list that shows the model, operator and state; modems taken by a line
  are not offered), optionally with its phone. "✕" removes a line with its
  phone. Both save the config, apply the files and restart ghostline;
- failed SIP attempts and the TLS certificate's expiry.

Apply saves the changes into ghostline.yaml (the previous copy goes to `.bak`),
writes the files and reloads Asterisk; no restart is needed. Preview shows the
files with the unsaved changes. A line whose modem is on another PBX (its
gateway's address differs from `pbx.asterisk`) cannot get a phone; this is the
case for lines still on FreePBX during a move.

Everything else (SMS, Telegram, ntfy, the call journal, transcription,
monitoring) works the same in both modes. To go back, set `pbx.mode: freepbx`.

## What ghostline writes, and what it does not

ghostline writes its files into `pbx.conf_dir`, a directory shared with the
Asterisk host. Asterisk sees it as `pbx.asterisk_dir`.

| File | Contents |
|---|---|
| `pjsip_ghostline.conf` | TLS transport (and UDP if `udp_port` is set), NAT settings, one endpoint/auth/aor per phone |
| `extensions_ghostline.conf` | `gl-in-<device>`: calls from the SIM; `gl-out-<device>`: what the phone may dial; recording. A modem without a phone gets only the SMS/USSD stubs; calls to it are not answered |
| `quectel_ghostline.conf`, `dongle_ghostline.conf` | one section per modem of `pbx.modems`: ports by path, `context=gl-in-<device>` |

The Asterisk host writes `usb.json` there (`gl-usb-inventory`, every 30 s):
its USB devices with serial ports and sound cards, and whether Asterisk may
open them. It reads sysfs only and sends nothing to the modems.

On Apply ghostline:
1. writes the files that changed; the previous copies go to `.bak`;
2. over the AMI of the gateway `pbx.asterisk`, for what changed:
   `module reload res_pjsip.so`, `dialplan reload`,
   `quectel reload gracefully`, `dongle reload gracefully`.

Written once, not by ghostline
([scripts/asterisk-install.sh](../scripts/asterisk-install.sh) writes them,
see [asterisk-setup.md](asterisk-setup.md)):

- `pjsip.conf` and `extensions.conf` include the generated files:

  ```
  ; pjsip.conf
  [global]
  type=global
  user_agent=Asterisk
  #include /etc/asterisk/ghostline/pjsip_ghostline.conf

  ; extensions.conf
  [general]
  static=yes
  writeprotect=yes
  [globals]
  #include /etc/asterisk/ghostline/extensions_ghostline.conf
  ```

  There must be no other endpoints and no anonymous endpoint. Requests that
  match no phone are rejected.
- `quectel.conf` / `dongle.conf`: `[general]` (and `[defaults]` for
  chan_dongle), then `#include /etc/asterisk/ghostline/quectel_ghostline.conf`
  (`dongle_ghostline.conf`). No device sections by hand.

A modem needs a gateway of its driver (`type: quectel` / `dongle`) on the
managed Asterisk: ghostline handles that driver's SMS and monitoring through
it. "+ Modem" adds a missing one with the AMI login of `pbx.asterisk`
(`<id>-quectel`, `<id>-dongle`) and restarts ghostline.
- `manager.conf`: the AMI user of ghostline needs `command` and `reporting`
  in `write`, and `security` in `read` (for the failed attempts).
- CDR: [cdr-sqlite.md](cdr-sqlite.md).

## Settings

```yaml
pbx:
  mode: managed
  asterisk: pbx                       # gateway id: its AMI reloads Asterisk
  conf_dir: /srv/asterisk-conf        # as ghostline sees the shared directory
  asterisk_dir: /etc/asterisk/ghostline   # as Asterisk sees it
  tls:
    cert: keys/sip.crt                # relative: inside conf_dir
    key: keys/sip.key
    port: 5061
  udp_port: 0                         # 0: no plain UDP (LAN phones without TLS)
  external_address: sip.example.org   # public name or IP for NAT
  local_nets: [192.168.0.0/16]
  recordings_path: /var/spool/asterisk/monitor   # on the Asterisk side

channels:
  - name: home
    gateway: pbx
    pattern: gsm1
    line: gsm1
    phone:
      extension: "11"                 # also the SIP user name
      dial: ru                        # ru | ru+intl
      encryption: sdes                # sdes (SRTP) | no
      # secret: written by the PBX tab ("New secret")
```

- **Certificate:** a relative path is inside `conf_dir`. ghostline can then
  read it, show its expiry and warn 14 days ahead. An absolute path is used
  as is on the Asterisk side and is not checked.
- **Secret:** 24 letters and digits, generated on the PBX tab and shown once.
  It is not in the form, and the preview masks it.
- **`dial: ru`:**
  - `+7…`, `8…`, `7…` (11 digits) and `9…` (10 digits) go out as +7…;
  - 3–4 digit service numbers go out as dialed.
- **`ru+intl`** adds `+…`, `00…` and `810…`.
- **Phone to phone:** a phone can call the others by extension. These calls
  are not recorded.

## Security

- **Phones:** only TLS by default. With `encryption: sdes` the voice is SRTP;
  the keys go inside the TLS signalling.
- **Brute force:** fail2ban on the Asterisk host bans by the Asterisk security
  log. ghostline lists the failed attempts on the PBX tab. When one address
  fails 5 times in 10 minutes, it sends a message to the common Telegram
  chats and ntfy topic (once a day per address).
- **Values:** the generated files contain only validated values (digits for
  extensions, letters and digits for secrets, plain paths, host names and
  networks). Apply refuses anything else.

## CDR and recordings

- Incoming calls run on the modem channel and end on the phone's extension.
- Outgoing calls keep the number as dialed.
- Recordings go to `recordings_path/YYYY/MM/DD/` with the `-r`/`-t` legs and
  the name in `CDR(recordingfile)`.

This is the layout the call journal reads from FreePBX too, so the journal,
the transcription and the notifications work unchanged. For CDR without
MySQL see [cdr-sqlite.md](cdr-sqlite.md). Recordings can be read from a
shared directory (`calls.recordings_dir`).
