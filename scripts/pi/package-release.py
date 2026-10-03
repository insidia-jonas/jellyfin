#!/usr/bin/env python3
"""Create a checksummed server/plugin bundle; contains no configuration or credentials."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET
import zipfile


def package(repo, server, output):
    repo, server, output = map(Path, (repo, server, output))
    if not (server / 'jellyfin.dll').is_file():
        raise ValueError('Publish the complete linux-arm64 server first.')
    plugin = repo / 'plugins/Jellyfin.Plugin.TreasureMaps'
    version = ET.parse(plugin / 'Jellyfin.Plugin.TreasureMaps.csproj').findtext('.//Version')
    dll = plugin / 'bin/Release/net10.0/Jellyfin.Plugin.TreasureMaps.dll'
    revision = subprocess.check_output(['git', '-C', str(repo), 'rev-parse', 'HEAD'], text=True).strip()
    dirty = bool(subprocess.check_output(['git', '-C', str(repo), 'status', '--porcelain'], text=True).strip())
    files = {f'server/{p.relative_to(server).as_posix()}': p for p in server.rglob('*') if p.is_file()}
    files['plugin/Jellyfin.Plugin.TreasureMaps.dll'] = dll
    manifest = {'schema': 1, 'commit': revision, 'dirty': dirty, 'pluginVersion': version, 'runtime': 'linux-arm64',
                'files': {name: hashlib.sha256(path.read_bytes()).hexdigest() for name, path in files.items()}}
    with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED) as archive:
        for name, path in files.items():
            archive.write(path, name)
        archive.writestr('release.json', json.dumps(manifest, indent=2))
    print(json.dumps({'bundle': str(output), 'commit': revision, 'dirty': dirty, 'files': len(files), 'pluginVersion': version}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', default=str(Path(__file__).resolve().parents[2]))
    parser.add_argument('--server', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    package(args.repo, args.server, args.output)
