# Managed PBX (`pbx: managed`)

An Asterisk gateway (`type: asterisk`) is by default run by FreePBX
(`pbx: freepbx`): ghostline only connects to it. With `pbx: managed` ghostline
writes the PBX part of a plain Asterisk itself, from its own lines:

- every line with a `phone` gets a SIP phone (PJSIP endpoint);
- the SIM rings that phone;
- the phone calls out through that SIM;
- calls are recorded and land in the call journal as with FreePBX.

Everything is set up on the Settings tab, under the gateway and its lines:

- **Gateways → gateway → Managed PBX** (`managed.*`): the shared directory,
  the SIP transport (TLS and UDP ports, external address, local networks,
  certificate and key), the recordings directory;
- **Gateways → gateway → Modems**: "+ Modem" picks a USB device of the
  Asterisk host from a list (model, USB port, VID:PID) and its AT and audio
  ports from combo boxes. Known models (Huawei E1550, Quectel EC200A and EC25,
  SIMCom SIM7600) are filled in. Only ports Asterisk may open and no other
  modem uses are offered. "✕" removes a modem that no line uses. Both take
  effect at once; the driver reloads gracefully, after the calls on it end;
- **Lines → line**: the gateway and one of its modems (a combo box);
- **Lines → line → Phone**: the extension (empty: no phone), dial set and
  media encryption, and a "New secret" button. A new phone gets a secret on
  save; it is shown once.

Save writes ghostline.yaml (the previous copy goes to `.bak`), then the
Asterisk files, and reloads Asterisk. Only one gateway can be managed; a line
on another gateway (for example still on FreePBX during a move) cannot have a
phone.

The live state is on the Status tab: per gateway AMI and the certificate's
expiry, per modem the driver state, model, USB port and line, the host's USB
modems not used yet, the phones' registration, and failed SIP attempts.

Everything else (SMS, Telegram, ntfy, the call journal, transcription,
monitoring) works the same in both modes. To go back, set `pbx: freepbx`.

## What ghostline writes, and what it does not

ghostline writes its files into `managed.conf_dir`, a directory shared with
the Asterisk host. Asterisk sees it as `managed.asterisk_dir`.

| File | Contents |
|---|---|
| `pjsip_ghostline.conf` | TLS transport (and UDP if `udp_port` is set), NAT settings, one endpoint/auth/aor per phone |
| `extensions_ghostline.conf` | `gl-in-<device>`: calls from the SIM; `gl-out-<device>`: what the phone may dial; recording. A modem without a phone gets only the SMS/USSD stubs; calls to it are not answered |
| `quectel_ghostline.conf`, `dongle_ghostline.conf` | one section per modem of the gateway's `modems`: ports by path, `context=gl-in-<device>` |

The Asterisk host writes `usb.json` there (`gl-usb-inventory`, every 30 s):
its USB devices with serial ports and sound cards, and whether Asterisk may
open them. It reads sysfs only and sends nothing to the modems.

On save ghostline:
1. writes the files that changed; the previous copies go to `.bak`;
2. over the gateway's AMI, for what changed:
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

- `manager.conf`: the AMI user of ghostline needs `command` and `reporting`
  in `write`, and `security` in `read` (for the failed attempts).
- CDR: [cdr-sqlite.md](cdr-sqlite.md).

## Settings

```yaml
gateways:
  - id: pbx
    type: asterisk
    ip: 10.0.0.12
    ami_port: 5038
    user: ghostline
    password: <AMI secret>
    pbx: managed
    managed:
      conf_dir: /srv/asterisk-conf        # as ghostline sees the shared directory
      asterisk_dir: /etc/asterisk/ghostline   # as Asterisk sees it
      recordings_path: /var/spool/asterisk/monitor   # on the Asterisk side
      sip:
        tls:
          cert: keys/sip.crt              # relative: inside conf_dir
          key: keys/sip.key
          port: 5061
          acme:                           # Let's Encrypt, see below
            enabled: true
            domain: ""                    # empty: external_address
            http_port: 80
        udp_port: 0                       # 0: no plain UDP (LAN phones without TLS)
        external_address: sip.example.org # public name or IP for NAT
        local_nets: [192.168.0.0/16]
    modems:
      - name: gsm1
        driver: quectel                   # quectel | dongle
        data: /dev/serial/by-path/...     # the AT port
        audio: /dev/serial/by-path/...    # or alsadev: for a UAC modem
        noqpcmv: false

lines:
  - name: home
    gateway: pbx
    pattern: gsm1
    modem: gsm1
    phone:
      extension: "11"                 # also the SIP user name
      dial: ru                        # ru | ru+intl
      encryption: sdes                # sdes (SRTP) | no
      # secret: generated on save
```

