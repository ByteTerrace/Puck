#!/bin/bash
# Installed by the VM extension; configuration and signing material are protected settings.
set -euo pipefail
umask 077
if ! command -v docker >/dev/null || ! command -v curl >/dev/null || ! command -v python3 >/dev/null; then
    . /etc/os-release
    case "$ID" in
        azurelinux)
            tdnf install -y moby-engine moby-cli curl python3
            ;;
        ubuntu)
            export DEBIAN_FRONTEND=noninteractive
            apt-get update -qq
            apt-get install -y -qq docker.io curl python3
            ;;
        *)
            printf 'Unsupported world host OS: %s\n' "$ID" >&2
            exit 1
            ;;
    esac
fi
systemctl enable --now docker
install -d -m 700 /etc/puck /var/lib/puck
# The runtime uses the .NET image's unprivileged app UID; bootstrap remains root-owned.
chown -R 1654:1654 /var/lib/puck
chgrp 1654 /etc/puck
chmod 750 /etc/puck
cat >/etc/puck/registry-login.py <<'PY'
import json, subprocess, urllib.parse, urllib.request
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
query = urllib.parse.urlencode({'api-version':'2018-02-01','resource':'https://containerregistry.azure.net','client_id':'__CLIENT_ID__'})
request = urllib.request.Request('http://169.254.169.254/metadata/identity/oauth2/token?' + query, headers={'Metadata':'true'})
with opener.open(request, timeout=20) as response:
    token = json.load(response)['access_token']
body = urllib.parse.urlencode({'grant_type':'access_token','service':'__REGISTRY__','access_token':token}).encode()
with urllib.request.urlopen('https://__REGISTRY__/oauth2/exchange', data=body, timeout=20) as response:
    refresh = json.load(response)['refresh_token']
subprocess.run(['docker','login','__REGISTRY__','--username','00000000-0000-0000-0000-000000000000','--password-stdin'], input=refresh, text=True, check=True, stdout=subprocess.DEVNULL)
PY
for attempt in $(seq 1 30); do
    if python3 /etc/puck/registry-login.py && docker pull '__IMAGE__'; then break; fi
    if [ "$attempt" = 30 ]; then exit 1; fi
    sleep 10
done
docker logout '__REGISTRY__' >/dev/null
# A release updates configuration only after the current process has saved its world.
if docker inspect puck-world >/dev/null 2>&1; then
    if [ "$(docker inspect -f '{{.State.Running}}' puck-world)" = true ]; then
        curl --fail --silent --show-error --max-time __SHUTDOWN_SECONDS__ -X POST http://127.0.0.1:__HEALTH_PORT__/drain
    fi
    systemctl stop puck-world.service
    docker rm puck-world >/dev/null
fi
printf '%s' '__SILO_DOCUMENT__' | base64 -d >/etc/puck/silo.json
printf '%s' '__FEDERATION_KEY__' | base64 -d >/etc/puck/federation.pk8
chgrp 1654 /etc/puck/silo.json /etc/puck/federation.pk8
chmod 640 /etc/puck/silo.json /etc/puck/federation.pk8
if [ '__MCP_ENABLED__' = 1 ]; then
    printf '%s' '__MCP_DOCUMENT__' | base64 -d >/etc/puck/mcp.json
    printf '%s' '__MCP_CERTIFICATE__' | base64 -d >/etc/puck/mcp.pfx
    chgrp 1654 /etc/puck/mcp.json /etc/puck/mcp.pfx
    chmod 640 /etc/puck/mcp.json /etc/puck/mcp.pfx
fi
cat >/etc/puck/firewall.sh <<'FIREWALL'
#!/bin/bash
set -euo pipefail
# Preserve the OS firewall policy; manage only Puck's chain on each service start.
iptables -nL PUCK-WORLD >/dev/null 2>&1 || iptables -N PUCK-WORLD
iptables -F PUCK-WORLD
iptables -A PUCK-WORLD -p udp --dport __QUIC_PORT__ -j ACCEPT
iptables -A PUCK-WORLD -p tcp -s 168.63.129.16/32 --dport __HEALTH_PORT__ -j ACCEPT
if [ '__MCP_ENABLED__' = 1 ]; then iptables -A PUCK-WORLD -p tcp --dport 8443 -j ACCEPT; fi
iptables -C INPUT -j PUCK-WORLD 2>/dev/null || iptables -I INPUT 1 -j PUCK-WORLD
FIREWALL
cat >/etc/systemd/system/puck-world.service <<'UNIT'
[Unit]
Description=Puck world silo
Requires=docker.service
After=docker.service network-online.target iptables.service
[Service]
Restart=on-failure
RestartSec=5
TimeoutStopSec=__STOP_SECONDS__
ExecStartPre=/bin/bash /etc/puck/firewall.sh
ExecStartPre=-/usr/bin/docker rm puck-world
ExecStart=/usr/bin/docker run --name puck-world --network host --user 1654:1654 --read-only --tmpfs /tmp:rw,noexec,nosuid,size=64m --cap-drop ALL --security-opt no-new-privileges --pids-limit 512 --log-driver local --log-opt max-size=10m --log-opt max-file=3 --cpus __CPU__ --memory __MEMORY__g --env AZURE_CLIENT_ID=__CLIENT_ID__ --env ASPNETCORE_HTTP_PORTS= --mount type=bind,source=/etc/puck,target=/configuration,readonly --mount type=bind,source=/var/lib/puck,target=/state __MCP_ENTRYPOINT__ __IMAGE__ __MCP_ARGUMENTS__
ExecStop=/usr/bin/docker stop --time __SHUTDOWN_SECONDS__ puck-world
[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable --now puck-world.service
for attempt in $(seq 1 180); do
    if curl --fail --silent http://127.0.0.1:__HEALTH_PORT__/healthz; then
        printf '%s\n' '__IMAGE__' >/etc/puck/release
        # Keep the running image and one previous release. Remove only unused world-silo images.
        python3 - <<'CLEANUP'
import json, subprocess
images = subprocess.check_output(['docker', 'image', 'ls', '__REGISTRY__/world-silo', '--format', '{{.ID}}'], text=True).splitlines()
protected = set()
for container in subprocess.check_output(['docker', 'ps', '-aq'], text=True).splitlines():
    protected.add(json.loads(subprocess.check_output(['docker', 'inspect', container], text=True))[0]['Image'])
unused = 0
for identity in dict.fromkeys(images):
    full = json.loads(subprocess.check_output(['docker', 'image', 'inspect', identity], text=True))[0]['Id']
    if full in protected:
        continue
    unused += 1
    if unused > 1:
        subprocess.run(['docker', 'image', 'rm', identity], check=True)
CLEANUP
        exit 0
    fi
    sleep 2
done
journalctl -u puck-world.service --no-pager -n 50
exit 1
