#!/usr/bin/env python3
"""Focused real-HTTP verification of the running local service. No hardware simulation."""
import argparse
import io
import json
import time
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import urllib.error
import urllib.request
import zipfile
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--url', default='http://127.0.0.1:37652')
args = parser.parse_args()
base = args.url.rstrip('/')

def call(path, body=None, token=None, method=None, headers=None):
    h = dict(headers or {})
    if token:
        h['X-Phyphox-Token'] = token
    if body is not None:
        h.update({'Content-Type': 'application/json', 'X-Phyphox-Client': 'browser'})
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(base + path, data=data, method=method, headers=h)
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            return response.status, response.read(), response.headers
    except urllib.error.HTTPError as error:
        return error.code, error.read(), error.headers

def data(path, body=None):
    status, value, _ = call(path, body, token)
    assert status == 200, (path, status, value[:500])
    return json.loads(value)

assert call('/api/v1/session')[0] == 401
assert call('/api/v1/bootstrap', headers={'Origin': 'https://untrusted.invalid'})[0] == 403
assert call('/api/v1/bootstrap', headers={'Host': 'untrusted.invalid'})[0] == 403
token = json.loads(call('/api/v1/bootstrap')[1])['token']
items = data('/api/v1/library')['items']
official = [x for x in items if x['source'] == 'official']
assert len(official) == 67, len(official)
sample = next(x for x in items if x['source'] == 'sample')
snapshot = data('/api/v1/session/load', {'id': sample['id']})
assert snapshot['buffers']['square'] == [4], snapshot
snapshot = data('/api/v1/session/commands', {'command': 'set', 'buffer': 'x', 'values': [3], 'requestId': 'check-set-3'})
assert snapshot['buffers']['square'] == [9], snapshot
snapshot = data('/api/v1/session/commands', {'command': 'set', 'buffer': 'x', 'values': [7], 'requestId': 'check-set-3'})
assert snapshot['buffers']['square'] == [9], 'Duplicate request was applied twice'
assert data('/api/v1/session/commands', {'command': 'start'})['status'] == 'running'
assert data('/api/v1/session/commands', {'command': 'pause'})['status'] == 'paused'
assert data('/api/v1/session/commands', {'command': 'stop'})['buffers']['square'] == [9]

status, csv, _ = call('/api/v1/exports?format=csv', token=token)
assert status == 200
with zipfile.ZipFile(io.BytesIO(csv)) as archive:
    rows = archive.read(next(n for n in archive.namelist() if not n.startswith('meta/'))).decode()
    assert '3,9' in rows, rows
