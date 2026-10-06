#!/usr/bin/env python3
"""Idempotent Raspberry Pi installation, discovery and API reconciliation.

Run --plan first (read-only); --apply installs the reviewed revision and repairs links.
Run as root with --user specifying the existing service owner. No data resets.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import shutil
import sqlite3
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
import stack_automation

PLUGIN_ID = 'e2c9a6f4-8b1d-4f3a-9c2e-7a5b6d4c3e21'
SERVICE = 'jellyfin12'


def run(*args, capture=False):
    return subprocess.run([str(a) for a in args], check=True, text=True,
                          stdout=subprocess.PIPE if capture else None).stdout


def request(base, route, key=None, method='GET', body=None, timeout=45):
    headers = {'Content-Type': 'application/json'}
    if key:
        headers['Authorization'] = 'MediaBrowser Token="' + key + '"'
    data = json.dumps(body).encode() if body is not None else b'' if method == 'POST' else None
    req = urllib.request.Request(base.rstrip('/') + '/' + route, headers=headers, data=data, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as response:
            raw = response.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        # Never include a URL, response body or token in an installation log.
        raise RuntimeError(f'API request failed: HTTP {error.code}') from None


def admin_token(base, data):
    """Read existing sessions, validate administrator policy, never print credentials."""
    supplied = os.environ.get('JELLYFIN_API_KEY')
    if supplied:
        request(base, 'Auth/Keys', supplied)
        return supplied
    db = data / 'data/jellyfin.db'
    if not db.exists():
        return None
    with sqlite3.connect(db.as_uri() + '?mode=ro', uri=True) as connection:
        for token, user in connection.execute('SELECT AccessToken,UserId FROM Devices ORDER BY DateLastActivity DESC LIMIT 50'):
            user = str(uuid.UUID(bytes_le=user)) if isinstance(user, bytes) else str(user)
            try:
                if request(base, 'Users/' + user, token).get('Policy', {}).get('IsAdministrator'):
                    return token
            except (RuntimeError, urllib.error.URLError):
                continue
    return None


def arr_configuration(path, fallback_port):
    root = ET.parse(path).getroot()
    port = root.findtext('Port') or str(fallback_port)
    return {'url': 'http://127.0.0.1:' + port + (root.findtext('UrlBase') or ''), 'apiKey': root.findtext('ApiKey') or ''}


def discover(args, home):
    found = []
    for name, port in [('radarr', 7878), ('sonarr', 8989)]:
        candidates = [Path(getattr(args, name + '_config'))] if getattr(args, name + '_config') else [Path('/var/lib') / name / 'config.xml', home / '.config' / name.capitalize() / 'config.xml']
        for path in candidates:
            if path.is_file():
                found.append({'service': name, **arr_configuration(path, port)})
                break
    sabpaths = [Path(args.sab_config)] if args.sab_config else [Path('/etc/sabnzbdplus/sabnzbd.ini'), Path('/var/lib/sabnzbd/sabnzbd.ini'), home / '.sabnzbd/sabnzbd.ini', home / '.config/sabnzbd/sabnzbd.ini']
    for path in sabpaths:
        if path.is_file():
            cfg = sab_misc(path.read_text())
            found.append({'service': 'sabnzbd', 'url': 'http://127.0.0.1:' + cfg.get('port', '8080'), 'apiKey': cfg.get('api_key', '')})
            break
    return found


def sab_misc(text):
    # SAB uses ConfigObj: global keys and nested [[sections]], not a standard INI.
    values, inside = {}, False
    for line in text.splitlines():
        line = line.strip()
        if line.startswith('['):
            inside = line.lower() == '[misc]'
        elif inside and '=' in line and not line.startswith('#'):
            key, value = line.split('=', 1)
            values[key.strip()] = value.strip().strip('"').strip("'")
    return values


def verify_bundle(bundle, stage):
    """Reject traversal, symlinks, unmanifested files and corrupt artifacts before stopping."""
    with zipfile.ZipFile(bundle) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError('Duplicate archive member')
        manifest = json.loads(archive.read('release.json'))
        if manifest.get('schema') != 1 or manifest.get('runtime') != 'linux-arm64':
            raise ValueError('Unsupported release manifest')
        files = manifest['files']
        if set(names) != set(files) | {'release.json'} or 'server/jellyfin.dll' not in files or 'plugin/Jellyfin.Plugin.TreasureMaps.dll' not in files:
            raise ValueError('Incomplete or unmanifested bundle')
        for name, digest in files.items():
            path = PurePosixPath(name)
            if path.is_absolute() or '..' in path.parts or '\\' in name or path.parts[0] not in ('server', 'plugin'):
                raise ValueError('Unsafe archive path')
            raw = archive.read(name)
            if hashlib.sha256(raw).hexdigest() != digest:
                raise ValueError('Release checksum mismatch')
            target = stage / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(raw)
    return manifest


def installed_matches(root, manifest):
    for name, digest in manifest['files'].items():
        path = root / name if name.startswith('server/') else root / 'data/plugins/Treasure-Maps' / Path(name).name
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            return False
    return True


def wait_health(base):
    for _ in range(90):
        try:
            with urllib.request.urlopen(base.rstrip('/') + '/health', timeout=2) as response:
                if response.status == 200:
                    return
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(1)
    raise RuntimeError('Jellyfin did not become healthy; rolling back')


def chown_tree(path, account):
    os.chown(path, account.pw_uid, account.pw_gid)
    if path.is_dir():
        for item in path.rglob('*'):
            if not item.is_symlink():
                os.chown(item, account.pw_uid, account.pw_gid)


def wait_management(base, token, version):
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        try:
            status = request(base, 'TreasureMaps/Management/Summary', token, timeout=12)
            if status.get('version') != version:
                raise RuntimeError('New plugin capability check failed')
            return
        except RuntimeError as error:
            if 'HTTP 503' not in str(error):
                raise
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(1)
    raise RuntimeError('Management API did not become ready; rolling back')


def check_playback_before_deploy(url, token, interrupt_playback=False):
    if not token or not any(s.get('NowPlayingItem') for s in request(url, 'Sessions', token)):
        return
    if not interrupt_playback:
        raise RuntimeError('Playback is active; installation deferred')
    print('Proceeding with the explicitly requested restart; existing playback will be interrupted.')


def deploy(args, account, bundle):
    root = args.out
    root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.release-', dir=root) as temporary:
        stage = Path(temporary)
        manifest = verify_bundle(bundle, stage)
        if manifest.get('dirty') and not args.allow_dirty:
            raise RuntimeError('Bundle contains uncommitted source; use a committed release')
        if installed_matches(root, manifest):
            (root / 'deployment.json').write_text(json.dumps(manifest, indent=2))
            print('Server and plugin checksums already match; no restart.')
            return manifest
        token = admin_token(args.url, root / 'data')
        check_playback_before_deploy(args.url, token, args.interrupt_playback)
        backup = root / 'backups' / ('stack-' + time.strftime('%Y%m%d-%H%M%S'))
        backup.mkdir(parents=True, mode=0o700)
        plugin = root / 'data/plugins/Treasure-Maps'
        unit = Path('/etc/systemd/system') / (SERVICE + '.service')
        had_server = (root / 'server').exists()
        had_unit = unit.exists()
        if had_unit:
            if str(root / 'server/jellyfin.dll') not in unit.read_text():
                raise RuntimeError('Existing service uses another installation path; specify its --out directory')
            shutil.copy2(unit, backup / 'service')
        # Keep the configured service definition and its FFmpeg / data paths.
        if not had_unit:
            web = args.web_dist or root / 'jellyfin-web/dist'
            if not (web / 'index.html').is_file():
                raise RuntimeError('Provide --web-dist pointing to a built jellyfin-web/dist')
            unit.write_text('[Unit]\nDescription=Jellyfin 12 Media Stack\nAfter=network-online.target\n\n[Service]\n'
                f'User={account.pw_name}\nGroup={account.pw_gid}\nWorkingDirectory={root}/server\n'
                f'Environment=DOTNET_ROOT={account.pw_dir}/.dotnet\n'
                f'ExecStart={account.pw_dir}/.dotnet/dotnet {root}/server/jellyfin.dll --datadir {root}/data --webdir {web}\n'
                'Restart=on-failure\nRestartSec=5\nUMask=0002\n\n[Install]\nWantedBy=multi-user.target\n')
            run('systemctl', 'daemon-reload')
        run('systemctl', 'stop', SERVICE)
        swapped = False
        try:
            # SQLite WAL and database are copied together with the service stopped.
            for rel in ('data/data', 'data/config', 'data/plugins/configurations'):
                source = root / rel
                if source.exists():
                    shutil.copytree(source, backup / rel)
            if plugin.exists():
                shutil.copytree(plugin, backup / 'plugin')
            if had_server:
                (root / 'server').rename(backup / 'server')
            (stage / 'server').rename(root / 'server')
            swapped = True
            plugin.mkdir(parents=True, exist_ok=True)
            shutil.copy2(stage / 'plugin/Jellyfin.Plugin.TreasureMaps.dll', plugin)
            meta = {'category': 'General', 'name': 'Evolution', 'guid': PLUGIN_ID, 'targetAbi': '12.0.0.0',
                    'version': manifest['pluginVersion'], 'status': 0, 'autoUpdate': False,
                    'description': 'Media management, targeted search, download imports and AI subtitles.',
                    'assemblies': ['Jellyfin.Plugin.TreasureMaps.dll']}
            (plugin / 'meta.json').write_text(json.dumps(meta, indent=2))
            chown_tree(root / 'server', account)
            chown_tree(plugin, account)
            if not had_server:
                chown_tree(root / 'data', account)
            run('systemctl', 'enable', '--now', SERVICE)
            wait_health(args.url)
            if token:
                wait_management(args.url, token, manifest['pluginVersion'])
            manifest['installedAt'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
            (root / 'deployment.json').write_text(json.dumps(manifest, indent=2))
            print('Installed release ' + manifest['commit'][:12] + '; backup: ' + str(backup))
        except BaseException:
            run('systemctl', 'stop', SERVICE)
            if swapped:
                (root / 'server').rename(backup / 'failed-server')
            if (backup / 'server').exists():
                (backup / 'server').rename(root / 'server')
            for rel in ('data/data', 'data/config', 'data/plugins/configurations'):
                saved = backup / rel
                if saved.exists():
                    current = root / rel
                    current.rename(backup / ('failed-' + rel.replace('/', '-')))
                    shutil.copytree(saved, current)
                    chown_tree(current, account)
            if (backup / 'plugin').exists():
                if plugin.exists():
                    plugin.rename(backup / 'failed-plugin')
                shutil.copytree(backup / 'plugin', plugin)
                chown_tree(plugin, account)
            if had_unit:
                shutil.copy2(backup / 'service', unit)
                run('systemctl', 'daemon-reload')
            if had_server:
                run('systemctl', 'start', SERVICE)
            raise
    return manifest


def install_missing(args, account, services):
    """Only install absent services. Existing systemd units and data always win."""
    installed = {s['service'] for s in services}
    arch = {'aarch64': 'arm64', 'x86_64': 'x64'}.get(platform.machine())
    if not arch:
        raise RuntimeError('64-bit Linux required')
    for name in ('radarr', 'sonarr', 'sabnzbd'):
        if name in installed:
            continue
        service_name = 'sabnzbdplus' if name == 'sabnzbd' else name
        present = subprocess.run(['systemctl', 'cat', service_name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0
        if present:
            print(name + ': installed service detected; supply its config path if discovery missed it.')
            continue
        if name == 'sabnzbd':
            run('apt-get', 'update')
            run('apt-get', 'install', '-y', 'sabnzbdplus')
            data = Path('/var/lib/sabnzbd')
            data.mkdir(exist_ok=True)
            config = data / 'sabnzbd.ini'
            if config.exists():
                raise RuntimeError('Existing SABnzbd config found; refusing to replace it')
            seed = '[misc]\nhost = 127.0.0.1\nport = 8080\nbrowser = 0\napi_key = ' + uuid.uuid4().hex + '\nnzb_key = ' + uuid.uuid4().hex + '\n'
            setup = stack_automation.load_setup(args.setup_config)
            if setup:
                for key, rel in [('download_dir', 'incomplete'), ('complete_dir', 'complete')]:
                    folder = Path(setup['mediaRoot']) / 'Downloads' / rel
                    stack_automation.ensure_directory(folder, account)
                    seed += key + ' = ' + json.dumps(str(folder)) + '\n'
            config.write_text(seed)
            chown_tree(data, account)
            config.chmod(0o600)
            unit = Path('/etc/systemd/system/sabnzbdplus.service')
            unit.write_text(f'[Unit]\nDescription=SABnzbd Media Stack\nAfter=network-online.target\n\n[Service]\nUser={account.pw_name}\nUMask=0002\n'
                            f'ExecStart=/usr/bin/sabnzbdplus -f {config} -s 127.0.0.1:8080 -b 0\nRestart=on-failure\n\n[Install]\nWantedBy=multi-user.target\n')
            run('systemctl', 'daemon-reload')
            run('systemctl', 'enable', '--now', 'sabnzbdplus')
            sab = {'service': 'sabnzbd', 'url': 'http://127.0.0.1:8080', 'apiKey': sab_misc(config.read_text())['api_key']}
            for attempt in range(30):
                try:
                    stack_automation.api(sab, {'mode': 'queue'})
                    break
                except (OSError, RuntimeError):
                    if attempt == 29: raise RuntimeError('SABnzbd did not become ready') from None
                    time.sleep(1)
            print('SABnzbd started with a private API key. Provider and category setup follows automatically.')
            continue
        destination = Path('/opt') / name.capitalize()
        data = Path('/var/lib') / name
        if destination.exists() or data.exists():
            raise RuntimeError(name + ': existing files found; refusing a duplicate installation')
        run('apt-get', 'update')
        run('apt-get', 'install', '-y', 'libsqlite3-0', 'libicu-dev', 'ca-certificates')
        url = (f'https://radarr.servarr.com/v1/update/master/updatefile?os=linux&runtime=netcore&arch={arch}' if name == 'radarr'
               else f'https://services.sonarr.tv/v1/download/main/latest?version=4&os=linux&arch={arch}')
        with tempfile.TemporaryDirectory(prefix='media-install-') as tmp:
            archive = Path(tmp) / 'app.tar.gz'
            urllib.request.urlretrieve(url, archive)
            with tarfile.open(archive) as tar:
                tar.extractall(Path(tmp) / 'unpacked', filter='data')
            shutil.move(str(Path(tmp) / 'unpacked' / name.capitalize()), destination)
        data.mkdir()
        chown_tree(destination, account)
        chown_tree(data, account)
        unit = Path('/etc/systemd/system') / (name + '.service')
        unit.write_text(f'[Unit]\nDescription={name.capitalize()}\nAfter=network-online.target\n\n[Service]\nUser={account.pw_name}\nUMask=0002\n'
                        f'ExecStart={destination}/{name.capitalize()} -nobrowser -data={data}\nRestart=on-failure\n\n[Install]\nWantedBy=multi-user.target\n')
        run('systemctl', 'daemon-reload')
        run('systemctl', 'enable', '--now', name)
        for _ in range(30):
            if (data / 'config.xml').is_file():
                try:
                    service = {'service': name, **arr_configuration(data / 'config.xml', 7878 if name == 'radarr' else 8989)}
                    stack_automation.api(service, 'system/status')
                    break
                except (OSError, RuntimeError):
                    pass
            time.sleep(1)
        else:
            raise RuntimeError(name + ': API did not become ready')


def reconcile(args, home, choices=None, setup=None):
    choices, setup = choices or {}, setup or {}
    token = admin_token(args.url, args.out / 'data')
    if not token:
        print('Complete the Jellyfin administrator setup/login, then rerun --configure-only; no credentials were created or reset.')
        return False
    settings = request(args.url, 'TreasureMaps/Management/Settings', token)
    known = {s['service']: s for s in settings['connections']}
    if setup.get('indexer') and not known['treasuremaps']['keyPresent']:
        request(args.url, 'TreasureMaps/Management/Settings', token, 'POST', {'service': 'treasuremaps', **setup['indexer']})
    if setup.get('indexers'):
        existing = request(args.url, 'TreasureMaps/Management/Indexers', token)['items']
        for source in setup['indexers']:
            # Preserve existing credentials and choices on repeated installs.
            if any(s['url'].rstrip('/') == source['url'].rstrip('/') for s in existing):
                continue
            request(args.url, 'TreasureMaps/Management/Indexers', token, 'POST', source)
            existing = request(args.url, 'TreasureMaps/Management/Indexers', token)['items']
    detected = {s['service']: s for s in discover(args, home)}
    for found in detected.values():
        current = known[found['service']]
        # Never replace manually configured remote servers or their credentials.
        if current['keyPresent']:
            continue
        request(args.url, 'TreasureMaps/Management/Settings', token, 'POST', found)
    # Pick a profile/root only when unambiguous; preserve all existing choices.
    settings = request(args.url, 'TreasureMaps/Management/Settings', token)
    for item in settings['connections']:
        if item['service'] not in ('radarr', 'sonarr') or not item['keyPresent'] or item['profile'] > 0 and item['root']:
            continue
        options = request(args.url, 'TreasureMaps/Management/Options/' + item['service'], token)
        local_url = detected.get(item['service'], {}).get('url', '')
        choice = choices.get(item['service']) if item['url'].rstrip('/') == local_url.rstrip('/') else None
        if choice:
            request(args.url, 'TreasureMaps/Management/Settings', token, 'POST',
                    {'service': item['service'], 'url': item['url'], 'profile': item['profile'] or choice['profile'], 'root': item['root'] or choice['root']})
        elif len(options['profiles']) == 1 and len(options['folders']) == 1:
            request(args.url, 'TreasureMaps/Management/Settings', token, 'POST',
                    {'service': item['service'], 'url': item['url'], 'profile': options['profiles'][0]['id'], 'root': options['folders'][0]['path']})
    if setup.get('mediaRoot'):
        route = 'Plugins/' + PLUGIN_ID + '/Configuration'
        config = request(args.url, route, token)
        changed = False
        for key, folder in [('SabnzbdMovieFolder', 'Filme'), ('SabnzbdTvFolder', 'Serien')]:
            if not config.get(key) or config[key] in ('movies', 'tv'):
                config[key] = str(Path(setup['mediaRoot']) / folder)
                changed = True
        if changed:
            request(args.url, route, token, 'POST', config)
    links = request(args.url, 'TreasureMaps/Management/Connections/Apply', token, 'POST', timeout=180)['links']
    for link in links:
        print(link['from'] + ' -> ' + link['to'] + ': ' + link['state'] + ' (' + link['detail'] + ')')
    return all(link['state'] not in ('error', 'missing') for link in links)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--user', default=os.environ.get('SUDO_USER') or os.environ.get('USER'))
    parser.add_argument('--out', type=Path)
    parser.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument('--branch', default='master')
    parser.add_argument('--url', default='http://127.0.0.1:8096')
    parser.add_argument('--web-dist', type=Path)
    parser.add_argument('--bundle', type=Path)
    parser.add_argument('--plan', action='store_true')
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--interrupt-playback', action='store_true', help='Explicitly allow the server restart to interrupt active or paused playback')
    parser.add_argument('--configure-only', action='store_true')
    parser.add_argument('--install-missing', action='store_true')
    parser.add_argument('--setup-config', type=Path, help='Private JSON with mediaRoot, indexer and Usenet credentials for new systems')
    parser.add_argument('--allow-dirty', action='store_true', help='Allow a locally reviewed development bundle')
    for service in ('radarr', 'sonarr', 'sab'):
        parser.add_argument('--' + service + '-config')
    args = parser.parse_args(argv)
    setup = stack_automation.load_setup(args.setup_config)
    import pwd
    account = pwd.getpwnam(args.user)
    home = Path(account.pw_dir)
    args.out = (args.out or home / 'jellyfin12').resolve()
    if args.out in (Path('/'), home, Path('/home'), Path('/var'), Path('/var/lib')) or any(c.isspace() for c in str(args.out)):
        raise ValueError('Use a dedicated installation directory without whitespace')
    services = discover(args, home)
    print(json.dumps({'install': str(args.out), 'branch': args.branch, 'detected': [{'service': s['service'], 'configured': bool(s['apiKey'])} for s in services]}, indent=2))
    if not args.apply or args.plan:
        print('Read-only plan. Apply builds/stages a release, backs up state, verifies health, discovers keys and reconciles API links.')
        try:
            token = admin_token(args.url, args.out / 'data')
            if token:
                links = request(args.url, 'TreasureMaps/Management/Connections', token)['links']
                print(json.dumps(links, ensure_ascii=False, indent=2))
        except (RuntimeError, urllib.error.URLError):
            print('Management capability will be checked after the plugin update.')
        return 0
    if os.geteuid() != 0:
        raise RuntimeError('Run with sudo --user ' + account.pw_name)
    os.umask(0o077)
    # Serialize installers, including configuration-only runs.
    import fcntl
    with open('/run/lock/jellyfin-media-stack.lock', 'w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if args.install_missing:
            install_missing(args, account, services)
        if not args.configure_only:
            if not args.bundle:
                if run('git', '-C', args.repo, 'status', '--porcelain', capture=True).strip():
                    raise RuntimeError('Checkout has local changes; commit or provide a release bundle')
                run('runuser', '-u', account.pw_name, '--', 'git', '-C', args.repo, 'fetch', 'origin', args.branch)
                run('runuser', '-u', account.pw_name, '--', 'git', '-C', args.repo, 'switch', args.branch)
                run('runuser', '-u', account.pw_name, '--', 'git', '-C', args.repo, 'merge', '--ff-only', 'origin/' + args.branch)
                dotnet = home / '.dotnet/dotnet'
                if not dotnet.exists():
                    run('apt-get', 'update')
                    run('apt-get', 'install', '-y', 'curl', 'ca-certificates', 'libfontconfig1', 'ffmpeg')
                    with tempfile.TemporaryDirectory() as tmp:
                        os.chmod(tmp, 0o755)
                        setup = Path(tmp) / 'dotnet-install.sh'
                        urllib.request.urlretrieve('https://dot.net/v1/dotnet-install.sh', setup)
                        os.chmod(setup, 0o755)
                        run('runuser', '-u', account.pw_name, '--', 'bash', setup, '--channel', '10.0', '--install-dir', home / '.dotnet')
                build = home / '.cache/jellyfin-stack-build'
                build.mkdir(parents=True, exist_ok=True)
                chown_tree(build, account)
                prefix = ('runuser', '-u', account.pw_name, '--', 'env', 'DOTNET_ROOT=' + str(dotnet.parent), 'DOTNET_CLI_TELEMETRY_OPTOUT=1')
                run(*prefix, dotnet, 'publish', args.repo / 'Jellyfin.Server', '-c', 'Release', '-r', 'linux-arm64', '--self-contained', 'false', '-o', build / 'server')
                run(*prefix, dotnet, 'build', args.repo / 'plugins/Jellyfin.Plugin.TreasureMaps', '-c', 'Release')
                args.bundle = build / 'release.zip'
                run(*prefix, 'python3', args.repo / 'scripts/pi/package-release.py', '--repo', args.repo, '--server', build / 'server', '--output', args.bundle)
            deploy(args, account, args.bundle)
        choices = stack_automation.configure(discover(args, home), setup, account)
        okay = reconcile(args, home, choices, setup)
        print('Medienzentrale: ' + args.url.rstrip('/') + '/web/#/configurationpage?name=TreasureMapsManagement')
        return 0 if okay else 2


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as error:
        # Network exceptions may contain credential-bearing URLs; print only a safe class.
        print(str(error) if isinstance(error, (RuntimeError, ValueError)) else type(error).__name__, file=sys.stderr)
        sys.exit(1)
