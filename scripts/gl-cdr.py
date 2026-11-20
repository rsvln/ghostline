#!/usr/bin/env python3
# CDR tools for ghostline (gateway pbx: managed). Runs on the Asterisk host as the asterisk user.
#
#   gl-cdr backup  <cdr-db> [keep-days]
#       A consistent copy of the CDR (SQLite online backup) into <cdr-dir>/backup/cdr-YYYYMMDD.db;
#       copies older than keep-days (14) are removed. Run daily from gl-cdr-backup.timer.
#
#   gl-cdr recover <conf-dir> <recordings-dir> <cdr-db> <from YYYY-MM-DD> [<to YYYY-MM-DD>] [--apply]
#       Rebuilds the CDR rows of calls that were recorded but never reached the CDR (Asterisk could
#       not write it: 2026-10-30, a read-only SQLite handle). One row per mixed recording, in the form
#       the managed dialplan writes; calls whose uniqueid is in the CDR are skipped. The extension of
#       our phone in the file name gives the modem, from the generated pjsip_ghostline.conf and
#       extensions_ghostline.conf. Without --apply it only prints what it would add. With it, the CDR
#       is backed up first (cdr.db.bak-<time>). ghostline picks the rows up on its next poll.
#       Missed calls are not recorded, so they cannot be recovered. Calls from before this CDR (another
#       PBX's CDR, a fresh cdr.db) show up too: they are in ghostline's journal already, so give the
#       dates of the gap only. A row ghostline already has (same uniqueid) is ignored by it anyway.
#
# Prints no phone numbers.
import datetime, os, re, shutil, sqlite3, struct, sys, time

RECORDING = re.compile(r"^(in|out)-([^-]+)-([^-]+)-(\d{8})-(\d{6})-(\d+\.\d+)\.wav$")


def backup(db, keep=14):
    out_dir = os.path.join(os.path.dirname(db), "backup")
    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, time.strftime("cdr-%Y%m%d.db"))
    src = sqlite3.connect(f"file:{db}?mode=ro", uri=True, timeout=30)
    dst = sqlite3.connect(out + ".tmp")
    try:
        src.backup(dst)
    finally:
        dst.close()
        src.close()
    os.replace(out + ".tmp", out)
    limit = time.time() - keep * 86400
    for name in os.listdir(out_dir):
        path = os.path.join(out_dir, name)
        if re.fullmatch(r"cdr-\d{8}\.db", name) and os.path.getmtime(path) < limit:
            os.remove(path)
    print("backup:", out)


# Extension of our phone -> modem: an endpoint's outgoing context gl-out-<modem>, and the ring
# context gl-ring-<modem> of incoming calls.
def extension_modems(conf):
    out, ring = {}, {}
    section = None
    try:
        for line in open(os.path.join(conf, "pjsip_ghostline.conf"), encoding="utf-8"):
            m = re.match(r"^\[([^\]]+)\]", line)
            if m:
                section = m.group(1)
            m = re.match(r"^context\s*=\s*gl-out-(\S+)", line)
            if m and section:
                out[section] = m.group(1)
    except OSError:
        pass
    try:
        modem = None
        for line in open(os.path.join(conf, "extensions_ghostline.conf"), encoding="utf-8"):
            m = re.match(r"^\[gl-ring-([^\]]+)\]", line)
            if m:
                modem = m.group(1)
                continue
            if line.startswith("["):
                modem = None
            m = re.match(r"^exten\s*=>\s*(\w+),1,", line)
            if m and modem:
                ring[m.group(1)] = modem
    except OSError:
        pass
    return out, ring


def wav_seconds(path):
    with open(path, "rb") as f:
        head = f.read(4096)
    if head[:4] != b"RIFF":
        return 0
    pos, rate_bytes = 12, 0
    while pos + 8 <= len(head):
        cid, size = head[pos:pos + 4], struct.unpack("<I", head[pos + 4:pos + 8])[0]
        if cid == b"fmt ":
            rate_bytes = struct.unpack("<I", head[pos + 16:pos + 20])[0]
        elif cid == b"data":
            data = os.path.getsize(path) - (pos + 8)
            return int(round(data / rate_bytes)) if rate_bytes else 0
        pos += 8 + size + (size & 1)
    return 0


