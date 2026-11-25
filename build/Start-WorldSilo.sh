#!/bin/bash
# Run by build/Boot-WorldSilo.py once it has verified this script against the pin in the VM extension's protected
# command. The script is a retained deployment input and holds no secret; the signing keys arrive only in
# PUCK_FEDERATION_KEYS, from that protected command.
set -euo pipefail
umask 077
# A timed-out pass reports only journal lines from this pass: a worker whose extension Azure runs again also holds its
# earlier passes' lines, whose errors would read as this pass's cause.
pass_started="$(date +%s)"
federation_keys="${PUCK_FEDERATION_KEYS:?the launcher passes the federation signing keys}"
unset PUCK_FEDERATION_KEYS
. /etc/os-release
crypto_stale=0
if [ "$ID" = azurelinux ]; then
    # Azure Linux builds moby-engine and containerd2 with Microsoft's Go, whose system crypto loads the host's
    # libcrypto when each daemon starts, yet neither package requires a minimum openssl-libs. The packages come from
    # the live repository while the library comes from the pinned image, so a newer Go meeting an older library can
    # break every TLS handshake (Go 1.27's default X25519MLKEM768 against an openssl-libs older than 3.3.7-5.azl3,
    # which lacks the provider-backed EVP_PKEY_Q_keygen fix). Take the repository's openssl-libs in its own
    # transaction first, so no daemon an install starts can load the old library, and take it on every pass, so a
    # re-run repairs a VM that an earlier pass left on the old library.
    crypto_before="$(rpm -q openssl-libs || true)"
    tdnf install -y openssl-libs
    if [ "$(rpm -q openssl-libs)" != "$crypto_before" ] && systemctl is-active --quiet containerd.service docker.service; then
        crypto_stale=1
    fi
fi
if ! command -v docker >/dev/null || ! command -v curl >/dev/null || ! command -v python3 >/dev/null || ! command -v flock >/dev/null; then
    case "$ID" in
        azurelinux)
            tdnf install -y moby-engine moby-cli curl python3 util-linux
            ;;
        ubuntu)
            export DEBIAN_FRONTEND=noninteractive
            apt-get update -qq
            apt-get install -y -qq docker.io curl python3 util-linux
            ;;
        *)
            printf 'Unsupported world host OS: %s\n' "$ID" >&2
            exit 1
            ;;
    esac
fi
# Serialize retained extension scripts with coordinator guest effects. Recheck the
# durable operation after waiting: Azure may execute a previously accepted job late.
exec 9>/run/puck-world-release.lock
flock -w 600 9
release_guard() {
    __RELEASE_GUARD__
}
release_guard
systemctl enable --now docker
# A daemon an earlier pass started before the library was replaced still holds the old one. Restart it only while no
# world runs: restarting Docker stops its containers, and a running writer is the coordinator's to drain.
if [ "$crypto_stale" = 1 ] && [ "$(docker inspect -f '{{.State.Running}}' puck-world 2>/dev/null || true)" != true ]; then
    systemctl try-restart containerd.service docker.service
fi
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
# Only the coordinator drains and stops an authoritative worker. A retried extension
# may observe its own healthy candidate, but must never restart a running writer.
release_guard
if docker inspect puck-world >/dev/null 2>&1; then
    if [ "$(docker inspect -f '{{.State.Running}}' puck-world)" = true ]; then
        if [ "$(docker inspect -f '{{.Config.Image}}' puck-world)" != '__IMAGE__' ]; then
            printf 'Another world image is still running; the coordinator must drain it first.\n' >&2
            exit 1
        fi
        curl --fail --silent --show-error --max-time 30 http://127.0.0.1:__HEALTH_PORT__/release/status |
            python3 -c 'import json,sys; assert json.load(sys.stdin)["release"] == "__RELEASE_ID__", "another release is running"'
        curl --fail --silent --show-error --max-time 30 http://127.0.0.1:__HEALTH_PORT__/private-healthz
    else
        systemctl stop puck-world.service
        docker rm puck-world >/dev/null
    fi
fi
printf '%s' '__SILO_DOCUMENT__' | base64 -d >/etc/puck/silo.json
PUCK_FEDERATION_KEYS="$federation_keys" python3 - <<'KEYS'
import base64, json, os, pathlib
for name, encoded in json.loads(os.environ['PUCK_FEDERATION_KEYS']).items():
    if '/' in name or '\\' in name or not (name.startswith('federation') or name == 'issuer.pk8') or not name.endswith('.pk8'):
        raise ValueError('invalid retained signing-key filename')
    path = pathlib.Path('/etc/puck') / name
    path.write_bytes(base64.b64decode(encoded, validate=True))
    os.chown(path, 0, 1654)
    os.chmod(path, 0o640)