status, xlsx, _ = call('/api/v1/exports?format=xlsx', token=token)
assert status == 200, xlsx
with zipfile.ZipFile(io.BytesIO(xlsx)) as archive:
    ns = {'s': 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
    sheet = ET.fromstring(archive.read('xl/worksheets/sheet1.xml'))
    values = [n.text for n in sheet.findall('.//s:v', ns)]
    assert values == ['3', '9'], values
status, state, _ = call('/api/v1/exports?format=state', token=token)
assert status == 200
root = ET.fromstring(state)
assert root.find("./data-containers/container[.='x']").get('init') == '3'

# Storage workflows use actual service files and journals, never synthesized device samples.
sealed = [r for r in data('/api/v1/recordings')['items'] if r['complete']]
assert sealed, 'Stopped experiment did not produce a complete journal'
replayed = data('/api/v1/recordings/' + sealed[0]['id'] + '/replay', {})
assert replayed['buffers']['square'] == [9] and replayed['hardwareOutputEnabled'] is False
saved = data('/api/v1/snapshots/save', {'id': 'focused-service-check'})
assert any(x['id'] == saved['id'] for x in data('/api/v1/snapshots')['items'])
data('/api/v1/session/commands', {'command': 'clear'})
restored = data('/api/v1/snapshots/' + saved['id'] + '/restore', {})
assert restored['buffers']['square'] == [9] and restored['status'] == 'paused'

def upload(path, name, content, fields=None):
    boundary = 'phyphox-upload-focused-check'
    body = bytearray()
    for key, value in (fields or {}).items():
        body.extend(f'--{boundary}\r\nContent-Disposition: form-data; name="{key}"\r\n\r\n{value}\r\n'.encode())
    body.extend(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{name}"\r\nContent-Type: application/octet-stream\r\n\r\n'.encode())
    body.extend(content)
    body.extend(f'\r\n--{boundary}--\r\n'.encode())
    req = urllib.request.Request(base + path, data=body, headers={'Content-Type': 'multipart/form-data; boundary=' + boundary, 'X-Phyphox-Client': 'browser', 'X-Phyphox-Token': token})
    with urllib.request.urlopen(req, timeout=15) as response:
        return json.load(response)

options = {'delimited': {'columns': [{'index': 0, 'buffer': 'x'}], 'hasHeader': True}, 'analyze': True}
result = upload('/api/v1/imports/data', 'actual.csv', b'value\n5\n', {'optionsJSON': json.dumps(options)})
assert result['session']['buffers']['square'] == [25] and result['originalPreserved']

# Exercise the network-to-engine boundary against a real local HTTP fixture.
received = []
class Fixture(BaseHTTPRequestHandler):
    def do_POST(self):
        received.append(json.loads(self.rfile.read(int(self.headers['Content-Length']))))
        payload = b'{"result":[7]}'
        self.send_response(200)
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)
    def log_message(self, *args):
        pass
fixture = ThreadingHTTPServer(('127.0.0.1', 0), Fixture)
threading.Thread(target=fixture.serve_forever, daemon=True).start()
try:
    xml = f'''<phyphox version="1.20"><title>HTTP boundary check</title><category>Verification</category>
    <data-containers><container size="0" init="1,2">send</container><container size="0">x</container><container size="0">square</container></data-containers>
    <views><view label="test"><button label="Send"><trigger>fixture</trigger></button><value label="Result"><input>square</input></value></view></views>
    <analysis><formula formula="[1]*[1]"><input as="in" keep="true">x</input><output as="out" append="false">square</output></formula></analysis>
    <network><connection id="fixture" service="http/post" address="http://127.0.0.1:{fixture.server_port}/" conversion="json" interval="0"><send id="samples" keep="false">send</send><send id="timing" type="time"/><receive id="result" append="false">x</receive></connection></network>
    </phyphox>'''
    imported = upload('/api/v1/library/import', 'network.phyphox', xml.encode())
    old = {r['id'] for r in data('/api/v1/recordings')['items'] if r['complete']}
    loaded = data('/api/v1/session/load', {'id': imported['items'][0]['id']})
    assert loaded['canStart'], loaded['issues']
    data('/api/v1/session/commands', {'command': 'start'})
    data('/api/v1/session/commands', {'command': 'button', 'elementId': '0:0'})
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        current = data('/api/v1/session')
        if current['buffers']['square'] == [49]:
            break
        time.sleep(.05)
    assert current['buffers']['square'] == [49], current
    assert received[0]['samples'] == [1, 2] and received[0]['timing']['events'][0]['event'] == 'START'
    assert current['buffers']['send'] == [], 'keep=false consumption was not committed'
    data('/api/v1/session/commands', {'command': 'stop'})
    new = [r for r in data('/api/v1/recordings')['items'] if r['complete'] and r['id'] not in old]
    again = data('/api/v1/recordings/' + new[0]['id'] + '/replay', {})
    assert again['buffers']['square'] == [49] and again['buffers']['send'] == []
finally:
    fixture.shutdown()
    fixture.server_close()

blocked = next(x for x in official if 'input:sensor' in x['requirements'] and 'output:audio' not in x['requirements'])
status, value, _ = call('/api/v1/session/load', {'id': blocked['id']}, token)
if status == 200:
    assert not json.loads(value)['canStart']
    assert call('/api/v1/session/commands', {'command': 'start'}, token)[0] >= 400
else:
    assert status in (400, 422), value

boundary = 'phyphox-focused-check'
with io.BytesIO() as z:
    with zipfile.ZipFile(z, 'w') as archive:
        archive.writestr('../escape.phyphox', '<phyphox/>')
    payload = z.getvalue()
multipart = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="traversal.zip"\r\nContent-Type: application/zip\r\n\r\n'.encode() + payload + f'\r\n--{boundary}--\r\n'.encode())
req = urllib.request.Request(base + '/api/v1/library/import', data=multipart,
    headers={'Content-Type': 'multipart/form-data; boundary=' + boundary, 'X-Phyphox-Client': 'browser', 'X-Phyphox-Token': token})
try:
    urllib.request.urlopen(req)
    raise AssertionError('Traversal archive accepted')
except urllib.error.HTTPError as e:
    assert e.code in (400, 409), e.code

print('PASS: auth/origin/host; 67 official files; load/formula/commands; idempotence; lifecycle; CSV/XLSX/state; storage save/restore/import/replay; live HTTP analysis+journal; hardware gate; ZIP traversal rejection')
print('NOT TESTED: Windows execution, physical devices, timing, performance, full format/UI compatibility')
