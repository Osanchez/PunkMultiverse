# Append a devcmd to the dedicated server's devcmd.txt over Pelican SFTP.
#   python srv_cmd.py "simprof 30"
# The mod polls that file twice a second and truncates it after executing.
import configparser, os, sys, io
import paramiko

SECRET = r"C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest\PunkMultiverse\.secret"
cfg = configparser.ConfigParser()
cfg.read(os.path.join(SECRET, "sftp.cfg"))
s = cfg["SFTP"]
t = paramiko.Transport((s["target"], int(s.get("port", "2022"))))
t.connect(username=s["user"], password=s["password"])
sftp = paramiko.SFTPClient.from_transport(t)
remote = "BepInEx/plugins/PunkMultiverse/devcmd.txt"
line = " ".join(sys.argv[1:]).strip() + "\n"
try:
    existing = b""
    try:
        with sftp.open(remote, "rb") as f:
            existing = f.read()
    except IOError:
        pass
    with sftp.open(remote, "wb") as f:
        f.write(existing + line.encode("ascii"))
    print(f"sent: {line.strip()}")
finally:
    sftp.close(); t.close()
