# Asterisk/FreePBX setup for `type: quectel`

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
write = command,originate,message
writetimeout = 5000
```

`write = command` is required: SMS are sent with `Action: Command`
(`quectel sms <device> <number> <text>`).

## 3. ghostline configuration

```yaml
gateways:
  - id: pbx
    type: quectel
    ip: 10.0.0.10
    ami_port: 5038
    user: ghostline
    password: <password>

channels:
  - name: personal
    gateway: pbx
    pattern: gsm1
    line: gsm1          # device name from quectel.conf
```

## 4. Behaviour

- SMS delivery is not tracked: a message counts as sent when AMI answers
  `[gsm1] SMS queued for send`, which means it was queued in the modem.
- Numbers are passed to `chan_quectel` in E.164 form with `+`. Yeastar and
  GoIP gateways get `8`/`810`-prefixed numbers instead.
- Keep `autodeletesms=yes` in `quectel.conf`, otherwise the SIM's SMS memory
  fills up and reception stops. SMS that arrive while ghostline is not
  connected to AMI are not stored by Asterisk.
