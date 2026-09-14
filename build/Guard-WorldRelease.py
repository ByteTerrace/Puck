#!/usr/bin/env python3
"""Refuse delayed VM effects whose durable release operation is no longer current.

The caller holds /run/puck-world-release.lock across this check and its effect.
The request and source are embedded in retained bootstrap or run-command input;
neither follows mutable files installed by a different release.
"""
import base64
import json
import sys
import urllib.parse
import urllib.request


def validate(request, record):
    operation = record.get('pendingOperationId')
    if (record.get('schema') != 'puck.world.release-group.v1'
            or record.get('deploymentGroup') != request['group'] or record.get('owner') != request['owner']):
        raise ValueError('no matching managed release group exists')
    if not operation:
        # A replacement VM may boot the already admitted release after an OS failure.
        # An obsolete extension still cannot boot an image that is no longer active.
        if (request.get('bootRelease') and record.get('activeRelease') == request['bootRelease']
                and record.get('admission') == 1):
            return
        raise ValueError('no matching managed release operation is pending')
    if request.get('operation') and operation != request['operation']:
        raise ValueError('the release operation changed before the guest effect')
    phase = record.get('pendingPhase')
    if 'phases' in request and phase not in request['phases']:
        raise ValueError('the release phase changed before the guest effect')
    if 'source' in request and record.get('pendingSourceRelease') != request['source']:
        raise ValueError('the source release changed before the guest effect')
    if 'target' in request and record.get('pendingTargetRelease') != request['target']:
        raise ValueError('the target release changed before the guest effect')
    if 'bootRelease' in request:
        expected = request['bootRelease']
        candidate = phase in (2, 3, 4) and record.get('pendingTargetRelease') == expected
        recovery = phase == 6 and record.get('pendingSourceRelease') == expected
        if not (candidate or recovery):
            raise ValueError('this retained bootstrap is not authorized in the current phase')


def guard(request):
    # IMDS must bypass proxies; storage uses normal HTTPS and fails closed on any error.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    query = urllib.parse.urlencode({'api-version': '2018-02-01',
                                   'resource': 'https://storage.azure.com/',
                                   'client_id': request['clientId']})
    identity = urllib.request.Request('http://169.254.169.254/metadata/identity/oauth2/token?' + query,
                                      headers={'Metadata': 'true'})
    with opener.open(identity, timeout=20) as response:
        token = json.load(response)['access_token']
    url = request['endpoint'].rstrip('/') + '/' + '/'.join(urllib.parse.quote(segment, safe='') for segment in
        (request['owner'], 'private', 'puck', 'hosted', 'release-groups', request['group'] + '.json'))
    read = urllib.request.Request(url, headers={'Authorization': 'Bearer ' + token,
                                               'x-ms-version': '2023-11-03'})
    with urllib.request.urlopen(read, timeout=20) as response:
        payload = response.read(1024 * 1024 + 1)
    if len(payload) > 1024 * 1024:
        raise ValueError('release group exceeds its document limit')
    validate(request, json.loads(payload))


if __name__ == '__main__':
    try:
        guard(json.loads(base64.b64decode(sys.argv[1], validate=True)))
    except Exception as error:
        sys.exit('world release guest guard refused: ' + str(error))
