import json
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from plex_hub_priority import (build_schedule, collect_files, load_schedule,
    local_relative, next_batch, paged, plex_get, tier, verify_selected_manifest)


class PlexHubPriorityTests(unittest.TestCase):
    def test_tiers_and_exact_path_mapping(self):
        self.assertEqual(tier({'hubIdentifier': 'home.ondeck'}), 0)
        self.assertEqual(tier({'title': 'Trending TV Right Now'}), 1)
        self.assertEqual(tier({'title': 'Recently Added Movies'}), 2)
        maps = [{'PlexPath': '/data', 'LocalPath': '/mnt/plex'},
                {'PlexPath': '/data/special', 'LocalPath': '/mnt/special'}]
        self.assertEqual(local_relative('/data/TV/a.mkv', maps, '/mnt/plex'), 'TV/a.mkv')
        self.assertIsNone(local_relative('/data/special/a.mkv', maps, '/mnt/plex'))
        self.assertIsNone(local_relative('/mnt/plex-other/a.mkv', [], '/mnt/plex'))
        self.assertIsNone(local_relative('/mnt/plex/../secret', [], '/mnt/plex'))

    def test_current_batch_wins_and_sparse_order_resumes(self):
        schedule = {'order': [4, 2, 0, 3, 1]}
        batches = [{'batchIndex': 0, 'status': 'acknowledged'},
                   {'batchIndex': 1, 'status': 'running'}]
        self.assertEqual(next_batch(batches, 5, schedule), 1)
        batches[-1]['status'] = 'acknowledged'
        self.assertEqual(next_batch(batches, 5, schedule), 4)
        batches.append({'batchIndex': 4, 'status': 'acknowledged'})
        self.assertEqual(next_batch(batches, 5, schedule), 2)
        with self.assertRaises(ValueError):
            next_batch(batches, 5)
        batches += [{'batchIndex': 2, 'status': 'acknowledged'},
                    {'batchIndex': 3, 'status': 'acknowledged'}]
        self.assertIsNone(next_batch(batches, 5, schedule))
        self.assertEqual(next_batch([], 5, schedule), 0)

    def test_multiple_active_or_duplicate_registration_stops(self):
        for batches in ([{'batchIndex': 1}, {'batchIndex': 2}],
                        [{'batchIndex': 0}, {'batchIndex': 0}],
                        [{'batchIndex': -1}], [{'batchIndex': 5}]):
            with self.assertRaises(ValueError):
                next_batch(batches, 5)

    def test_schedule_ranking_identity_and_manifest_changes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for index, links in enumerate((['A', 'B'], ['C'], ['D', 'E'], ['Z'])):
                folder = root / f'batch-{index + 1:04d}'
                folder.mkdir()
                (folder / 'manifest.json').write_text(json.dumps({
                    'masterManifestDigest': 'master', 'batchIndex': index, 'batchCount': 4,
                    'selectedLinks': [{'libraryRelativePath': file} for file in links]}))
            plan = build_schedule(root, 'master', 4,
                {'A': {'tier': 1}, 'B': {'tier': 1}, 'C': {'tier': 0},
                 'D': {'tier': 0}, 'E': {'tier': 0}}, [])
            self.assertEqual(plan['order'], [2, 1, 0, 3])
            path = root / 'plan.json'
            path.write_text(json.dumps(plan))
            self.assertEqual(load_schedule(path, 'master', 4)['order'], plan['order'])
            with self.assertRaises(ValueError):
                load_schedule(path, 'wrong-master', 4)
            verify_selected_manifest(plan, root, 2)
            (root / 'batch-0003' / 'manifest.json').write_text('{}')
            with self.assertRaises(ValueError):
                verify_selected_manifest(plan, root, 2)
            plan['order'] = [0, 1, 1, 3]
            path.write_text(json.dumps(plan))
            with self.assertRaises(ValueError):
                load_schedule(path, 'master', 4)
            self.assertIsNone(load_schedule(root / 'absent.json', 'master', 4))

    def test_pagination_uses_actual_items_and_follows_all_pages(self):
        calls = []
        def get(server, key):
            calls.append(key)
            return ET.fromstring('<MediaContainer size="1" totalSize="2"><Video /></MediaContainer>')
        self.assertEqual(len(list(paged({}, '/hub?type=1', get))), 2)
        self.assertIn('X-Plex-Container-Start=1', calls[1])
        self.assertIn('type=1', calls[1])

    def test_collect_expands_unwatched_series_and_uses_episode_priority(self):
        def get(server, key):
            if key.startswith('/hubs?'):
                return ET.fromstring('<MediaContainer><Hub hubIdentifier="home.ondeck" type="episode" key="/deck" title="On Deck"/><Hub type="show" key="/trending" title="Trending TV"/></MediaContainer>')
            if key.startswith('/deck?'):
                return ET.fromstring('<MediaContainer size="1"><Video type="episode" title="Next"><Media><Part file="/mnt/plex/TV/next.mkv"/></Media></Video></MediaContainer>')
            if key.startswith('/trending?'):
                return ET.fromstring('<MediaContainer size="1"><Directory type="show" ratingKey="123"/></MediaContainer>')
            return ET.fromstring('<MediaContainer size="3"><Video viewCount="1"><Media><Part file="/mnt/plex/TV/watched.mkv"/></Media></Video><Video><Media><Part file="/mnt/plex/TV/next.mkv"/></Media></Video><Video><Media><Part file="/mnt/plex/TV/later.mkv"/></Media></Video></MediaContainer>')
        files, hubs = collect_files([{'Id': 'one'}], '/mnt/plex', get)
        self.assertEqual(files['TV/next.mkv']['tier'], 0)
        self.assertEqual(files['TV/later.mkv']['tier'], 1)
        self.assertNotIn('TV/watched.mkv', files)
        self.assertEqual(len(hubs), 2)

    def test_external_hub_keys_rejected_before_credentials_used(self):
        for key in ('http://evil/hub', '//evil/hub', '/hub#fragment', '/hub?X-Plex-Token=bad'):
            with self.assertRaises(ValueError):
                plex_get({}, key)


if __name__ == '__main__':
    unittest.main()
