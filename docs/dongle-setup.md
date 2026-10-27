# Asterisk/FreePBX setup for `type: dongle`

[chan_dongle](https://github.com/wdoekes/asterisk-chan-dongle) drives Huawei
3G USB sticks (E1550, E173, E171, E352 and similar). chan_quectel is a fork of
it, so the setup is the same as in [quectel-setup.md](quectel-setup.md):
the same AMI user, the same stub context. Only the names differ:

| | `quectel` | `dongle` |
|---|---|---|
| config file | `quectel.conf` | `dongle.conf` |
| CLI | `quectel …` | `dongle …` |
| AMI | `QuectelNewSMS`, `QuectelShowDevices` | `DongleNewSMS`, `DongleShowDevices` |
| channel | `Quectel/gsm1-…` | `Dongle/hw1-…` |

Both drivers can run in one Asterisk. Tested with FreePBX 17 / Asterisk 22.10
and chan_dongle `7db8e75`, which builds on Asterisk 22 without changes.

## 1. The stick

1. Most Huawei sticks start as a CD-ROM (`12d1:1446` and similar in `lsusb`).
   Install `usb-modeswitch` on the host that owns the USB port. It switches
   them to modem mode, for example to `12d1:1003` or `12d1:1506`.
2. Find the AT ports: `ls -l /dev/serial/by-path/`, then send `ATI` to each
   port. Two of them answer, the modem port and the control port (PCUI).
   Give chan_dongle the control port as `data`.
3. Check the stick, with any terminal on the control port:
   - `AT^CARDLOCK?`: the first number is `2` when the stick is not locked to
     an operator, `1` when it is locked; a locked stick refuses other SIMs.
   - Voice: `ATI` lists `+FCLASS` in `+GCAP` on a firmware with voice, and
     chan_dongle shows `Voice: Yes` in `dongle show device state`. Do not go
     by `AT^CVOICE?`: an E1550 with working voice answers `^CVOICE:1` too.
     Many operator firmwares ship with voice off; it then needs another
     firmware. SMS and monitoring work either way.
   - `AT+CPIN?`: `READY`; "SIM failure" means no SIM or a bad contact.

## 2. Build

```
git clone https://github.com/wdoekes/asterisk-chan-dongle.git
cd asterisk-chan-dongle
./bootstrap
./configure --with-asterisk=/usr/include --with-astversion=<version> DESTDIR=/usr/lib/x86_64-linux-gnu/asterisk/modules
make && make install
asterisk -rx "module load chan_dongle.so"
```

## 3. `dongle.conf`

Always name the ports by path. Never rely on discovery by IMEI/IMSI: it opens
every `ttyUSB` and sends AT commands to it, including the ports of other
modems on the host.

```
[general]
interval=15

[defaults]
context=dongle-incoming
group=3
autodeletesms=yes
resetdongle=no
u2diag=-1
initstate=start

[hw1]
audio=/dev/serial/by-path/pci-0000:00:14.0-usb-0:7:1.0-port0
data=/dev/serial/by-path/pci-0000:00:14.0-usb-0:7:1.1-port0
```

`context` needs the same `sms`/`ussd` stubs as for chan_quectel (see
[quectel-setup.md](quectel-setup.md#1-a-stub-context-for-the-sms-service-channel)).

## 4. ghostline

The AMI user is the one from quectel-setup.md (`write` with `command` and
`reporting`). The gateway gets its own entry, even on the same PBX:

```yaml
gateways:
  - id: dongle1
    type: dongle
    ip: 10.0.0.10
    ami_port: 5038
    user: ghostline
    password: <password>

channels:
  - name: line3
    gateway: dongle1
    pattern: hw1
    line: hw1            # device name from dongle.conf
```

## 5. Line monitoring

The same as for `quectel`: registration, operator, signal, alerts.

- Signal: the bars come from RSSI; the sticks have no LTE.
- Network type: chan_dongle keeps it as `Mode`/`Submode` (GSM → 2G; WCDMA,
  HSDPA, HSUPA → 3G), but only from the stick's `^MODE` report, which comes
  only when the mode changes. So after a start the type stays empty, often
  for hours.
  [chan_dongle-sysinfo.patch](chan_dongle-sysinfo.patch) fixes this: each
  `DongleShowDevices` also queues `AT^SYSINFO`, and its answer fills
  `Mode`/`Submode`. The next poll a minute later shows the type.

  ```
  cd asterisk-chan-dongle
  git apply /path/to/ghostline/docs/chan_dongle-sysinfo.patch
  make && make install
  asterisk -rx "module unload chan_dongle.so"
  asterisk -rx "module load chan_dongle.so"
  ```
