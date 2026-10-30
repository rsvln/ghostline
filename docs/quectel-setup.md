# Asterisk/FreePBX setup for chan_quectel (`driver: quectel`)

How to connect ghostline to `chan_quectel` in Asterisk/FreePBX. Tested with
FreePBX 17 / Asterisk 22 and Quectel EC200A-EU / SIMCom SIM7600G-H modules.
In the examples the PBX is `10.0.0.10` and ghostline runs on `10.0.0.11`.

## 1. A stub context for the SMS service channel

When an SMS arrives, `chan_quectel` creates a service Local channel in the
device's context with `Exten: sms` (and `ussd` for USSD). Without a handler
for these extensions the "call" falls through to the context's fallback and
can ring a real extension.

`/etc/asterisk/extensions_custom.conf`:

```
[quectel-incoming]
exten => sms,1,NoOp(SMS from ${CALLERID(num)})
 same => n,Hangup()
exten => ussd,1,NoOp(USSD)
 same => n,Hangup()
include => from-trunk
```

`/etc/asterisk/quectel.conf`, in each device section:

```
context=quectel-incoming
```

With several lines it is convenient to have one such context per line that
sends real calls to the right extension (for example
`Goto(from-did-direct,<ext>,1)` after the `sms`/`ussd` stubs).

`chan_quectel` sets `CALLERID(name)` to the device name (`gsm1`), so phones
show "gsm1" instead of the number. Add `Set(CALLERID(name)=${CALLERID(num)})`
before passing the call on.

## 2. AMI access

ghostline connects to AMI over the network, so `bindaddr` in
`/etc/asterisk/manager.conf` must be `0.0.0.0` and access is restricted with
`deny`/`permit` of the user. Add the user to `manager_custom.conf` (FreePBX
does not overwrite it):

```ini
[ghostline]
secret = <password>
deny=0.0.0.0/0.0.0.0
permit=10.0.0.11/255.255.255.255
read = system,call,user,message,reporting
write = command,originate,message,reporting
writetimeout = 5000
```

`write = command` is required: SMS are sent with `Action: Command`
(`quectel sms <device> <number> <text>`). `reporting` is needed for line
monitoring (`QuectelShowDevices`, see below); without it ghostline logs a
warning once and the Status tab shows no network data.

## 3. ghostline configuration

```yaml
gateways:
  - id: pbx
    type: asterisk
    ip: 10.0.0.10
    ami_port: 5038
    user: ghostline
    password: <password>
    modems:
      - name: gsm1      # device name from quectel.conf
        driver: quectel

lines:
  - name: personal
    gateway: pbx
    pattern: gsm1
    modem: gsm1
```

## 4. Line monitoring

With `monitor: true` (the default for `asterisk` gateways) ghostline polls
`QuectelShowDevices` once a minute. The action only reads the driver's state
and sends nothing to the modems. Per line it shows:

- registration and operator;
- network type (2G/3G/4G/5G);
- signal: bars and a level in dBm (details in the tooltip);
- LAC and cell id (in the tooltip);
- the modem model and its USB port (taken from `data=`; the full path in the
  tooltip);
- the SIM's own number when the SIM reports it, else `lines[].number`.

This is shown on the Status tab and in `/status`. When a line has had no
network for `monitor.alert_minutes` (default 2), a message goes to the line's
Telegram chats and ntfy topic, and another one when the network is back.

The stock driver knows neither the network type nor the LTE signal quality.
[chan_quectel-line-monitoring.patch](chan_quectel-line-monitoring.patch) adds
both. Without the patch everything else works: the type column is empty, and
the bars come from RSSI.

Network type: the stock `Mode` field is filled only from Huawei `^MODE`
messages, so Quectel and SIMCom modules always show "No Service". The patch
keeps the access technology from the `+COPS?` answer, which the driver
already queries on every registration change. It adds `AccessTechnology` to
`QuectelShowDevices` and "Access technology" to `quectel show device state`.

Signal: the stock driver knows only RSSI, which includes noise and neighbour
cells, so in LTE it shows full bars even on a poor link. The patch adds the
LTE quality of the serving cell: RSRP, RSRQ and SINR, as `RSRP`/`RSRQ`/`SINR`
in `QuectelShowDevices` and in `quectel show device state`. The driver asks the module on every
`QuectelShowDevices`, so the values are one poll old:

- Quectel modules: `AT+QENG="servingcell"`;
- SIMCom modules: `AT+CPSI?`.

A module that answers `ERROR` is not asked again until it reconnects. In 2G/3G
the values are not reported.

With these values ghostline draws the bars from RSRP and RSRQ, whichever is
worse, and shows RSRP next to them:

| Bars | RSRP | RSRQ |
|---|---|---|
| 4 | ≥ −85 dBm | ≥ −10 dB |
| 3 | ≥ −95 dBm | ≥ −13 dB |
| 2 | ≥ −105 dBm | ≥ −16 dB |
| 1 | ≥ −115 dBm | ≥ −19 dB |

Without them the bars and the level come from RSSI (`-113 + 2·RSSI` dBm), as
before.

## 5. Behaviour

- SMS delivery is not tracked: a message counts as sent when AMI answers
  `[gsm1] SMS queued for send`, which means it was queued in the modem.
- Numbers are passed to `chan_quectel` in E.164 form with `+`. Yeastar and
  GoIP gateways get `8`/`810`-prefixed numbers instead.
- Keep `autodeletesms=yes` in `quectel.conf`, otherwise the SIM's SMS memory
  fills up and reception stops. SMS that arrive while ghostline is not
  connected to AMI are not stored by Asterisk.
