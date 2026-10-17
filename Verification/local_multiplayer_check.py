"""Run server, six-client smoke checks and encrypted audit replay in one local session.

Build the server first. Requires the existing websockets test dependency.
No cloud resources are contacted. Test artifacts stay in the ignored audit directory.
"""
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import urllib.request
import uuid

root = Path(__file__).resolve().parent.parent
run_dir = root / '.drawmaha-audit' / ('smoke-' + uuid.uuid4().hex)
run_dir.mkdir(parents=True)
with socket.socket() as probe:
    probe.bind(('127.0.0.1', 0))
    port = probe.getsockname()[1]
env = dict(os.environ)
env.update(ASPNETCORE_ENVIRONMENT='Development', DRAWMAHA_ACCESS_KEY='obsolete-key-must-be-ignored',
           DRAWMAHA_SHOWDOWN_SECONDS='1', DRAWMAHA_RECONNECT_SECONDS='3',
           DRAWMAHA_AUDIT_DIRECTORY=str(run_dir))
env.pop('DRAWMAHA_TABLES_ENDPOINT', None)
env.pop('DRAWMAHA_AUDIT_KEY', None)
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
with (run_dir / 'server.log').open('w', encoding='utf-8') as log:
    process = subprocess.Popen(
        ['dotnet', str(root / 'Server/bin/Debug/net10.0/Drawmaha.Server.dll'), '--urls', f'http://127.0.0.1:{port}'],
        cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
    try:
        for attempt in range(100):
            if process.poll() is not None:
                raise RuntimeError('Local test server exited; inspect its server.log.')
            try:
                with opener.open(f'http://127.0.0.1:{port}/health', timeout=1):
                    break
            except OSError:
                time.sleep(.1)
        else:
            raise RuntimeError('Local test server did not become ready.')
        test = 'profile_smoke.py' if '--profile' in sys.argv else 'chat_smoke.py' if '--chat' in sys.argv else 'multiplayer_smoke.py'
        subprocess.run([sys.executable, str(root / 'Verification' / test),
                        f'ws://127.0.0.1:{port}/ws'], cwd=root, env=env, check=True, timeout=180)
    finally:
        # Stop only the child process created above. This deliberately tests crash/open-stream inspection.
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)
audits = list(run_dir.glob('audit-*.jsonl'))
if len(audits) != 1:
    raise RuntimeError('Expected one audit stream from the local server.')
subprocess.run(['dotnet', 'run', '--project', 'Verification/RngChecks.csproj', '--no-restore', '--',
                'verify', str(audits[0]), '--allow-open'], cwd=root, env=env, check=True, timeout=120)
