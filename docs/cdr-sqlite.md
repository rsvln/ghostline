# Call journal from SQLite (`calls.cdr_sqlite`)

By default ghostline reads the call journal (CDR) from the FreePBX MySQL
database (`calls.cdr_db`). Without MySQL, Asterisk can write the CDR into a
SQLite file with its stock `cdr_sqlite3_custom` module, and ghostline polls
that file. Nothing is lost while ghostline is down: it catches up from the
file, as it does with MySQL.

The file must be on a directory both sides see. On one Proxmox host, a bind
mount of one host directory into both containers works; SQLite locking works
there because both run on the same kernel. A network share (NFS, SMB) is not
safe for SQLite.

## 1. Shared directory (Proxmox example)

```
mkdir -p /srv/gl-share/cdr
chown <asterisk uid>:<asterisk gid> /srv/gl-share/cdr    # as seen on the host
pct set <pbx ct> -mp0 /srv/gl-share/cdr,mp=/var/spool/asterisk/gl-cdr
pct set <ghostline ct> -mp0 /srv/gl-share/cdr,mp=/srv/asterisk-cdr,ro=1
```

Restart both containers. ghostline only reads, so its mount is read-only.

## 2. Asterisk: `/etc/asterisk/cdr_sqlite3_custom.conf`

The section name is the database file; Asterisk 22 takes an absolute path.
The columns are the ones ghostline reads.

```
[/var/spool/asterisk/gl-cdr/cdr.db]
table => cdr
columns => calldate, uniqueid, linkedid, src, dst, did, dcontext, channel, dstchannel, lastapp, disposition, duration, billsec, recordingfile
values => '${CDR(start)}','${CDR(uniqueid)}','${CDR(linkedid)}','${CDR(src)}','${CDR(dst)}','${CDR(did)}','${CDR(dcontext)}','${CDR(channel)}','${CDR(dstchannel)}','${CDR(lastapp)}','${CDR(disposition)}','${CDR(duration)}','${CDR(billsec)}','${CDR(recordingfile)}'
busy_timeout => 5000
```

```
asterisk -rx "module load cdr_sqlite3_custom.so"
```

The module creates the file and the table at the first call. It can run next
to the MySQL CDR of FreePBX; both get the same rows.

## 3. ghostline

```yaml
calls:
  cdr_sqlite: /srv/asterisk-cdr/cdr.db
```

`cdr_sqlite` wins over `cdr_db`, so to go back remove the line. The Status
tab shows the file as the CDR source.

The recordings can come the same way, from `/var/spool/asterisk/monitor`
mounted into the ghostline container (`calls.recordings_dir`) instead of HTTP
(`calls.recordings_url`).
