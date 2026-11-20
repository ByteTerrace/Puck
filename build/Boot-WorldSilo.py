#!/usr/bin/env python3
"""Fetch a world release's retained bootstrap, verify it, and run it with the release's signing keys.

The VM extension's protected command runs this program with PUCK_RELEASE_INPUTS set to base64 JSON holding the full
pin of the release's retained deployment inputs and the federation signing keys by file name: the only secrets a
deployment holds. The inputs themselves (the bootstrap script with its silo document, the compute template) are an
immutable object in the world owner's private release content store, which this VM's managed identity reads. Their
bytes are checked against the pin before anything runs, so the command fixes exactly what boots.
"""
import base64
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

CONTENT = '__CONTENT_URL__'
CLIENT_ID = '__CLIENT_ID__'
LIMIT = 16 * 1024 * 1024
ATTEMPTS = 30


def digest(pin):
    match = re.fullmatch(r'sha256/([0-9a-f]{64})', pin)
    if not match:
        raise ValueError('the retained inputs need a full content pin')
    return match.group(1)


def fetch(hex_digest):
    # IMDS must bypass proxies; storage uses normal HTTPS.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    query = urllib.parse.urlencode({'api-version': '2018-02-01',
                                    'resource': 'https://storage.azure.com/',
                                    'client_id': CLIENT_ID})
    identity = urllib.request.Request('http://169.254.169.254/metadata/identity/oauth2/token?' + query,
                                      headers={'Metadata': 'true'})
    with opener.open(identity, timeout=20) as response:
        token = json.load(response)['access_token']
    read = urllib.request.Request(CONTENT + hex_digest, headers={'Authorization': 'Bearer ' + token,
                                                                 'x-ms-version': '2023-11-03'})
    with urllib.request.urlopen(read, timeout=60) as response:
        return response.read(LIMIT + 1)


def read_inputs(pin):
    hex_digest = digest(pin)
    for attempt in range(1, ATTEMPTS + 1):
        try:
            payload = fetch(hex_digest)
            break
        except (urllib.error.URLError, TimeoutError, OSError, KeyError, ValueError):
            # A new VM's identity and network can lag its first boot; the pin check below still fails closed.
            if attempt == ATTEMPTS:
                raise
            time.sleep(10)
    if len(payload) > LIMIT:
        raise ValueError('the retained inputs exceed their byte budget')
    if hashlib.sha256(payload).hexdigest() != hex_digest:
        raise ValueError('the retained inputs do not match their pin')
    return json.loads(payload)


def main():
    request = json.loads(base64.b64decode(os.environ.pop('PUCK_RELEASE_INPUTS'), validate=True))
    keys = request['keys']
    if not isinstance(keys, dict) or not keys:
        raise ValueError('the protected command carries no signing keys')
    script = read_inputs(request['inputs'])['Bootstrap']
    with tempfile.NamedTemporaryFile('w', prefix='puck-world-bootstrap-', suffix='.sh', dir='/run',
                                     delete=False) as file:
        file.write(script)
    try:
        environment = dict(os.environ, PUCK_FEDERATION_KEYS=json.dumps(keys))
        result = subprocess.run(['/bin/bash', file.name], env=environment, stdin=subprocess.DEVNULL, check=False)
    finally:
        os.unlink(file.name)
    sys.exit(result.returncode)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        sys.exit('world bootstrap launcher refused: ' + str(error))
