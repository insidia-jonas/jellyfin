import copy
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('automation', Path(__file__).with_name('stack_automation.py'))
automation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(automation)


class AutomationTests(unittest.TestCase):
    def test_private_setup_rejects_missing_mount_and_public_credentials(self):
        with tempfile.TemporaryDirectory() as tmp:
            config = Path(tmp) / 'setup.json'
            config.write_text(json.dumps({'mediaRoot': str(Path(tmp) / 'not-mounted')}))
            config.chmod(0o600)
            with self.assertRaises(ValueError): automation.load_setup(config)

    def fixture(self, root):
        state = {'servers': [], 'categories': [], 'sorters': [], 'misc': {'complete_dir': 'Downloads/complete', 'download_dir': 'Downloads/incomplete'}}
        resources = {n: {'rootfolder': [], 'qualityprofile': [{'id': 4, 'name': 'HD-1080p'}],
                         'config/downloadclient': {'id': 1, 'enableCompletedDownloadHandling': False, 'unrelated': 23},
                         'config/indexer': {'id': 1, 'rssSyncInterval': 0, 'retention': 1000}} for n in ('sonarr', 'radarr')}
        writes = []

        def api(service, route, body=None, method=None):
            if service['service'] == 'sabnzbd':
                section = route['section']
                if route['mode'] == 'get_config': return {'config': {section: copy.deepcopy(state[section])}}
                writes.append((section, copy.deepcopy(route)))
                if section == 'misc': state[section][route['keyword']] = route['value']
                else:
                    row = {k: v for k, v in route.items() if k not in ('mode', 'section')}
                    if section == 'sorters': row['sort_cats'] = [row['sort_cats']]
                    state[section].append(row)
                return {'status': True}
            items = resources[service['service']]
            if body is None: return copy.deepcopy(items[route])
            writes.append((route, copy.deepcopy(body)))
            if route == 'rootfolder': items[route].append({'id': 1, **body}); return items[route][-1]
            items[route.rsplit('/', 1)[0]] = copy.deepcopy(body)
            return body
        return state, resources, writes, api

    def test_fresh_setup_configures_all_three_services_and_second_run_is_noop(self):
        with tempfile.TemporaryDirectory() as tmp:
            state, resources, writes, api = self.fixture(tmp)
            services = [{'service': n} for n in ('sonarr', 'radarr', 'sabnzbd')]
            setup = {'mediaRoot': tmp, 'usenet': {'host': 'news.example', 'username': 'private', 'password': 'private'}}
            with patch.object(automation, 'api', side_effect=api), patch.object(automation, 'ensure_directory'):
                choices = automation.configure(services, setup, SimpleNamespace())
                count = len(writes)
                automation.configure(services, setup, SimpleNamespace())
            self.assertEqual(count, len(writes))
            self.assertEqual(4, len(state['categories']))
            self.assertEqual(2, len(state['sorters']))
            self.assertEqual({'tv', 'movies'}, {s['sort_cats'][0] for s in state['sorters']})
            self.assertEqual(4, choices['sonarr']['profile'])
            self.assertTrue(resources['sonarr']['config/downloadclient']['enableCompletedDownloadHandling'])
            self.assertEqual(23, resources['sonarr']['config/downloadclient']['unrelated'])
            self.assertEqual(1000, resources['radarr']['config/indexer']['retention'])

    def test_existing_custom_provider_paths_categories_and_rss_survive(self):
        state, resources, writes, api = self.fixture('/unused')
        state['servers'] = [{'host': 'existing', 'enable': 1}]
        state['categories'] = [{'name': 'custom', 'dir': '/custom'}]
        state['misc'] = {'complete_dir': '/old-complete', 'download_dir': '/old-incomplete'}
        for resource in resources.values():
            resource['rootfolder'] = [{'id': 3, 'path': '/custom'}]
            resource['config/downloadclient']['enableCompletedDownloadHandling'] = True
            resource['config/indexer']['rssSyncInterval'] = 30
        with patch.object(automation, 'api', side_effect=api):
            automation.configure([{'service': n} for n in ('sonarr', 'radarr', 'sabnzbd')], {}, SimpleNamespace())
        self.assertEqual([], writes)

    def test_missing_usenet_credentials_fails_instead_of_claiming_ready(self):
        _, _, _, api = self.fixture('/unused')
        with patch.object(automation, 'api', side_effect=api), self.assertRaisesRegex(ValueError, 'Usenet'):
            automation.configure([{'service': 'sabnzbd'}], {}, SimpleNamespace())


if __name__ == '__main__': unittest.main()