KEYS
chgrp 1654 /etc/puck/silo.json
chmod 640 /etc/puck/silo.json
if [ '__MCP_ENABLED__' = 1 ]; then
    rm -f /etc/puck/mcp.pfx
    printf '%s' '__MCP_DOCUMENT__' | base64 -d >/etc/puck/mcp.json
    printf '%s' '__MCP_TLS_DOCUMENT__' | base64 -d >/etc/puck/tls.json
    chgrp 1654 /etc/puck/mcp.json
    chmod 640 /etc/puck/mcp.json
    chgrp 1655 /etc/puck/tls.json
    chmod 640 /etc/puck/tls.json
    # The keeper runs on the host as Caddy's UID, and systemd refuses a numeric User= or Group= that the host's account
    # database lacks (its get_user_creds answers ESRCH, so the start fails at step USER and Caddy, which waits for the
    # keeper, never starts). The containers need no host account; the keeper does, so the host carries one.
    getent group 1655 >/dev/null || groupadd --system --gid 1655 puck-tls
    getent passwd 1655 >/dev/null || useradd --system --uid 1655 --gid 1655 --no-create-home --home-dir /var/lib/puck-tls --shell /usr/sbin/nologin puck-tls
    install -d -m 700 -o 1655 -g 1655 /var/lib/puck-tls
    # The keeper holds the TLS state (certificate, key, ACME account) in the owner's private store, so a fresh worker
    # restores it instead of issuing again (build/Keep-WorldTls.py). It runs as Caddy's user, which /etc/puck excludes.
    install -d -m 750 -o 0 -g 1655 /etc/puck-tls
    printf '%s' '__TLS_KEEPER__' | base64 -d >/etc/puck-tls/keeper.py
    chgrp 1655 /etc/puck-tls/keeper.py
    chmod 640 /etc/puck-tls/keeper.py
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
# Each restart takes a fresh authority fence, so a silo that keeps failing backs off from 5 s to 5 min.
RestartSec=5
RestartSteps=6
RestartMaxDelaySec=300
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
if [ '__MCP_ENABLED__' = 1 ]; then
    # Caddy starts only once the keeper reports ready: the stored or local certificate is fresh, or this worker holds
    # the store's issuance lease. Its start may wait on another worker's issuance, so nothing here blocks on it.
    # The keeper's unit is the one door: Caddy's unit is Requisite on it (a start fails unless the keeper is already
    # active, which a Type=notify unit is only after READY) and is never enabled itself, and the keeper Upholds Caddy,
    # so systemd starts Caddy whenever the keeper is ready, including after a keeper start that failed and was retried.
    # A Requires= edge would fail Caddy's start job with the keeper's and never queue it again.
    systemctl disable puck-tls.service 2>/dev/null || true
    cat >/etc/systemd/system/puck-tls-keeper.service <<'KEEPERUNIT'
[Unit]
Description=Puck TLS state keeper
Wants=network-online.target
After=network-online.target
Upholds=puck-tls.service
[Service]
Type=notify
User=1655
Group=1655
ExecStart=/usr/bin/python3 /etc/puck-tls/keeper.py
Restart=always
RestartSec=10
TimeoutStartSec=infinity
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
ReadWritePaths=/var/lib/puck-tls
[Install]
WantedBy=multi-user.target
KEEPERUNIT
    cat >/etc/systemd/system/puck-tls.service <<'TLSUNIT'
[Unit]
Description=Puck automatic HTTPS
Requires=docker.service
Requisite=puck-tls-keeper.service
After=docker.service network-online.target puck-world.service puck-tls-keeper.service
[Service]
Restart=on-failure
RestartSec=5
TimeoutStopSec=30
ExecStartPre=-/usr/bin/docker rm puck-tls
ExecStart=/usr/bin/docker run --name puck-tls --network host --user 1655:1655 --read-only --tmpfs /tmp:rw,noexec,nosuid,size=16m --cap-drop ALL --security-opt no-new-privileges --pids-limit 128 --memory 128m --log-driver local --log-opt max-size=10m --log-opt max-file=3 --env XDG_DATA_HOME=/data --env XDG_CONFIG_HOME=/data --mount type=bind,source=/etc/puck/tls.json,target=/tls.json,readonly --mount type=bind,source=/var/lib/puck-tls,target=/data --entrypoint /usr/bin/caddy __IMAGE__ run --config /tls.json
ExecStop=/usr/bin/docker stop --time 20 puck-tls
TLSUNIT
    systemctl daemon-reload
    systemctl enable puck-tls-keeper.service
    # Stop both in one transaction (the keeper would uphold a Caddy stopped alone), then start the keeper: Caddy
    # follows with this release's configuration once the keeper is ready.
    systemctl stop puck-tls-keeper.service puck-tls.service
    systemctl start --no-block puck-tls-keeper.service
else
    systemctl disable --now puck-tls.service puck-tls-keeper.service 2>/dev/null || true
