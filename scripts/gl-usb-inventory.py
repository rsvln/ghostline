#!/usr/bin/env python3
# USB modem inventory for ghostline (gateway pbx: managed). Runs on the Asterisk host as the
# asterisk user and writes <conf-dir>/usb.json: every USB device with serial ports, its interfaces
# and sound cards, and whether Asterisk may open them. Only sysfs and file permissions are read;
# nothing is sent to the modems (no AT commands, no port is opened).
import json, os, sys, tempfile

BY_PATH = "/dev/serial/by-path"


def read(path):
    try:
        with open(path) as f:
            return f.read().strip()
    except OSError:
        return ""


def main(out):
    devices = {}
    names = sorted(os.listdir(BY_PATH)) if os.path.isdir(BY_PATH) else []
    # Each port has two names (usb-0:… and usbv2-0:…); the plain one is kept.
    for name in sorted(names, key=lambda n: "-usbv2-" in n):
        link = os.path.join(BY_PATH, name)
        tty = os.path.basename(os.path.realpath(link))
        port_dev = os.path.realpath(f"/sys/class/tty/{tty}/device")   # .../3-7:1.2/ttyUSB9
        iface = os.path.dirname(port_dev)                                # .../3-7:1.2
        usb = os.path.dirname(iface)                                     # .../3-7
        if not read(os.path.join(usb, "idVendor")):
            continue
        d = devices.setdefault(usb, {
            "usb": os.path.basename(usb),
            "vid": read(os.path.join(usb, "idVendor")),
            "pid": read(os.path.join(usb, "idProduct")),
            "manufacturer": read(os.path.join(usb, "manufacturer")),
            "product": read(os.path.join(usb, "product")),
            "ports": [],
            "sound": [],
        })
        if any(p["tty"] == tty for p in d["ports"]):
            continue
        d["ports"].append({
            "path": link,
            "tty": tty,
            "interface": int(read(os.path.join(iface, "bInterfaceNumber")) or "-1", 16),
            "access": os.access(f"/dev/{tty}", os.R_OK | os.W_OK),
        })

    for card in sorted(os.listdir("/sys/class/sound")) if os.path.isdir("/sys/class/sound") else []:
        if not card.startswith("card"):
            continue
        dev = os.path.realpath(f"/sys/class/sound/{card}/device")
        usb = os.path.dirname(dev)
        if usb in devices:
            cid = read(f"/sys/class/sound/{card}/id")
            n = card[4:]
            devices[usb]["sound"].append({
                "card": cid,
                "alsadev": f"hw:CARD={cid},DEV=0",
                "access": all(os.access(f"/dev/snd/pcmC{n}D0{x}", os.R_OK | os.W_OK) for x in "cp"),
            })

    for d in devices.values():
        d["ports"].sort(key=lambda p: p["interface"])
    data = json.dumps({"devices": list(devices.values())}, indent=1)
    if os.path.exists(out) and read(out) == data:
        return
    fd, tmp = tempfile.mkstemp(dir=os.path.dirname(out), prefix=".usb.")
    with os.fdopen(fd, "w") as f:
        f.write(data)
    os.chmod(tmp, 0o644)
    os.replace(tmp, out)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "/etc/asterisk/ghostline/usb.json")
