import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import stack_storage as storage


class StorageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.media = self.root / 'media'
        self.media.mkdir()
        self.cache = self.media / '.jellyfin-transcodes'
        self.setup = {'mediaRoot': str(self.media)}
        self.mount = {'target': str(self.media), 'source': '/dev/nvme0n1', 'fstype': 'btrfs'}
        self.encoding = {'TranscodingTempPath': None, 'LiveTvEncoderPreset': 'fast',
                         'HardwareAccelerationType': 'vaapi', 'EnableThrottling': True}
        self.sessions = []
        self.writes = []
        self.dropin = self.root / 'units/jellyfin12.service.d/60-jellyfin-stack-storage.conf'

    def api(self, route, method='GET', body=None):
        if route == 'Sessions':
            return copy.deepcopy(self.sessions)
        self.assertEqual('System/Configuration/encoding', route)
        if method == 'POST':
            self.writes.append(copy.deepcopy(body))
            self.encoding = copy.deepcopy(body)
        return copy.deepcopy(self.encoding)

    def configure(self, api=None):
        return storage.configure(api or self.api, self.setup, self.root / 'install', unit_root=self.root / 'units')

    def prepare(self, path, service, fstype):
        path.mkdir(exist_ok=True)

    def test_fresh_ssd_setup_preserves_encoding_options_and_second_run_does_not_write(self):
        original = copy.deepcopy(self.encoding)
        with patch.object(storage, 'ssd_mount', return_value=self.mount), \
                patch.object(storage, 'prepare_directory', side_effect=self.prepare), \
                patch.object(storage, 'command') as commands:
            self.assertTrue(self.configure())
            first_dropin = self.dropin.read_bytes()
            self.assertTrue(self.configure())
        self.assertEqual([{**original, 'TranscodingTempPath': str(self.cache)}], self.writes)
        self.assertEqual(first_dropin, self.dropin.read_bytes())
        self.assertIn('RequiresMountsFor=', self.dropin.read_text())
        self.assertIn('AssertPathIsMountPoint=', self.dropin.read_text())
        commands.assert_called_once_with('systemctl', 'daemon-reload')
        backups = list((self.root / 'install/config-backups').glob('*.json'))
        self.assertEqual(1, len(backups))
        self.assertEqual(original, json.loads(backups[0].read_text())['encoding'])

    def test_existing_custom_path_wins_even_over_explicit_setup_without_mutation(self):
        self.encoding['TranscodingTempPath'] = '/custom/existing/cache'
        self.setup['transcodePath'] = '/new/request/.jellyfin-transcodes'
        with patch.object(storage, 'command') as commands, patch.object(storage, 'prepare_directory') as prepare:
            self.assertTrue(self.configure())
        commands.assert_not_called()
        prepare.assert_not_called()
        self.assertEqual([], self.writes)
        self.assertFalse(self.dropin.exists())

    def test_playing_and_paused_playback_defer_without_touching_directory_or_settings(self):
        for paused in (False, True):
            self.sessions = [{'NowPlayingItem': {'Id': 'episode'}, 'PlayState': {'IsPaused': paused}}]
            with self.subTest(paused=paused), patch.object(storage, 'ssd_mount', return_value=self.mount), \
                    patch.object(storage, 'prepare_directory') as prepare:
                self.assertFalse(self.configure())
                prepare.assert_not_called()
        self.assertFalse(self.cache.exists())
        self.assertEqual([], self.writes)

    def test_playback_started_during_preparation_defers(self):
        def prepare(*args):
            self.prepare(*args)
            self.sessions = [{'NowPlayingItem': {'Id': 'new-viewer'}}]
        with patch.object(storage, 'ssd_mount', return_value=self.mount), patch.object(storage, 'prepare_directory', side_effect=prepare):
            self.assertFalse(self.configure())
        self.assertFalse(self.dropin.exists())
        self.assertEqual([], self.writes)

    def test_concurrent_encoding_edit_is_not_overwritten(self):
        def prepare(*args):
            self.prepare(*args)
            self.encoding['LiveTvEncoderPreset'] = 'custom'
        with patch.object(storage, 'ssd_mount', return_value=self.mount), patch.object(storage, 'prepare_directory', side_effect=prepare):
            self.assertFalse(self.configure())
        self.assertEqual('custom', self.encoding['LiveTvEncoderPreset'])
        self.assertEqual([], self.writes)

    def test_nonempty_unmarked_directory_and_media_root_as_cache_are_rejected(self):
        self.cache.mkdir()
        (self.cache / 'film.mkv').write_bytes(b'keep')
        with patch.object(storage, 'ssd_mount', return_value=self.mount):
            with self.assertRaisesRegex(ValueError, 'nonempty'):
                storage.plan(self.setup, self.encoding)
            with self.assertRaisesRegex(ValueError, 'dedicated'):
                storage.plan({'transcodePath': str(self.media)}, self.encoding)
            (self.cache / storage.MARKER).touch()
            self.assertEqual('ready', storage.plan(self.setup, self.encoding)['state'])
        self.assertEqual(b'keep', (self.cache / 'film.mkv').read_bytes())

    def test_plan_is_read_only_and_unverified_storage_never_redirects_to_sd(self):
        with patch.object(storage, 'ssd_mount', return_value=self.mount):
            self.assertEqual('ready', storage.plan(self.setup, self.encoding)['state'])
        self.assertFalse(self.cache.exists())
        with patch.object(storage, 'ssd_mount', return_value=None):
            self.assertEqual('skipped', storage.plan(self.setup, self.encoding)['state'])
            with self.assertRaisesRegex(ValueError, 'verified mounted'):
                storage.plan({'transcodePath': str(self.cache)}, self.encoding)
        self.assertEqual('skipped', storage.plan({}, self.encoding)['state'])

    def test_failed_or_timed_out_save_restores_path_and_guard_without_losing_other_settings(self):
        for prior in (None, storage.MANAGED + '[Unit]\nRequiresMountsFor=/old/cache\n'):
            with self.subTest(previous_dropin=prior):
                if prior:
                    self.dropin.parent.mkdir(parents=True, exist_ok=True)
                    self.dropin.write_text(prior)
                original = copy.deepcopy(self.encoding)
                failed = False
                def timed_out(route, method='GET', body=None):
                    nonlocal failed
                    result = self.api(route, method, body)
                    if method == 'POST' and not failed:
                        failed = True
                        self.encoding['ConcurrentSetting'] = 'keep'
                        raise RuntimeError('timed-out save')
                    return result
                with patch.object(storage, 'ssd_mount', return_value=self.mount), \
                        patch.object(storage, 'prepare_directory', side_effect=self.prepare), patch.object(storage, 'command'):
                    with self.assertRaisesRegex(RuntimeError, 'timed-out save'):
                        self.configure(timed_out)
                self.assertEqual(original['TranscodingTempPath'], self.encoding['TranscodingTempPath'])
                self.assertEqual('keep', self.encoding['ConcurrentSetting'])
                self.assertEqual(prior, self.dropin.read_text() if self.dropin.exists() else None)

    def test_failed_daemon_reload_prevents_api_mutation_and_restores_dropin(self):
        with patch.object(storage, 'ssd_mount', return_value=self.mount), \
                patch.object(storage, 'prepare_directory', side_effect=self.prepare), \
                patch.object(storage, 'command', side_effect=[RuntimeError('reload failed'), '']):
            with self.assertRaisesRegex(RuntimeError, 'reload failed'):
                self.configure()
        self.assertEqual([], self.writes)
        self.assertFalse(self.dropin.exists())

    def test_unverified_rollback_retains_guard_and_reports_backup(self):
        posted = False
        def unavailable(route, method='GET', body=None):
            nonlocal posted
            if posted:
                raise RuntimeError('offline')
            result = self.api(route, method, body)
            if method == 'POST':
                posted = True
            return result
        with patch.object(storage, 'ssd_mount', return_value=self.mount), \
                patch.object(storage, 'prepare_directory', side_effect=self.prepare), patch.object(storage, 'command'):
            with self.assertRaisesRegex(RuntimeError, 'mount guard retained. Backup:'):
                self.configure(unavailable)
        self.assertTrue(self.dropin.exists())

    def test_admin_dropin_is_never_overwritten(self):
        self.dropin.parent.mkdir(parents=True)
        self.dropin.write_text('[Unit]\nRequiresMountsFor=/admin/cache\n')
        with patch.object(storage, 'ssd_mount', return_value=self.mount), patch.object(storage, 'prepare_directory', side_effect=self.prepare):
            with self.assertRaisesRegex(ValueError, 'not installer-managed'):
                self.configure()
        self.assertEqual([], self.writes)
        self.assertIn('/admin/cache', self.dropin.read_text())

    def test_storage_classification_rejects_sd_emmc_hdd_network_and_unresolved_disks(self):
        cases = [
            ('/dev/nvme0n1', 'disk', False, 'nvme', True),
            ('/dev/sda', 'disk', False, 'usb', True),
            ('/dev/mmcblk0', 'disk', False, None, False),
            ('/dev/sda', 'disk', True, 'sata', False),
            ('/dev/sda', 'disk', None, 'usb', False),
            ('/dev/loop0', 'loop', False, None, False),
            ('/dev/dm-0', 'crypt', False, None, False),
        ]
        for name, kind, rota, transport, accepted in cases:
            blocks = {'blockdevices': [{'name': name, 'type': kind, 'rota': rota, 'tran': transport}]}
            with self.subTest(device=name, rotational=rota), patch.object(storage, 'command', side_effect=[
                    json.dumps({'filesystems': [self.mount]}), json.dumps(blocks)]):
                self.assertEqual(accepted, bool(storage.ssd_mount(self.media)))
        for mount in ({**self.mount, 'target': '/'}, {**self.mount, 'source': 'nas:/media', 'fstype': 'nfs4'}):
            with patch.object(storage, 'command', return_value=json.dumps({'filesystems': [mount]})):
                self.assertIsNone(storage.ssd_mount(self.media))

    def test_partition_requires_all_resolved_parent_disks_to_be_ssds(self):
        parent = {'name': '/dev/sda', 'type': 'disk', 'rota': False, 'tran': 'usb'}
        blocks = {'blockdevices': [{'name': '/dev/sda1', 'type': 'part', 'children': [parent]}]}
        with patch.object(storage, 'command', side_effect=[json.dumps({'filesystems': [self.mount]}), json.dumps(blocks)]):
            self.assertIsNotNone(storage.ssd_mount(self.media))
        blocks['blockdevices'][0]['children'].append({**parent, 'name': '/dev/sdb', 'rota': True})
        with patch.object(storage, 'command', side_effect=[json.dumps({'filesystems': [self.mount]}), json.dumps(blocks)]):
            self.assertIsNone(storage.ssd_mount(self.media))

    def test_mount_values_escape_systemd_specifiers_and_reject_line_injection(self):
        self.assertEqual('"/srv/media %%n/with \\"quotes\\""', storage.unit_quote('/srv/media %n/with "quotes"'))
        with self.assertRaises(ValueError):
            storage.unit_quote('/srv/media\nExecStart=oops')


if __name__ == '__main__':
    unittest.main()
