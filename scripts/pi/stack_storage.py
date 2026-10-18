"""Select a mounted SSD cache for fresh installs; preserve existing encoding choices."""
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import uuid


MANAGED = '# Managed by jellyfin manage-stack.py\n'
MARKER = '.jellyfin-transcode'
MIN_FREE_BYTES = 2 * 1024 ** 3


def command(*args):
    return subprocess.check_output([str(arg) for arg in args], text=True, stderr=subprocess.DEVNULL).strip()


def ssd_mount(directory):
    """Fail closed on SD/eMMC, network, unknown or unmounted storage."""
    try:
        mounts = json.loads(command('findmnt', '--json', '--target', directory,
                                    '--output', 'TARGET,SOURCE,FSTYPE'))['filesystems']
        mount = mounts[0]
        source = mount['source'].split('[', 1)[0]
        if mount['target'] == '/' or mount['fstype'] not in ('ext4', 'ext3', 'xfs', 'btrfs') or not source.startswith('/dev/'):
            return None
        devices = json.loads(command('lsblk', '--json', '--inverse', '--paths',
                                     '--output', 'NAME,TYPE,ROTA,TRAN', source))['blockdevices']
        disks = []
        def visit(device):
            if device['type'] == 'disk':
                disks.append(device)
            elif not device.get('children'):
                # An unresolved device mapper/RAID/loop must not look like an SSD.
                disks.append(device)
            for parent in device.get('children', []):
                visit(parent)
        for device in devices:
            visit(device)
        if not disks or any(d['type'] != 'disk' or d.get('rota') not in (False, 0, '0')
                            or not re.fullmatch(r'/dev/(nvme\d+n\d+|sd[a-z]+)', d['name']) for d in disks):
            return None
        return mount
    except (OSError, subprocess.CalledProcessError, ValueError, KeyError, IndexError, TypeError):
        return None


def plan(setup, encoding):
    current = encoding.get('TranscodingTempPath')
    if current and current.strip():
        return {'state': 'preserved', 'path': current, 'detail': 'Existing transcode path retained.'}
    explicit = setup.get('transcodePath')
    if not explicit and not setup.get('mediaRoot'):
        return {'state': 'skipped', 'detail': 'No mediaRoot/transcodePath supplied; existing default cache retained.'}
    path = Path(explicit) if explicit else Path(setup['mediaRoot']) / '.jellyfin-transcodes'
    if not path.is_absolute() or path.name != '.jellyfin-transcodes' or not path.parent.is_dir() or path.is_symlink():
        raise ValueError('transcodePath must be a dedicated .jellyfin-transcodes directory under an existing mounted SSD directory')
    if any(ord(c) < 32 for c in str(path)):
        raise ValueError('Transcode paths must not contain control characters')
    path = path.resolve()
    mount = ssd_mount(path if path.exists() else path.parent)
    if not mount:
        if explicit:
            raise ValueError('transcodePath is not on a verified mounted local SSD; mount the SSD first')
        return {'state': 'skipped', 'detail': 'mediaRoot is not on a verified mounted local SSD; default cache retained. Supply transcodePath on an SSD.'}
    if path.exists() and (not path.is_dir() or any(path.iterdir()) and not (path / MARKER).is_file()):
        raise ValueError('Refusing a nonempty cache directory without the Jellyfin transcode marker')
    return {'state': 'ready', 'path': str(path), 'mount': mount['target'], 'fstype': mount['fstype'],
            'detail': 'Dedicated SSD cache and service mount dependency will be configured when playback is idle.'}


def unit_quote(value):
    if any(ord(c) < 32 for c in value):
        raise ValueError('Mount paths must not contain control characters')
    return '"' + value.replace('\\', '\\\\').replace('"', '\\"').replace('%', '%%') + '"'


