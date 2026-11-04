"""Generated source IDs and packages; no Plex account or network access."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from test_rust_import_runner import package

SPEC=importlib.util.spec_from_file_location('priority',Path(__file__).with_name('rust-migration-priority.py'))
priority=importlib.util.module_from_spec(SPEC);SPEC.loader.exec_module(priority)

class PriorityTests(unittest.TestCase):
    def test_complete_snapshot_ranks_exact_source_ids_and_freezes_manifest_digests(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);ids=[];digests=[]
            for index in range(3):
                path=root/f'batch-{index+1:04d}';digest=package(path,lambda value:value.update(batchIndex=index,batchCount=3))
                sealed=priority.runner.Package(path,digest)
                try:ids.append(sealed.manifest['selectedLinks'][0]['legacyDavItemId']);digests.append(digest)
                finally:sealed.close()
            snapshot={'updated_at':1000,'stale':False,'refreshing':False,'error':None,'predictions':[
                {'item':ids[0],'source':'library','warning':None},
                {'item':ids[1],'source':'hub','warning':None},
                {'item':ids[2],'source':'next_episode','warning':None}]}
            plan=priority.build(root,'b'*64,3,snapshot,'a'*40,2000)
            self.assertEqual(plan['order'],[2,1,0]);self.assertEqual([row['manifestDigest'] for row in plan['batches']],digests)
            # Warning/unknown rows cannot borrow a viewer's priority.
            snapshot['predictions'][2]['warning']='viewer unresolved'
            self.assertEqual(priority.build(root,'b'*64,3,snapshot,'a'*40,2000)['order'],[1,0,2])

    def test_stale_partial_future_or_outage_snapshot_cannot_replace_schedule(self):
        base={'updated_at':1000,'stale':False,'refreshing':False,'error':None,'predictions':[]}
        for change in ({'stale':True},{'refreshing':True},{'error':'fixture outage'},{'updated_at':999999999}):
            with self.subTest(change=change),self.assertRaisesRegex(ValueError,'complete current'):
                priority.build('/unused','b'*64,1,{**base,**change},'a'*40,2000)

if __name__=='__main__':unittest.main()