def recover(conf, rec, db, first, last, apply):
    out_modem, ring_modem = extension_modems(conf)
    if not out_modem and not ring_modem:
        sys.exit(f"no extensions found in {conf}/pjsip_ghostline.conf and extensions_ghostline.conf")
    con = sqlite3.connect(db if apply else f"file:{db}?mode=ro", uri=not apply, timeout=10)
    known = {u for row in con.execute("select uniqueid, linkedid from cdr") for u in row}
    rows = []
    day = first
    while day <= last:
        d = os.path.join(rec, day.strftime("%Y/%m/%d"))
        for name in sorted(os.listdir(d)) if os.path.isdir(d) else []:
            m = RECORDING.match(name)
            if not m:
                continue
            direction, a, b, ymd, hms, uid = m.groups()
            if uid in known:
                continue
            secs = wav_seconds(os.path.join(d, name))
            # The start time as Asterisk writes CDR(start): the epoch in the uniqueid, in local time. The
            # time in the file name is not used: STRFTIME there can be in another zone than the CDR.
            calldate = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(int(uid.split(".")[0])))
            if direction == "out":            # out-<dialed>-<extension>-...
                dst, ext = a, b
                modem = out_modem.get(ext) or ring_modem.get(ext)
                row = (ext, dst, "", f"gl-out-{modem}", f"PJSIP/{ext}-recovered", f"Quectel/{modem}-recovered")
            else:                             # in-<extension>-<caller>-...
                ext, src = a, b
                modem = ring_modem.get(ext) or out_modem.get(ext)
                row = (src, ext, modem, f"gl-in-{modem}", f"Quectel/{modem}-recovered", f"PJSIP/{ext}-recovered")
            if modem is None:
                print(f"skip: {direction} {calldate}: extension {ext} is not in the generated config")
                continue
            disposition = "ANSWERED" if secs > 0 else "NO ANSWER"
            rows.append((calldate, uid, uid) + row + ("Dial", disposition, secs, secs, name))
            print(f"{'add' if apply else 'would add'}: {direction} ext {ext} {modem} {calldate} "
                  f"{secs // 60}:{secs % 60:02d} {disposition}")
        day += datetime.timedelta(days=1)
    if apply and rows:
        con.close()
        shutil.copy2(db, db + ".bak-" + time.strftime("%Y%m%d-%H%M%S"))
        con = sqlite3.connect(db, timeout=10)
        con.executemany("""insert into cdr (calldate, uniqueid, linkedid, src, dst, did, dcontext, channel,
                           dstchannel, lastapp, disposition, duration, billsec, recordingfile)
                           values (?,?,?,?,?,?,?,?,?,?,?,?,?,?)""", rows)
        con.commit()
    con.close()
    print(f"{len(rows)} call(s) {'added' if apply else 'to add; run again with --apply'}")


def usage():
    sys.exit("usage: gl-cdr backup <cdr-db> [keep-days]\n"
             "       gl-cdr recover <conf-dir> <recordings-dir> <cdr-db> <from YYYY-MM-DD> [<to YYYY-MM-DD>] [--apply]")


if __name__ == "__main__":
    args = sys.argv[1:]
    if args[:1] == ["backup"] and len(args) in (2, 3):
        backup(args[1], int(args[2]) if len(args) == 3 else 14)
    elif args[:1] == ["recover"] and len(args) >= 5:
        apply = "--apply" in args
        args = [a for a in args if a != "--apply"]
        first = datetime.date.fromisoformat(args[4])
        last = datetime.date.fromisoformat(args[5]) if len(args) > 5 else datetime.date.today()
        recover(args[1], args[2], args[3], first, last, apply)
    else:
        usage()