- **Certificate:** a relative path is inside `conf_dir`. ghostline can then
  read it, show its expiry and warn 14 days ahead. An absolute path is used
  as is on the Asterisk side and is not checked.
- **Let's Encrypt (`sip.tls.acme`):** `gl-acme` on the Asterisk host gets the
  certificate with acme.sh (HTTP-01, standalone) into `cert` / `key` and
  renews it when acme.sh says it is due (about 30 days before expiry).
  - Port 80 from outside must reach the Asterisk host on `http_port`.
  - The domain is `domain`, or `sip.external_address` when that is a name.
  - Needs relative `cert` / `key` (inside `conf_dir`).
  - ghostline writes `<conf_dir>/acme_ghostline.json`; the host reports in
    `<conf_dir>/acme.json`. A failed renewal is sent to the common Telegram
    chats and ntfy topic; after a failure the host waits 6 hours before the
    next try (Let's Encrypt limits failed validations).
  - Status tab, "SIP certificate": the issuer, the expiry, the next renewal,
    the last error and "Renew now" (`<conf_dir>/acme_request.json`).
  - A certificate acme.sh already has for the domain is taken over, not
    issued again.
  - With a DNS challenge set up by hand on the host, leave `acme` off; the
    expiry is still shown and warned about.
- **Secret:** 24 letters and digits, generated on save (or by "New secret")
  and shown once.
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
  log, on all ports. The rule is in `gateways[].managed.fail2ban`
  (form: the gateway → Managed PBX → fail2ban):

  ```yaml
  fail2ban:
    max_retry: 5          # failures ...
    find_minutes: 10      # ... within this time
    ban_minutes: 60
    ban_growth: true      # the same address again: x24, then x168 (1 h, 1 day, 1 week)
    ignore_ip: []         # addresses or networks never banned
  ```

  - ghostline writes these values to `<conf_dir>/fail2ban_ghostline.json`.
    `gl-fail2ban` on the Asterisk host (installed by `asterisk-install.sh`)
    builds the jail from them. It accepts only numbers and addresses, so
    ghostline cannot put raw fail2ban config there. ghostline's own address
    (`--ami-permit`) is never banned. The ban history is kept for 30 days,
    which is what makes the growing ban work.
  - The Status tab shows the failed attempts, the rule and the banned
    addresses with "Unban". Unban writes `<conf_dir>/fail2ban_request.json`;
    the host handles it within seconds. The host reports back in
    `<conf_dir>/fail2ban.json` every 30 s.
  - When one address fails 5 times in 10 minutes, ghostline sends a message to
    the common Telegram chats and ntfy topic (once a day per address). It says
    whether the accounts tried are yours (a wrong password) or do not exist
    (a scanner guessing names), and until when fail2ban banned the address.
- **Values:** the generated files contain only validated values (digits for
  extensions, letters and digits for secrets, plain paths, host names and
  networks). Save refuses anything else.

## CDR and recordings

- Incoming calls run on the modem channel and end on the phone's extension.
- Outgoing calls keep the number as dialed.
- Recordings go to `managed.recordings_path/YYYY/MM/DD/` with the `-r`/`-t` legs and
  the name in `CDR(recordingfile)`.

This is the layout the call journal reads from FreePBX too, so the journal,
the transcription and the notifications work unchanged. For CDR without
MySQL see [cdr-sqlite.md](cdr-sqlite.md). Recordings can be read from a
shared directory (`system.calls.recordings_dir`).