fi
# Wait only for what a candidate reaches before its release commits: private health (the pinned rows activated and
# checkpointed) and, with MCP, the TLS front. Public /healthz answers 503 until the coordinator commits and opens
# admission, which happens only after this script succeeds, so waiting on it here would deadlock the bootstrap.
# The silo activates its rows one after another, so the deadline grows with the row count (rendered by the deploy).
# A silo that exits during the wait has failed: its start is deterministic over the stored roots and the release, so
# systemd's restart would only repeat the refusal under a fresh fence until the deadline. The wait ends at the first
# exit, stops the unit so it stops retrying, and reports that run's own lines.
ready_deadline=$(( $(date +%s) + __READY_SECONDS__ ))
private_state='private health has not answered'
mcp_state='not reached: private health is not ready'
verdict="World worker readiness timed out after __READY_SECONDS__ s."
while [ "$(date +%s)" -lt "$ready_deadline" ]; do
    if private_state="$(curl --fail-with-body --silent --show-error --max-time 30 http://127.0.0.1:__HEALTH_PORT__/private-healthz 2>&1)"; then
        if [ '__MCP_ENABLED__' = 1 ] && ! mcp_state="$(curl --fail --silent --show-error --max-time 10 --connect-to '__MCP_HOST__:443:127.0.0.1:8443' 'https://__MCP_HOST__/.well-known/oauth-protected-resource/mcp' 2>&1 >/dev/null)"; then
            sleep 2
            continue
        fi
        printf '%s\n' '__IMAGE__' >/etc/puck/release
        # Registry retention owns rollback availability; never guess the previous
        # release from local image ordering or prune retained images during startup.
        exit 0
    fi
    # One property per call: systemctl show prints several in its own order, not the order asked.
    restarts="$(systemctl show -p NRestarts --value puck-world.service)"
    active="$(systemctl show -p ActiveState --value puck-world.service)"
    sub="$(systemctl show -p SubState --value puck-world.service)"
    if [ "${restarts:-0}" -gt 0 ] || [ "$active" = failed ] || [ "$sub" = auto-restart ]; then
        verdict="World worker silo exited (status $(systemctl show -p ExecMainStatus --value puck-world.service)) after $(( $(date +%s) - pass_started )) s, before private health answered; its restart was stopped."
        systemctl stop puck-world.service || true
        break
    fi
    sleep 2
done
# Only the tail of the extension's output survives in Azure's error, so the silo's own lines (the health listener logs
# no access lines) and the TLS front's errors go to stdout, and the verdict of each wait goes last, to stderr.
journalctl -u puck-world.service --no-pager -o cat -n 40 --since "@$pass_started" || true
if [ '__MCP_ENABLED__' = 1 ]; then
    { journalctl -u puck-tls.service --no-pager -o cat -n 400 --since "@$pass_started" | grep -e error -e fail | tail -n 6 | cut -c1-200; } || true
fi
{
    printf '%s\n' "$verdict"
    printf 'private health (what this wait needs): %s\n' "$private_state"
    if [ '__MCP_ENABLED__' = 1 ]; then
        printf 'MCP TLS front (what this wait needs): %s\n' "$mcp_state"
        printf 'TLS state keeper (starts the front): %s; last lines: %s\n' \
            "$(systemctl show -p ActiveState,Result,ExecMainStatus --value puck-tls-keeper.service 2>&1 | paste -sd ' ')" \
            "$(journalctl -u puck-tls-keeper.service --no-pager -o cat -n 3 --since "@$pass_started" 2>&1 | paste -sd '|')"
    fi
    printf 'public health (opens only after the release commits): %s\n' "$(curl --silent --show-error --max-time 10 --write-out ' (HTTP %{http_code})' http://127.0.0.1:__HEALTH_PORT__/healthz 2>&1)"
    printf 'container: %s\n' "$(docker inspect -f '{{.State.Status}} restarts={{.RestartCount}} exit={{.State.ExitCode}}' puck-world 2>&1)"
    # The silo's own lines, kept short and last: the stdout above can fill Azure's kept tail with the front's request
    # logs, and a silo that exits at start says why only here. Its tagged narration ([silo.activate: ... refused],
    # [silo.host: stopped by ...]) comes first, since a stack trace can be longer than the tail.
    silo_log="$(docker logs puck-world 2>&1 || journalctl -u puck-world.service --no-pager -o cat --since "@$pass_started" 2>&1 || true)"
    printf 'silo (its tagged lines):\n'
    { printf '%s\n' "$silo_log" | grep -e '\[silo' -e '\[world' | tail -n 15 | cut -c1-240; } || true
    printf 'silo (last lines):\n'
    { printf '%s\n' "$silo_log" | tail -n 12 | cut -c1-240; } || true
} >&2
exit 1
