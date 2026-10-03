import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location('stack', Path(__file__).with_name('manage-stack.py'))
stack = importlib.util.module_from_spec(spec)
spec.loader.exec_module(stack)


class BundleTests(unittest.TestCase):
    def test_health_before_api_readiness_does_not_cause_premature_rollback(self):
        with patch.object(stack, 'request', side_effect=[RuntimeError('API request failed: HTTP 503'), {'version': '1.0.4.0'}]) as api, patch.object(stack.time, 'sleep'):
            stack.wait_management('http://localhost', 'private', '1.0.4.0')
            self.assertEqual(2, api.call_count)

    def test_sab_configobj_supports_global_keys_and_nested_sections(self):
        self.assertEqual({'port': '8081', 'api_key': 'private'}, stack.sab_misc('__version__ = 19\n[misc]\nport = 8081\napi_key = "private"\n[servers]\n[[news]]\nport = 563'))

    def bundle(self, root, extra=None, corrupt=False):
        files = {'server/jellyfin.dll': b'server', 'plugin/Jellyfin.Plugin.TreasureMaps.dll': b'plugin'}
        files.update(extra or {})
        manifest = {'schema': 1, 'runtime': 'linux-arm64', 'files': {n: hashlib.sha256(b).hexdigest() for n, b in files.items()}}
        path = root / 'release.zip'
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr('release.json', json.dumps(manifest))
            for name, data in files.items():
                archive.writestr(name, b'corrupt' if corrupt else data)
        return path

    def test_corrupt_bundle_rejected_before_deployment(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            with self.assertRaisesRegex(ValueError, 'checksum'):
                stack.verify_bundle(self.bundle(root, corrupt=True), root / 'stage')

    def test_archive_traversal_never_writes_outside_stage(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            with self.assertRaisesRegex(ValueError, 'Unsafe'):
                stack.verify_bundle(self.bundle(root, {'server/../../escape': b'x'}), root / 'stage')
            self.assertFalse((root / 'escape').exists())

    def test_same_artifacts_are_a_noop_but_changed_plugin_is_detected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            manifest = stack.verify_bundle(self.bundle(root), root / 'stage')
            (root / 'stage/server').rename(root / 'server')
            target = root / 'data/plugins/Treasure-Maps'
            target.parent.mkdir(parents=True)
            (root / 'stage/plugin').rename(target)
            self.assertTrue(stack.installed_matches(root, manifest))
            (target / 'Jellyfin.Plugin.TreasureMaps.dll').write_bytes(b'old')
            self.assertFalse(stack.installed_matches(root, manifest))

    def test_existing_keys_profiles_and_remote_services_are_preserved(self):
        from types import SimpleNamespace
        settings = {'connections': [{'service': 'radarr', 'keyPresent': True, 'url': 'https://remote.example', 'profile': 8, 'root': '/custom'}]}
        calls = []

        def api(base, route, key, method='GET', body=None, **kwargs):
            calls.append((route, method, body))
            return {'links': []} if route.endswith('/Apply') else settings

        with patch.object(stack, 'admin_token', return_value='private'), patch.object(stack, 'request', side_effect=api), patch.object(stack, 'discover', return_value=[{'service': 'radarr', 'url': 'http://localhost', 'apiKey': 'different'}]):
            self.assertTrue(stack.reconcile(SimpleNamespace(url='http://localhost', out=Path('/unused')), Path('/unused')))
        self.assertFalse(any(method == 'POST' and route.endswith('Settings') for route, method, body in calls))


if __name__ == '__main__':
    unittest.main()