def atomic_write(path, contents, mode=0o644):
    with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', dir=path.parent, delete=False) as stream:
        temporary = Path(stream.name)
        try:
            stream.write(contents)
            stream.flush()
            os.fsync(stream.fileno())
        except BaseException:
            temporary.unlink(missing_ok=True)
            raise
    try:
        os.chmod(temporary, mode)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def prepare_directory(path, service, fstype):
    import pwd
    owner = command('systemctl', 'show', service, '--property=User', '--value') or 'root'
    account = pwd.getpwnam(owner)
    if not path.exists():
        path.mkdir(mode=0o755)
        os.chown(path, account.pw_uid, account.pw_gid)
        os.chmod(path, 0o755)
        if fstype == 'btrfs' and shutil.which('chattr'):
            subprocess.run(['chattr', '+C', str(path)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    # Exercise create/write/delete as the actual unit user, including parent traversal.
    # Existing media and directories are never recursively chowned.
    command('runuser', '-u', account.pw_name, '--', 'python3', '-c',
            'import sys,tempfile; f=tempfile.TemporaryFile(dir=sys.argv[1]); f.write(b"ssd-check"); f.flush(); f.close()', path)
    if shutil.disk_usage(path).free < MIN_FREE_BYTES:
        raise RuntimeError('SSD transcode cache needs at least 2 GiB free')


def configure(api, setup, out, service='jellyfin12', unit_root=Path('/etc/systemd/system')):
    """api(route, method='GET', body=None); returns False when an idle rerun is needed."""
    route = 'System/Configuration/encoding'
    encoding = api(route)
    selected = plan(setup, encoding)
    print('Playback cache: ' + json.dumps(selected, ensure_ascii=False))
    if selected['state'] != 'ready':
        return True
    if any(s.get('NowPlayingItem') for s in api('Sessions')):
        print('Playback cache pending: playback is active or paused; rerun --configure-only when idle. No buffer was changed.')
        return False
    path = Path(selected['path'])
    prepare_directory(path, service, selected['fstype'])
    # Recheck the mount and API state after directory preparation, before mutation.
    if plan(setup, encoding) != selected:
        raise RuntimeError('SSD mount changed during setup; no cache configuration was changed')
    latest = api(route)
    if latest != encoding or any(s.get('NowPlayingItem') for s in api('Sessions')):
        print('Playback cache pending: playback or encoding settings changed during setup; rerun when idle.')
        return False
    dropin = unit_root / (service + '.service.d') / '60-jellyfin-stack-storage.conf'
    if dropin.is_symlink():
        raise ValueError('Refusing to replace a symlinked storage drop-in')
    previous = dropin.read_text(encoding='utf-8') if dropin.exists() else None
    if previous is not None and not previous.startswith(MANAGED):
        raise ValueError('Existing storage drop-in is not installer-managed; keep it and review manually')
    contents = (MANAGED + '[Unit]\nRequiresMountsFor=' + unit_quote(str(path))
                + '\nAssertPathIsMountPoint=' + unit_quote(selected['mount']) + '\n')
    backups = out / 'config-backups'
    backups.mkdir(mode=0o700, parents=True, exist_ok=True)
    backup = backups / ('encoding-before-ssd-' + uuid.uuid4().hex + '.json')
    atomic_write(backup, json.dumps({'encoding': encoding, 'storageDropIn': previous}, indent=2), mode=0o600)
    dropin.parent.mkdir(mode=0o755, parents=True, exist_ok=True)
    changed_dropin = False
    posted = False
    try:
        if contents != previous:
            atomic_write(dropin, contents)
            changed_dropin = True
            command('systemctl', 'daemon-reload')
        posted = True  # A timed-out POST may still have persisted server-side.
        api(route, 'POST', {**encoding, 'TranscodingTempPath': str(path)})
        if api(route).get('TranscodingTempPath') != str(path):
            raise RuntimeError('SSD cache setting did not persist')
    except Exception:
        if posted:
            try:
                current = api(route)
                if current.get('TranscodingTempPath') == str(path):
                    api(route, 'POST', {**current, 'TranscodingTempPath': encoding.get('TranscodingTempPath')})
                    if api(route).get('TranscodingTempPath') != encoding.get('TranscodingTempPath'):
                        raise RuntimeError('Cache rollback did not persist')
            except Exception:
                raise RuntimeError('SSD cache setup failed and API rollback could not be verified; mount guard retained. Backup: ' + str(backup)) from None
        if changed_dropin:
            if previous is None:
                dropin.unlink()
            else:
                atomic_write(dropin, previous)
            command('systemctl', 'daemon-reload')
        raise
    print('SSD playback cache configured; encoding options preserved. No service restart or buffer deletion. Backup: ' + str(backup))
    return True
