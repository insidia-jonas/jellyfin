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
    def test_space_reserves_room_for_staging_backup_and_normal_operation(self):
        from types import SimpleNamespace
        with patch.object(stack.shutil, 'disk_usage', return_value=SimpleNamespace(free=stack.MIN_FREE_BYTES + 99)):
            with self.assertRaisesRegex(RuntimeError, 'Insufficient free space'):
                stack.require_deployment_space(Path.cwd(), 100)
            stack.require_deployment_space(Path.cwd(), 99)

    def test_retention_only_removes_completed_owned_backups_and_keeps_three(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for day in range(1, 7):
                backup = root / 'backups' / f'stack-202610{day:02}-120000'
                backup.mkdir(parents=True)
                (backup / 'completed.json').write_text(json.dumps({
                    'schema': 1, 'installation': str(root.resolve()), 'previousVerified': True}))
            legacy = root / 'backups/stack-20260901-120000'
            legacy.mkdir()
            invalid = root / 'backups/stack-20260902-120000'
            invalid.mkdir()
            (invalid / 'completed.json').write_text('{interrupted')
            foreign = root / 'backups/stack-20260903-120000'
            foreign.mkdir()
            (foreign / 'completed.json').write_text(json.dumps({
                'schema': 1, 'installation': '/another/install', 'previousVerified': True}))
            failed = root / 'backups/stack-20260904-120000'
            failed.mkdir()
            (failed / 'completed.json').write_text(json.dumps({
                'schema': 1, 'installation': str(root.resolve()), 'previousVerified': False}))
            self.assertEqual(['stack-20261003-120000', 'stack-20261002-120000', 'stack-20261001-120000'], stack.prune_deployment_backups(root))
            for path in [legacy, invalid, foreign, failed, root / 'backups/stack-20261004-120000']:
                self.assertTrue(path.exists())
            self.assertEqual([], stack.prune_deployment_backups(root))

    def test_retention_rejects_unsafe_keep_count(self):
        with self.assertRaises(ValueError):
            stack.prune_deployment_backups(Path.cwd(), keep=0)

    def test_retention_does_not_follow_backup_directory_symlink(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / 'install'
            root.mkdir()
            outside = Path(tmp) / 'outside'
            outside.mkdir()
            try:
                (root / 'backups').symlink_to(outside, target_is_directory=True)
            except OSError:
                self.skipTest('Directory symlinks unavailable')
            for day in range(1, 6):
                backup = outside / f'stack-202610{day:02}-120000'
                backup.mkdir()
                (backup / 'completed.json').write_text(json.dumps({
                    'schema': 1, 'installation': str(root.resolve()), 'previousVerified': True}))
            self.assertEqual([], stack.prune_deployment_backups(root))
            self.assertEqual(5, len(list(outside.iterdir())))

    def test_playing_and_paused_sessions_defer_installation_by_default(self):
        for paused in (False, True):
            with self.subTest(paused=paused), patch.object(stack, 'request', return_value=[{'NowPlayingItem': {'Id': 'episode'}, 'PlayState': {'IsPaused': paused}}]):
                with self.assertRaisesRegex(RuntimeError, 'Playback is active'):
                    stack.check_playback_before_deploy('http://localhost', 'private')

    def test_explicit_restart_can_interrupt_playback_without_sending_player_commands(self):
        with patch.object(stack, 'request', return_value=[{'NowPlayingItem': {'Id': 'episode'}}]) as api:
            stack.check_playback_before_deploy('http://localhost', 'private', interrupt_playback=True)
            api.assert_called_once_with('http://localhost', 'Sessions', 'private')

    def test_idle_sessions_do_not_need_an_interruption_override(self):
        with patch.object(stack, 'request', return_value=[{'NowPlayingItem': None}]):
            stack.check_playback_before_deploy('http://localhost', 'private')

    def test_multiple_indexers_preserve_existing_sources_and_do_not_duplicate_on_rerun(self):
        from types import SimpleNamespace
        existing = [{'id': 'legacy', 'url': 'https://one.test', 'apiKey': 'kept'}]
        added = []
        settings = {'connections': []}
        def api(base, route, key, method='GET', body=None, **kwargs):
            if route.endswith('/Indexers'):
                if method == 'POST':
                    added.append(body)
                    existing.append(body)
                    return {'ok': True}
                return {'items': existing}
            return {'links': []} if route.endswith('/Apply') else settings
        setup = {'indexers': [{'name': 'First', 'url': 'https://one.test/', 'apiKey': 'do-not-replace'},
                              {'name': 'Second', 'protocol': 'newznab', 'url': 'https://two.test/api', 'apiKey': 'private'}]}
        with patch.object(stack, 'admin_token', return_value='private'), patch.object(stack, 'request', side_effect=api), patch.object(stack, 'discover', return_value=[]):
            for _ in range(2):
                self.assertTrue(stack.reconcile(SimpleNamespace(url='http://localhost', out=Path('/unused')), Path('/unused'), setup=setup))
        self.assertEqual(1, len(added))
        self.assertEqual('Second', added[0]['name'])
        self.assertEqual('kept', existing[0]['apiKey'])

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
