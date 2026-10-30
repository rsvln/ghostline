# Plain Asterisk for `pbx: managed`

[scripts/asterisk-install.sh](../scripts/asterisk-install.sh) sets up a
plain Asterisk that ghostline manages ([managed-pbx.md](managed-pbx.md)),
without FreePBX, MySQL or a web server. Tested on Debian 13 in a Proxmox LXC
container.

What it installs:

- Asterisk 22 from source, with SRTP and `cdr_sqlite3_custom`. It is a portable
  build without CSV CDR and without unused channel drivers.
- `chan_quectel` with
  [chan_quectel-noqpcmv.patch](chan_quectel-noqpcmv.patch) and
  [chan_quectel-line-monitoring.patch](chan_quectel-line-monitoring.patch).
- `chan_dongle` with [chan_dongle-sysinfo.patch](chan_dongle-sysinfo.patch).
- fail2ban: the stock `asterisk` filter on the Asterisk security log. It bans
  after 5 failures in 10 minutes, for 1 hour, on all ports.
- The base configs (see below) and a systemd unit. Asterisk runs as the user
  `asterisk`, in the groups `dialout` and `audio` (modem ports and UAC sound
  cards).

The drivers are pinned to the commits the patches are made for. The script
can run again: it never overwrites an existing config, and it rebuilds only
for another Asterisk version or with `--rebuild`.

## 1. Run

From a ghostline checkout (the patches are taken from `docs/`):

```
git clone https://github.com/rsvln/ghostline.git
bash ghostline/scripts/asterisk-install.sh --ami-permit 10.0.0.11/32
```

`--ami-permit` is the address of ghostline: only it may log in to AMI. The
other options are listed by `--help`:

- `--conf-dir`: default `/etc/asterisk/ghostline`, `pbx.asterisk_dir` in
  ghostline;
- `--cdr-dir`: default `/var/spool/asterisk/gl-cdr`;
- `--sip-name`: the name in the self-signed certificate;
- `--version`: another Asterisk 22 release;
- `--no-quectel` / `--no-dongle`: skip a driver.

The build takes 10–20 minutes. At the end the script shows the module check,
the fail2ban jail and the AMI password of the user `ghostline`. The password
is shown only when `manager.conf` is created.

## 2. What is written

| File | Contents |
|---|---|
| `pjsip.conf` | `[global]` and `#include <conf-dir>/pjsip_ghostline.conf` |
| `extensions.conf` | `#include <conf-dir>/extensions_ghostline.conf` |
| `manager.conf` | AMI on 5038, user `ghostline` limited to `--ami-permit`. `read` includes `security`; `write` includes `command` and `reporting` |
| `cdr.conf`, `cdr_sqlite3_custom.conf` | CDR into `<cdr-dir>/cdr.db` only ([cdr-sqlite.md](cdr-sqlite.md)). `unanswered=yes`: otherwise a call to a phone that is not registered leaves no CDR and the journal misses it |
| `logger.conf` | `messages` and `security` logs (fail2ban reads `security`) |
| `modules.conf` | autoload without the CSV/manager CDR and unused drivers |
| `rtp.conf` | RTP ports 10000–20000 |
| `quectel.conf`, `dongle.conf` | `[general]` / `[defaults]` and `#include` of the files ghostline writes; the modems are added in ghostline's settings |
| `gl-usb-inventory` (timer, every 30 s) | writes `<conf-dir>/usb.json`, the USB modems ghostline offers; reads sysfs only |
| `<conf-dir>/keys/sip.crt`, `sip.key` | self-signed certificate, so TLS starts |

`pjsip_ghostline.conf` and `extensions_ghostline.conf` start as empty
placeholders. ghostline replaces them when its settings are saved.

There are no sample configs, so at start Asterisk logs "Unable to load config
file" for modules that are not used here (ARI, parking, confbridge and
others). These errors are expected.

## 3. Modems

The modems are added in ghostline's settings (Gateways → gateway → Modems,
"+ Modem"): the USB device and
its ports are chosen from lists, known models are filled in. In a container
the USB devices must be passed through first (below); a modem Asterisk cannot
open is listed as "not passed to Asterisk" and cannot be chosen.

## 4. Shared directories (Proxmox example)

ghostline and Asterisk must see three directories. With two containers on
one host, bind-mount host directories into both. Create them on the host and
give them to the `asterisk` user of the container. In an unprivileged
container uid N is uid 100000+N on the host.

