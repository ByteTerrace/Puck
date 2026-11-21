#!/usr/bin/env python3
"""Keep the MCP front's TLS state (certificate, key and ACME account) in the world owner's private store.

Caddy keeps that state in its data directory on the worker's own disk, so without this keeper every fresh worker would
register a new ACME account and issue a new certificate for the same hostname, and Let's Encrypt allows only five new
certificates per exact set of names in seven days. The keeper runs as Caddy's unprivileged user beside it and owns all
of its store traffic, with the worker's managed identity, through the same door the launcher reads its inputs by.

The invariant: only the holder of the issuance lease (a blob lease on one lock blob) lets Caddy start without a fresh
certificate in hand. Startup is the one door: systemd starts Caddy only after this program reports READY, and it
reports READY only when the restored or local certificate is still outside Caddy's renewal window, or once it holds the
lease. A second worker that finds no fresh certificate waits for the holder to store one (or for its lease to lapse),
then restores it, so a hostname issues once however many workers start. Afterwards the keeper stores whichever
certificate is newer, local or stored, so a renewal reaches the store and a worker that sees another's renewal loads it
instead of renewing. Renewals Caddy makes on its own carry ACME renewal information and are exempt from rate limits.
"""
import base64
import datetime
import json
import os
import pathlib
import socket
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

STATE_URL = '__TLS_STATE_URL__'
LOCK_URL = '__TLS_LOCK_URL__'
CLIENT_ID = '__CLIENT_ID__'
HOST = '__MCP_HOST__'
DATA = pathlib.Path('/var/lib/puck-tls/caddy')
SCHEMA = 'puck.world.tls-state.v1'
# Caddy's certificate and ACME account trees; locks, OCSP staples and the instance id stay per worker.
TREES = ('certificates', 'acme')
LIMIT = 1024 * 1024
LEASE_SECONDS = 60
RENEW_SECONDS = 20
WAIT_SECONDS = 10
SYNC_SECONDS = 600
# How long a holder keeps the lease while Caddy has not yet stored a fresh certificate; Caddy keeps retrying on its
# own afterwards, and another worker may then take its turn.
HOLD_SECONDS = 900

_token = (None, 0.0)


def log(message):
    print(message, flush=True)


def token():
    global _token
    if _token[0] is None or time.time() > _token[1] - 300:
        # IMDS must bypass proxies; storage uses normal HTTPS.
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        query = urllib.parse.urlencode({'api-version': '2018-02-01', 'resource': 'https://storage.azure.com/',
                                        'client_id': CLIENT_ID})
        request = urllib.request.Request('http://169.254.169.254/metadata/identity/oauth2/token?' + query,
                                         headers={'Metadata': 'true'})
        with opener.open(request, timeout=20) as response:
            body = json.load(response)
        _token = (body['access_token'], float(body['expires_on']))
    return _token[0]


def call(method, url, headers=None, body=None):
    """Sends one storage request; returns (status, headers, body) for success and for 404, 409 and 412."""
    request = urllib.request.Request(url, data=body, method=method, headers={
        'Authorization': 'Bearer ' + token(), 'x-ms-version': '2023-11-03', **(headers or {})})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, response.headers, response.read(LIMIT + 1)
    except urllib.error.HTTPError as error:
        if error.code in (404, 409, 412):
            return error.code, error.headers, b''
        raise


def validity(pem):
    """Returns the leaf certificate's (notBefore, notAfter) from PEM bytes, reading the DER validity field."""
    text = pem.decode('ascii')
    start = text.index('-----BEGIN CERTIFICATE-----') + len('-----BEGIN CERTIFICATE-----')
    der = base64.b64decode(''.join(text[start:text.index('-----END CERTIFICATE-----', start)].split()))

    def element(at):
        tag, length, at = der[at], der[at + 1], at + 2
        if length & 0x80:
            count = length & 0x7f
            length, at = int.from_bytes(der[at:at + count], 'big'), at + count
        return tag, at, at + length

    _, certificate, _ = element(0)
    _, field, _ = element(certificate)
    tag, _, end = element(field)
    if tag == 0xa0:
        field = end
    for _ in range(3):
        field = element(field)[2]
    _, at, _ = element(field)
    times = []
    for _ in range(2):
        tag, start, at = element(at)
        layout = '%y%m%d%H%M%SZ' if tag == 0x17 else '%Y%m%d%H%M%SZ'
        times.append(datetime.datetime.strptime(der[start:at].decode('ascii'), layout)
                     .replace(tzinfo=datetime.timezone.utc).timestamp())
    return times[0], times[1]


def leaf(files):
    """Returns the hostname's certificate validity in a file map, or None."""
    # Caddy's file storage names a certificate's directory and files after the lowercase hostname.
    suffix = '/' + HOST.lower() + '/' + HOST.lower() + '.crt'
    found = [validity(content) for name, content in files.items()
             if name.startswith('certificates/') and name.endswith(suffix)]
    return max(found, key=lambda span: span[1]) if found else None


def fresh(span, now):
    # Caddy's default renewal window is the last third of a certificate's lifetime.
    return span is not None and span[1] - now > (span[1] - span[0]) / 3


def newer(left, right):
    return left is not None and (right is None or left[1] > right[1])


def local_files():
    files = {}
    for tree in TREES:
        root = DATA / tree
        if root.is_dir():
            for path in root.rglob('*'):
                if path.is_file() and not path.name.startswith('.'):
                    files[path.relative_to(DATA).as_posix()] = path.read_bytes()
    return files


