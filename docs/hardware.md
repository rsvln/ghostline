# Hardware: GSM voice and SMS into Asterisk

A description of a working setup where two cellular modules act as GSM lines of
a FreePBX/Asterisk system, with ghostline on top. Voice in both directions, caller
ID and SMS work on both lines.

## Overview

| Part | What is used |
|---|---|
| Host | Topton X2E mini PC: Intel N100, 8 GB RAM is enough (Proxmox, the PBX and ghostline together use about 3 GB), one SATA SSD, 4× 2.5 GbE. It has an M.2 slot and a mini-PCIe slot for cellular modules (both are USB inside), plus SIM holders |
| Hypervisor | Proxmox VE 9 |
| PBX | FreePBX 17 / Asterisk 22 in a privileged LXC container |
| GSM driver | `chan_quectel` ([IchthysMaranatha fork](https://github.com/IchthysMaranatha/asterisk-chan-quectel)) built from source, with a small patch for the EC200A |
| Module 1 | Quectel **EC200A-EU** (mini-PCIe), USB ID `2c7c:6005` |
| Module 2 | SIMCom **SIM7600G-H** (M.2), USB ID `1e0e:9001` |
| ghostline | Separate unprivileged LXC container, systemd service |

Both modules appear on the same USB bus of the host. Each one is a composite
USB device with several serial interfaces (`option` driver), a network
interface, and — for the EC200A after configuration — USB audio interfaces.

## The two modules handle voice differently

| | SIM7600G-H | EC200A-EU |
|---|---|---|
| Platform | Qualcomm | ASR |
| AT port | interface 3 | interface 3 |
| Voice path | PCM over a dedicated serial interface (interface 4) | USB Audio Class: an ALSA card named `Android` |
| `chan_quectel` setting | `audio=<serial port>` | `quec_uac=1`, `alsadev=…`, `noqpcmv=1` (patch) |

The SIM7600 works with the stock driver. The EC200A needs the configuration and
the patch described below.

## Finding the AT port

Do not guess baud rates. The module's USB descriptor names the interfaces:

```
lsusb -v -d 2c7c:6005 | grep -E "bInterfaceNumber|iInterface"
```

For the EC200A:

| Interface | Descriptor label | Driver | AT commands |
|---|---|---|---|
| 0/1 | Mobile ECM Network Adapter | cdc_ether | — |
| 2 | Mobile Diag Interface | option | no |
| 3 | **Mobile AT Interface** | option | **yes** |
| 4 | Mobile MODEM Interface | option | yes |

The port answers at 115200 8N1, no flow control, commands end with `\r`.

Always address ports through `/dev/serial/by-path/…`: `/dev/ttyUSBn` numbers
change between reboots and when a module is reset. For example the EC200A AT
port is `/dev/serial/by-path/pci-0000:00:14.0-usb-0:6:1.3-port0`
(`usb-0:6` is the USB port of the slot, `1.3` the interface).

## EC200A: voice over USB

The EC200A has no `AT+QPCMV` (the usual Quectel "voice over USB serial"
command). On the ASR platform voice goes over USB Audio Class and is enabled
with `AT+QAUDCFG`:

```
AT+QAUDCFG="uacmode",1
AT+QAUDCFG="uactype",1
AT+QAUDCFG="uacrxsamp",8000
AT+CFUN=1,1
```

After the reset the module comes back with three additional audio
interfaces handled by `snd-usb-audio`, and an ALSA card `Android` appears.
Check that both directions are 8 kHz:

```
cat /proc/asound/card*/stream0      # Playback 8000 and Capture 8000
```

The factory value of `uacrxsamp` is 16000 while `uactxsamp` is 8000. This
asymmetry is the cause of the known `Rate not correct, requested 8000, got
16000` error; setting `uacrxsamp` to 8000 fixes it without changing the
driver. The settings survive a power cycle.

After `AT+CFUN=1,1` the ALSA card takes several seconds to reappear. Wait for
the USB device number to change in `lsusb`; `/proc/asound/cards` may still show
the old card for a while.

## `chan_quectel` patch for the EC200A

The stock driver rejects the EC200A for two reasons:

1. It probes `AT+QPCMV?` to decide whether the module supports voice. The
   EC200A answers `ERROR`, so the channel is marked as voiceless.
2. It prefixes `ATD` and `ATA` with `AT+QPCMV=0;+QPCMV=1,2;` even with
   `quec_uac=1`, so calls fail.

[chan_quectel-noqpcmv.patch](chan_quectel-noqpcmv.patch) adds a device option
`noqpcmv=1`. With it the failed probe is treated as "voice available", and
plain `ATD<number>;` / `ATA` are sent. Everything else uses the existing
`quec_uac=1` audio code.

The upstream source tree has CRLF line endings, while the patch has LF. Apply
it with whitespace differences ignored:

```
cd asterisk-chan-quectel
git apply --ignore-whitespace /path/to/chan_quectel-noqpcmv.patch
```

Build against your Asterisk version as described in the driver's README. One
`chan_quectel.so` serves all devices.

## `quectel.conf`

```ini
[general]
interval=15

; EC200A-EU — voice over USB audio
[gsm1]
data=/dev/serial/by-path/pci-0000:00:14.0-usb-0:6:1.3-port0
quec_uac=1
noqpcmv=1
alsadev=hw:CARD=Android,DEV=0
context=in-line1
group=1
rxgain=0
txgain=0
resetquectel=yes
initstate=start
autodeletesms=yes

; SIM7600G-H — voice over a serial port
[gsm2]
data=/dev/serial/by-path/pci-0000:00:14.0-usb-0:1:1.3-port0
audio=/dev/serial/by-path/pci-0000:00:14.0-usb-0:1:1.4-port0
context=in-line2
group=2
rxgain=0
txgain=0
resetquectel=yes
initstate=start
autodeletesms=yes
```

`autodeletesms=yes` is required: otherwise the SIM's SMS memory fills up and
SMS reception stops.

## Sound device permissions

`snd_pcm_open failed: No such device` for `hw:CARD=Android` is a permission
problem, not a missing device. Asterisk started with `-G asterisk` calls
`setgroups(0, NULL)` and drops supplementary groups, so adding the `asterisk`
user to `audio` has no effect. A udev rule on the host fixes it:

```
# /etc/udev/rules.d/99-ec200a-audio.rules
SUBSYSTEM=="sound", ATTRS{idVendor}=="2c7c", ATTRS{idProduct}=="6005", MODE="0666"
```

## Passing the modules into an LXC container

In `/etc/pve/lxc/<id>.conf`, in the main section (lines appended after a
snapshot section apply to the snapshot, not to the running container):

```
lxc.cgroup2.devices.allow: c 188:* rwm
lxc.cgroup2.devices.allow: c 116:* rwm
lxc.autodev: 1
lxc.hook.autodev: /var/lib/lxc/<id>/mount-hook.sh
lxc.mount.entry: /dev/serial dev/serial none bind,optional,create=dir
lxc.mount.entry: /dev/snd dev/snd none bind,optional,create=dir
```

Major 188 is `ttyUSB`, 116 is ALSA. The hook creates the `ttyUSB` nodes when
the container starts:

```sh
#!/bin/sh
for i in $(seq 0 15); do
  [ -e /dev/ttyUSB$i ] && mknod -m 666 ${LXC_ROOTFS_MOUNT}/dev/ttyUSB$i c 188 $i
done
exit 0
```

`/dev/serial` and `/dev/snd` are bind mounts and follow module resets without
restarting the container.

## FreePBX routing notes

- **Incoming.** Each device has its own context (`in-line1`, `in-line2`) that
  handles the `sms`/`ussd` service extensions and sends real calls to the
  destination extension with `Goto(from-did-direct,<ext>,1)`. Do not send
  trunk calls to `from-internal`: outbound routes are reachable from there.
- **Caller name.** `chan_quectel` sets `CALLERID(name)` to the device name, so
  phones show `gsm1`. Set it to the number before passing the call on:
  `Set(CALLERID(name)=${CALLERID(num)})`.
- **Outgoing line per extension.** Free FreePBX selects outbound routes only by
  the dialed number. To make each extension use its own line, give the
  extension a custom context (`from-internal-line1`) that includes its route
  before `from-internal`.
- **Number formats.** Outbound route patterns normalise `8…`, `7…`, `9…` and
  `+7…` to E.164 so the module always gets `+7…`.

## Writing the line's own number to the SIM

The driver reads the line's number with `AT+CNUM` and shows it in
`quectel show devices`. If the SIM does not have it, write it once. Stop the
channel first and talk to the AT port directly, because `quectel cmd` strips
double quotes:

```
asterisk -rx "quectel stop now gsm2"
# then on the AT port:
AT+CMEE=2
AT+CSCS="GSM"
AT+CPBS="ON"
AT+CPBW=1,"+79001234567",145,"MSISDN"
AT+CNUM
AT+CPBS="SM"
AT+CSCS="UCS2"
```

The driver keeps the module in UCS2; switch to GSM before writing text and
back afterwards.

## Other lessons

- `systemctl restart asterisk` does nothing useful on FreePBX; use
  `asterisk -rx "core restart now"`.
- Renaming sections in `quectel.conf` needs a full Asterisk restart:
  after `quectel reload now` the old instances keep the ports.
- Do not enable `AT+CLCC=1` on the SIM7600: the driver misparses call-end
  messages and the channel hangs in the ringing state.
- On Qualcomm-based Quectel modules `AT+QCFG="band"` accepts values without
  the `0x` prefix; with the prefix it answers `OK` and changes nothing. Read
  the value back after writing.
- Use `AT+CMEE=2` at the start of every AT session for readable errors.
- To check a complaint about echo, use a test context that answers, plays a
  beep and then only records. Asterisk sends nothing into the line, so
  whatever the caller hears is real echo. `Echo()` in a test dialplan produces
  echo on purpose.
- The remote side from the GSM module is much quieter than a SIP phone. This
  matters for recordings and transcription; ghostline balances the levels.