```
# on the host; 100999 = asterisk (999) in the Asterisk container
mkdir -p /srv/gl-share/conf /srv/gl-share/cdr /srv/gl-share/rec
chown 100999:100999 /srv/gl-share/conf /srv/gl-share/cdr /srv/gl-share/rec
chmod 2775 /srv/gl-share/conf

pct set <asterisk ct> -mp0 /srv/gl-share/conf,mp=/etc/asterisk/ghostline
pct set <asterisk ct> -mp1 /srv/gl-share/cdr,mp=/var/spool/asterisk/gl-cdr
pct set <asterisk ct> -mp2 /srv/gl-share/rec,mp=/var/spool/asterisk/monitor

pct set <ghostline ct> -mp0 /srv/gl-share/conf,mp=/srv/asterisk-conf
pct set <ghostline ct> -mp1 /srv/gl-share/cdr,mp=/srv/asterisk-cdr,ro=1
pct set <ghostline ct> -mp2 /srv/gl-share/rec,mp=/srv/asterisk-rec,ro=1
```

ghostline writes into the conf directory, so its user must be able to write
there. Either run ghostline in the same group, or give the directory to the
ghostline user and add `asterisk` to its group. Asterisk only reads it.

Mount the directories **before** running the script, or copy the
placeholders and `keys/` into the mounted directory afterwards. The script
writes into whatever is mounted at `--conf-dir`.

USB modems in an unprivileged LXC container (Proxmox example). Inside the
container `/dev` nodes cannot be created, so the host's nodes are bind-mounted,
and a udev rule on the host gives the chosen modems to the container's
`dialout` group (gid 20, on the host 100020). Ports of other modems stay
`root:dialout` on the host and closed to the container, so a modem used by
another PBX cannot be taken by mistake.

```
# on the host: one line per modem, by its USB path (udevadm info -q property -n /dev/ttyUSBn | grep ID_PATH)
cat > /etc/udev/rules.d/99-gl-modems.rules <<'EOF'
SUBSYSTEM=="tty", KERNEL=="ttyUSB*", ENV{ID_PATH}=="pci-0000:00:14.0-usb-0:7:*", GROUP="100020", MODE="0660"
EOF
udevadm control --reload && udevadm trigger --action=change --subsystem-match=tty

# /etc/pve/lxc/<asterisk ct>.conf
lxc.cgroup2.devices.allow: c 188:* rwm
lxc.mount.entry: /dev/serial dev/serial none bind,optional,create=dir
lxc.mount.entry: /dev/ttyUSB0 dev/ttyUSB0 none bind,optional,create=file
# ... the same for ttyUSB1 to ttyUSB15
```

A UAC sound card (Quectel EC200A) is passed the same way: `c 116:* rwm`, a
bind mount of `/dev/snd`, and a rule with `SUBSYSTEM=="sound"` and
`GROUP="100029"` (`audio`). Restart the container. Within 30 s the Status
tab lists the modem as a free USB modem, and "+ Modem" offers it.

## 5. ghostline

```yaml
gateways:
  - id: pbx
    type: asterisk
    ip: 10.0.0.12            # the Asterisk container
    ami_port: 5038
    user: ghostline
    password: <from the script>
    pbx: managed
    managed:
      conf_dir: /srv/asterisk-conf
      asterisk_dir: /etc/asterisk/ghostline
      sip:
        tls:
          cert: keys/sip.crt
          key: keys/sip.key
          port: 5061
        external_address: sip.example.org
        local_nets: [10.0.0.0/8]

calls:
  cdr_sqlite: /srv/asterisk-cdr/cdr.db
  recordings_dir: /srv/asterisk-rec
```

Then in Settings: add the modems ("+ Modem"), the lines on them and their
phones, and save; each new phone's secret is shown once. Check in Asterisk:

```
asterisk -rx "pjsip show transports"
asterisk -rx "pjsip show endpoints"
asterisk -rx "dialplan show gl-in-gsm1"
```

## 6. Certificate

The self-signed certificate is only there so that TLS starts; phones
usually refuse it. Put a real one at the same paths, `keys/sip.crt` (with the
chain) and `keys/sip.key`, readable by `asterisk`. For example, use acme.sh
with a DNS challenge and a deploy hook that copies the files and runs
`asterisk -rx "module reload res_pjsip.so"`. ghostline shows the expiry on
the Status tab and warns 14 days ahead.

## 7. Firewall and NAT

From outside only SIP TLS (`managed.sip.tls.port`) and the RTP range
(10000–20000/udp) need to be forwarded. AMI (5038) must not be reachable from
outside; `manager.conf` permits only ghostline anyway.

Check fail2ban:

```
fail2ban-client status asterisk
tail /var/log/asterisk/security
```