def stored():
    """Returns (files, etag) of the stored state, or ({}, None) when none is stored."""
    status, headers, body = call('GET', STATE_URL)
    if status == 404:
        return {}, None
    if status != 200 or len(body) > LIMIT:
        raise ValueError('the stored TLS state is unreadable')
    document = json.loads(body)
    if document.get('schema') != SCHEMA or document.get('host') != HOST:
        raise ValueError('the stored TLS state belongs to another host or schema')
    return {name: base64.b64decode(content, validate=True) for name, content in document['files'].items()}, \
        headers['ETag']


def store(files, etag):
    """Writes the state only over the version this worker read; returns whether it was written."""
    body = json.dumps({'schema': SCHEMA, 'host': HOST, 'files': {
        name: base64.b64encode(content).decode('ascii') for name, content in sorted(files.items())}}).encode()
    condition = {'If-Match': etag} if etag else {'If-None-Match': '*'}
    status, _, _ = call('PUT', STATE_URL, {'x-ms-blob-type': 'BlockBlob', 'Content-Type': 'application/json',
                                           **condition}, body)
    return status == 201


def restore(files):
    for name, content in files.items():
        parts = name.split('/')
        if parts[0] not in TREES or any(part in ('', '.', '..') or part.startswith('.') for part in parts):
            raise ValueError('the stored TLS state names a path outside Caddy\'s trees')
        path = DATA.joinpath(*parts)
        path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        if path.is_file() and path.read_bytes() == content:
            continue
        # Replace each file whole, so Caddy never reads one half written.
        temporary = path.with_name('.' + path.name + '.puck')
        temporary.write_bytes(content)
        os.chmod(temporary, 0o600)
        os.replace(temporary, path)


def lease(action, lease_id):
    """Renews or releases this worker's lease on the lock blob; returns whether the lease answered."""
    status, _, _ = call('PUT', LOCK_URL + '?comp=lease', {'x-ms-lease-action': action, 'x-ms-lease-id': lease_id})
    return status == 200


def acquire():
    """Takes the issuance lease; returns its id, or None while another worker holds it."""
    # The lock blob is created once and only ever leased, never written again.
    call('PUT', LOCK_URL, {'x-ms-blob-type': 'BlockBlob', 'If-None-Match': '*'}, b'')
    lease_id = str(uuid.uuid4())
    status, _, _ = call('PUT', LOCK_URL + '?comp=lease', {'x-ms-lease-action': 'acquire',
                                                          'x-ms-lease-duration': str(LEASE_SECONDS),
                                                          'x-ms-proposed-lease-id': lease_id})
    return lease_id if status == 201 else None


def sync():
    """Stores the local certificate when it is newer than the stored one, and restores the stored one when it is
    newer. Returns the local certificate's validity afterwards."""
    files, etag = stored()
    mine = local_files()
    theirs_span, mine_span = leaf(files), leaf(mine)
    if newer(mine_span, theirs_span):
        if store(mine, etag):
            log('stored the local TLS state')
        return mine_span
    # A stored account is restored even without a newer certificate, so this worker never registers another.
    if newer(theirs_span, mine_span) or (has_account(files) and not has_account(mine)):
        restore(files)
        log('restored the stored TLS state')
        return leaf(local_files())
    return mine_span


def has_account(files):
    return any(name.startswith('acme/') for name in files)


def start():
    """Waits until Caddy may start. Returns the issuance lease's id when this worker must issue, or None."""
    waiting = False
    while True:
        try:
            if fresh(leaf(local_files()), time.time()):
                return None
            if fresh(sync(), time.time()):
                return None
            lease_id = acquire()
            if lease_id is not None:
                # Another worker may have stored its certificate between the read and the lease.
                if fresh(sync(), time.time()):
                    lease('release', lease_id)
                    return None
                log('holding the TLS issuance lease; Caddy may issue')
                return lease_id
            if not waiting:
                log('another worker holds the TLS issuance lease; waiting for its certificate')
                waiting = True
        except (urllib.error.URLError, OSError, ValueError, KeyError) as error:
            log('TLS state unavailable, retrying: ' + str(error))
        time.sleep(WAIT_SECONDS)


def notify(message):
    path = os.environ.get('NOTIFY_SOCKET')
    if path:
        with socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM) as channel:
            channel.connect('\0' + path[1:] if path.startswith('@') else path)
            channel.sendall(message.encode())


def hold(lease_id, held):
    """Advances one round while this worker holds the issuance lease taken at `held`; returns the lease's id, or None
    once the issued certificate is stored or the lease is given up."""
    if fresh(leaf(local_files()), time.time()):
        sync()
        lease('release', lease_id)
        log('stored the issued certificate and released the TLS issuance lease')
        return None
    if time.time() - held > HOLD_SECONDS or not lease('renew', lease_id):
        lease('release', lease_id)
        log('gave up the TLS issuance lease before Caddy stored a certificate')
        return None
    return lease_id


def main():
    os.umask(0o077)
    lease_id = start()
    held = time.time()
    notify('READY=1')
    synced = 0.0
    while True:
        try:
            if lease_id is not None:
                lease_id = hold(lease_id, held)
            elif time.time() - synced > SYNC_SECONDS:
                sync()
                synced = time.time()
        except Exception as error:
            # The keeper never takes Caddy down: a failed round is retried on the next.
            log('TLS state round failed: ' + str(error))
        time.sleep(RENEW_SECONDS)


if __name__ == '__main__':
    main()
