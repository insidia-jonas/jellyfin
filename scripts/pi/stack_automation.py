"""Configure local Arr/SAB installations without replacing existing provider choices."""
import json
import os
from pathlib import Path
import urllib.error
import urllib.parse
import urllib.request


def load_setup(path):
    if not path:
        return {}
    path = Path(path)
    if path.stat().st_mode & 0o077:
        raise ValueError('Setup credentials must be private: chmod 600 ' + str(path))
    setup = json.loads(path.read_text())
    root = Path(setup.get('mediaRoot', ''))
    if not root.is_absolute() or root in (Path('/'), Path('/home'), Path('/var'), Path('/srv')) or not root.is_dir():
        raise ValueError('mediaRoot must be an existing, dedicated media directory (mount the disk first)')
    return setup


def api(service, route, body=None, method=None):
    headers = {'Content-Type': 'application/json'}
    if service['service'] == 'sabnzbd':
        url = service['url'].rstrip('/') + '/api?' + urllib.parse.urlencode({'apikey': service['apiKey'], 'output': 'json', **route}, doseq=True)
    else:
        url = service['url'].rstrip('/') + '/api/v3/' + route
        headers['X-Api-Key'] = service['apiKey']
    req = urllib.request.Request(url, headers=headers, data=json.dumps(body).encode() if body is not None else None,
                                 method=method or ('POST' if body is not None else 'GET'))
    try:
        with urllib.request.urlopen(req, timeout=40) as response:
            raw = response.read()
            result = json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        raise RuntimeError(service['service'] + ': HTTP ' + str(error.code)) from None
    if isinstance(result, dict) and (result.get('status') is False or result.get('error')):
        raise RuntimeError(service['service'] + ': configuration rejected; check the service UI')
    return result


def ensure_directory(path, account):
    if not path.exists():
        missing = [path]
        while not missing[-1].parent.exists():
            missing.append(missing[-1].parent)
        for created in reversed(missing):
            created.mkdir(mode=0o2775)
            os.chown(created, account.pw_uid, account.pw_gid)
            os.chmod(created, 0o2775)
    # Never recursively change an existing media library's ownership.
    if not path.is_dir():
        raise ValueError('Media destination is not a directory')


def configure(services, setup, account):
    """Returns selected root/profile defaults for Jellyfin; existing selections win there."""
    by_name = {s['service']: s for s in services}
    choices = {}
    root = Path(setup['mediaRoot']) if setup else None
    paths = {}
    if root:
        for name, rel in {'radarr': 'Filme', 'sonarr': 'Serien', 'complete': 'Downloads/complete', 'incomplete': 'Downloads/incomplete'}.items():
            paths[name] = root / rel
            ensure_directory(paths[name], account)

    sab = by_name.get('sabnzbd')
    if not sab:
        raise ValueError('SABnzbd not found; use --install-missing or --sab-config')
    if sab:
        servers = api(sab, {'mode': 'get_config', 'section': 'servers'})['config']['servers']
        if not any(s.get('enable', 1) in (True, 1, '1') for s in servers):
            provider = setup.get('usenet')
            if not provider or not all(provider.get(k) for k in ('host', 'username', 'password')):
                raise ValueError('SABnzbd has no enabled Usenet server. Supply usenet host/username/password in --setup-config.')
            fields = {k: provider[k] for k in ('host', 'username', 'password')}
            fields.update(port=int(provider.get('port', 563)), ssl=int(provider.get('ssl', True)), connections=int(provider.get('connections', 8)), enable=1)
            api(sab, {'mode': 'set_config', 'section': 'servers', 'name': provider['host'], **fields})
            verified = api(sab, {'mode': 'get_config', 'section': 'servers'})['config']['servers']
            if not any(s.get('host') == provider['host'] and s.get('enable') in (True, 1, '1') for s in verified):
                raise RuntimeError('SABnzbd Usenet configuration did not persist')
            print('SABnzbd: Usenet server configured; credentials remain private.')
        if root:
            misc = api(sab, {'mode': 'get_config', 'section': 'misc'})['config']['misc']
            # Only replace empty/default paths, not a working existing installation.
            for key, target in [('download_dir', paths['incomplete']), ('complete_dir', paths['complete'])]:
                if not misc.get(key) or misc[key] in ('Downloads/incomplete', 'Downloads/complete'):
                    api(sab, {'mode': 'set_config', 'section': 'misc', 'keyword': key, 'value': str(target)})
            categories = api(sab, {'mode': 'get_config', 'section': 'categories'})['config']['categories']
            for name, folder in [('radarr', paths['complete'] / 'radarr'), ('sonarr', paths['complete'] / 'sonarr'), ('movies', paths['radarr']), ('tv', paths['sonarr'])]:
                if not any(c['name'] == name for c in categories):
                    ensure_directory(folder, account)
                    api(sab, {'mode': 'set_config', 'section': 'categories', 'name': name, 'dir': str(folder), 'pp': 3, 'script': 'None'})
            # SAB sorting is exclusively for direct TM categories; Arr owns its staging categories.
            sorters = api(sab, {'mode': 'get_config', 'section': 'sorters'})['config']['sorters']
            for name, category, kind, pattern in [('Treasure Maps TV', 'tv', 1, '%sn/Season %0s/%fn.%ext'), ('Treasure Maps Movies', 'movies', 3, '%title (%y)/%fn.%ext')]:
                if not any(category in s.get('sort_cats', []) for s in sorters):
                    api(sab, {'mode': 'set_config', 'section': 'sorters', 'name': name, 'sort_cats': category, 'sort_type': kind, 'sort_string': pattern, 'is_active': 1})
                    verified = api(sab, {'mode': 'get_config', 'section': 'sorters'})['config']['sorters']
                    if not any(s.get('name') == name and s.get('is_active') == 1 and category in s.get('sort_cats', []) for s in verified):
                        raise RuntimeError('SABnzbd direct-download sorting did not persist')

    for name in ('radarr', 'sonarr'):
        if name not in by_name:
            raise ValueError(name + ' not found; run with --install-missing or supply its config path')
        service = by_name[name]
        roots = api(service, 'rootfolder')
        if not roots and root:
            created = api(service, 'rootfolder', {'path': str(paths[name])})
            roots = [created]
        if not roots:
            raise ValueError(name + ': no library root; supply mediaRoot in --setup-config')
        profiles = api(service, 'qualityprofile')
        wanted = setup.get('qualityProfile', 'HD-1080p')
        profile = next((p for p in profiles if p['name'] == wanted), None)
        if not profile and len(profiles) == 1:
            profile = profiles[0]
        if len(roots) == 1 and profile:
            choices[name] = {'root': roots[0]['path'], 'profile': profile['id']}
        for resource, field, desired in [('config/downloadclient', 'enableCompletedDownloadHandling', True), ('config/indexer', 'rssSyncInterval', 15)]:
            current = api(service, resource)
            needs_change = not current.get(field) if field != 'rssSyncInterval' else int(current.get(field, 0)) == 0
            if needs_change:
                current[field] = desired
                api(service, resource + '/' + str(current['id']), current, 'PUT')
                if api(service, resource).get(field) != desired:
                    raise RuntimeError(name + ': automation setting did not persist')
        print(name + ': completed download handling and RSS enabled; existing library/profile choices preserved.')
    return choices
