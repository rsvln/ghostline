# PBX setup for `mode: full`

What ghostline needs from FreePBX/Asterisk to work with calls. Tested with
FreePBX 17 / Asterisk 22. In the examples the PBX is `10.0.0.10`, the
ghostline host is `10.0.0.11` and the transcription service is `10.0.0.20`.

## 1. Call recording

Recording must be enabled for the lines you want to see with audio. In
FreePBX it can be forced per extension, independently of route settings:

```
asterisk -rx "database put AMPUSER <ext>/recording/in/external always"
asterisk -rx "database put AMPUSER <ext>/recording/out/external always"
```

Family and key are separate arguments; `AMPUSER/<ext>/...` as one string
silently does nothing. Use `wav`: with `ogg` (Vorbis at low bitrate) the
recordings may sound echoey because of pre-echo artefacts.

## 2. CDR access (read-only)

The FreePBX MariaDB usually listens on all interfaces (systemd socket
activation ignores `bind-address`), so a user restricted to the ghostline host
is enough:

```sql
CREATE USER 'ghostline'@'10.0.0.11' IDENTIFIED BY '<password>';
GRANT SELECT ON asteriskcdrdb.cdr TO 'ghostline'@'10.0.0.11';
```

## 3. HTTP access to recordings

`/etc/apache2/conf-available/ghostline-rec.conf`, then `a2enconf ghostline-rec`
and `systemctl reload apache2`:

```apache
Alias /ghostline-rec/ /var/spool/asterisk/monitor/
<Directory /var/spool/asterisk/monitor/>
    Options -Indexes
    AllowOverride None
    Require ip 10.0.0.11
</Directory>
```

Check: from `10.0.0.11` a recording is served (200), from any other address
the response is 403, directory listing is 403.

## 4. AMI user

`/etc/asterisk/manager_custom.conf`, then `asterisk -rx "manager reload"`:

```ini
[ghostline]
secret = <password>
deny=0.0.0.0/0.0.0.0
permit=10.0.0.11/255.255.255.255
read = system,call,user,message,reporting
write = command,originate,message
writetimeout = 5000
```

## 5. Recording each side separately (dialog transcripts)

With one mixed recording the transcript cannot tell who is speaking. Asterisk
`MixMonitor` can additionally write what is received from and what is sent to
the channel into separate files (`r()` and `t()` options). FreePBX calls
MixMonitor in the generated context `[sub-record-check]`
(`extensions_additional.conf`). The `D` (stereo) option requires a `.raw`
file, and `MONITOR_REC_OPTION` is substituted without `EVAL`, so the file name
cannot be passed through it.

The working approach: copy the whole `[sub-record-check]` context into
`extensions_override_freepbx.conf` (it is included first, and Asterisk keeps
the first definition) and change one line:

```
exten => recordcheck,n(monitorcmd),MixMonitor(${MIXMON_DIR}${YEAR}/${MONTH}/${DAY}/${CALLFILENAME}.${MON_FMT},a${MONITOR_REC_OPTION}i(${LOCAL_MIXMON_ID})r(${MIXMON_DIR}${YEAR}/${MONTH}/${DAY}/${CALLFILENAME}-r.${MON_FMT})t(${MIXMON_DIR}${YEAR}/${MONTH}/${DAY}/${CALLFILENAME}-t.${MON_FMT})${MIXMON_BEEP},${MIXMON_POST})
```

Next to `X.wav` the files `X-r.wav` and `X-t.wav` appear. MixMonitor runs on
the channel where the recording check happened: for incoming calls the GSM
channel of the caller, for outgoing calls the channel of the local phone.
ghostline maps them accordingly (incoming: `r` is the remote side, `t` is you;
outgoing: the other way round).

After a FreePBX upgrade compare the copied context with the new
`extensions_additional.conf` and recreate the copy if it changed.

## 6. Transcription service

Any OpenAI-compatible `/v1/audio/transcriptions` endpoint works. Example with
[speaches](https://github.com/speaches-ai/speaches) on CPU:

```yaml
services:
  speaches:
    image: ghcr.io/speaches-ai/speaches:latest-cpu
    restart: unless-stopped
    ports:
      - "8000:8000"
    environment:
      WHISPER__INFERENCE_DEVICE: cpu
      WHISPER__COMPUTE_TYPE: int8
      WHISPER__CPU_THREADS: "6"
      WHISPER__TTL: "-1"            # keep the model loaded
      ENABLE_UI: "false"
    volumes:
      - hf-cache:/home/ubuntu/.cache/huggingface/hub
volumes:
  hf-cache:
```

Download the model once: `curl -X POST
http://10.0.0.20:8000/v1/models/deepdml/faster-whisper-large-v3-turbo-ct2`.

If the host cannot reach huggingface.co, download the model files elsewhere
and put them into the volume in the Hugging Face cache layout
(`models--<org>--<name>/refs/main` with the commit hash,
`models--<org>--<name>/snapshots/<hash>/…`), then run speaches with
`HF_HUB_OFFLINE=1`. The snapshot must include `README.md`: speaches reads the
model card (`library_name: ctranslate2`, tag `automatic-speech-recognition`)
and returns an error without it.

On an Intel i5-13500 with 6 threads, large-v3-turbo int8 transcribes about
4× faster than real time.

### Accuracy notes

- Separate recordings per side (section 5) give correct speaker turns.
- Echo of the remote voice from the local phone's speaker gets into the local
  side's recording and confuses the model. ghostline attenuates it before
  transcription (a soft gate, about −30 dB, only where the remote side is
  louder).
- `transcribe.prompt` with names and terms that are often misrecognized helps
  noticeably. Keep it short and avoid pairs of similar-sounding words: the
  model may start substituting one for the other.
- Whisper sometimes inserts subtitle credits on silence ("Subtitles by …");
  ghostline removes them.
