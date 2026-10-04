#!/usr/bin/env python3
"""Bounded, real stdio MCP qualification. ROMs are supplied locally, never copied into the repo."""
import argparse
import base64
import hashlib
import json
import queue
import struct
import subprocess
import threading
from pathlib import Path


class Client:
    def __init__(self, command, cwd):
        self.process = subprocess.Popen(command, cwd=cwd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, text=True)
        self.responses = queue.Queue()
        self.errors = []
        self.next_id = 0
        threading.Thread(target=self._stdout, daemon=True).start()
        threading.Thread(target=self._stderr, daemon=True).start()
        self.request('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {},
                                   'clientInfo': {'name': 'sms-qualification', 'version': '1'}})
        self.send({'jsonrpc': '2.0', 'method': 'notifications/initialized'})

    def _stdout(self):
        try:
            for line in self.process.stdout:
                if len(line) > 16 * 1024 * 1024:
                    raise RuntimeError('MCP response exceeds 16 MiB')
                self.responses.put(json.loads(line))
        except Exception as error:
            self.responses.put(error)
        finally:
            self.responses.put(None)

    def _stderr(self):
        for line in self.process.stderr:
            self.errors.append(line[:1000])
            self.errors[:] = self.errors[-20:]

    def send(self, data):
        self.process.stdin.write(json.dumps(data) + '\n')
        self.process.stdin.flush()

    def request(self, method, params):
        self.next_id += 1
        self.send({'jsonrpc': '2.0', 'id': self.next_id, 'method': method, 'params': params})
        while True:
            reply = self.responses.get(timeout=45)
            if reply is None or isinstance(reply, Exception):
                raise RuntimeError(f'MCP exited or invalid stdout: {reply}; {self.errors}')
            if reply.get('id') == self.next_id:
                if 'error' in reply:
                    raise RuntimeError(reply['error'])
                return reply['result']

    def call(self, name, **arguments):
        result = self.request('tools/call', {'name': name, 'arguments': arguments})
        if result.get('isError'):
            raise RuntimeError(f'{name}: {result}')
        content = result['content'][0]
        return content if content['type'] == 'image' else json.loads(content['text'])

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)


def gameplay(client, path, output):
    """Title -> start -> start -> move right/jump; the PNG is for visual inspection of real gameplay."""
    loaded = client.call('load_rom', path=str(path.resolve()))
    # SMS Start is the Pause button (NMI); SMS games start with button 1, GG games with Start.
    start = 'start' if loaded['system'] == 'gg' else '1'
    # Timelines are bounded to 600 frames per call.
    for segments in ([[600, '']], [[10, start], [300, '']], [[10, start], [400, '']],
                     [[120, 'right'], [10, 'right 1'], [10, 'right']]):
        client.call('run_input_timeline', segments=[{'frames': f, 'buttons': b} for f, b in segments])
    image = client.call('capture_screen')
    (output / (path.stem + '-gameplay.png')).write_bytes(base64.b64decode(image['data']))


def qualify(client, path, output, frames):
    loaded = client.call('load_rom', path=str(path.resolve()))
    fm_writes = len(client.call('trace_fm_writes', frames=frames, maxEntries=16384)['writes']) if loaded['fmUnit'] else None
    client.call('reset')
    observed = client.call('observe_execution', frames=frames, sampleEvery=max(1, frames // 10), addresses=[49152, 49153])
    assert observed['result']['reason'] == 'frame_complete', observed['result']['reason']
    assert observed['result']['framesExecuted'] == frames
    image = client.call('capture_screen')
    png = base64.b64decode(image['data'])
    assert png[:8] == b'\x89PNG\r\n\x1a\n'
    width, height = struct.unpack('>II', png[16:24])
    assert (width, height) in [(160, 144), (256, 192), (256, 224), (256, 240)]
    output.mkdir(parents=True, exist_ok=True)
    stem = path.stem
    (output / (stem + '.png')).write_bytes(png)
    snapshot = client.call('save_state')['stateBase64']
    audio = client.call('capture_audio', frames=30)
    wav = base64.b64decode(audio['wavBase64'])
    assert wav[:4] == b'RIFF' and len(wav) > 44
    (output / (stem + '.wav')).write_bytes(wav)
    after = client.call('get_state')
    client.call('load_state', stateBase64=snapshot)
    replay = client.call('capture_audio', frames=30)
    assert audio['wavBase64'] == replay['wavBase64'], 'Audio replay differs'
    assert after == client.call('get_state'), 'CPU/VDP replay differs'
    client.call('run_input_timeline', segments=[{'frames': 1, 'buttons': 'start', 'player': 1},
                                              {'frames': 30, 'buttons': '', 'player': 1},
                                              {'frames': 30, 'buttons': 'right 1', 'player': 1}])
    gameplay = client.call('capture_screen')
    (output / (stem + '-input.png')).write_bytes(base64.b64decode(gameplay['data']))
    state = client.call('get_state')
    bp = client.call('set_breakpoint', address=str(state['cpu']['pc']))
    stopped = client.call('continue_until_break', maxInstructions=100)
    assert stopped['reason'] == 'breakpoint' and stopped['instructionsExecuted'] == 0
    client.call('clear_breakpoint', id=bp['id'])
    tiles = client.call('dump_tileset', count=256)
    (output / (stem + '-tiles.png')).write_bytes(base64.b64decode(tiles['data']))
    fm = client.call('read_fm_state') if loaded['fmUnit'] else None
    summary = {'rom': str(path), 'sha256': loaded['romSha256'], 'system': loaded['system'], 'framesObserved': frames,
               'fmUnit': loaded['fmUnit'], 'fmWrites': fm_writes, 'fmAudioControl': fm and fm['audioControl'],
               'screen': [width, height], 'differentScreens': len(set(s['screenSha256'] for s in observed['samples'])),
               'audioSamples': audio['sampleValues'], 'audioPeak': audio['peak'], 'audioRms': audio['rms'],
               'audioSha256': hashlib.sha256(wav).hexdigest(), 'deterministicReplay': True,
               'breakpointVerified': True, 'finalFrames': state['frames']}
    return summary


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('roms', nargs='+', type=Path)
    parser.add_argument('--server', type=Path)
    parser.add_argument('--output', type=Path, default=Path('artifacts/qualification'))
    parser.add_argument('--frames', type=int, default=180)
    parser.add_argument('--gameplay', action='store_true', help='also save a title/Start/move screenshot per ROM')
    args = parser.parse_args()
    assert 1 <= args.frames <= 600
    root = Path(__file__).resolve().parent.parent
    server = args.server or root / 'src/Zafiro.MasterSystem.Debug.Mcp/bin/Release/net10.0/Zafiro.MasterSystem.Debug.Mcp.dll'
    client = Client(['dotnet', str(server.resolve())], root)
    try:
        tools = client.request('tools/list', {})['tools']
        names = {t['name'] for t in tools}
        assert {'capture_audio', 'observe_execution', 'set_breakpoint', 'dump_tileset', 'read_fm_state'} <= names
        reports = []
        for rom in args.roms:
            report = qualify(client, rom, args.output, args.frames)
            if args.gameplay:
                gameplay(client, rom, args.output)
            reports.append(report)
            print(json.dumps(report), flush=True)
        args.output.mkdir(parents=True, exist_ok=True)
        (args.output / 'report.json').write_text(json.dumps({'tools': len(tools), 'roms': reports}, indent=2))
        print(json.dumps({'tools': len(tools), 'report': str(args.output / 'report.json')}))
    finally:
        client.close()


if __name__ == '__main__':
    main()
