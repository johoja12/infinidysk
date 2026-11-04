"""Generated package fixtures and simulated API responses; no production calls."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
import uuid
from test_rust_import_runner import package

SPEC=importlib.util.spec_from_file_location('discovery',Path(__file__).with_name('rust-migration-discover.py'))
discovery=importlib.util.module_from_spec(SPEC);SPEC.loader.exec_module(discovery)

class DiscoveryTests(unittest.TestCase):
    def exercise(self,ambiguous=False,unavailable=False,changed=False,bound=100):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary)/'package';digest=package(root)
            sealed=discovery.runner.Package(root,digest)
            try:
                first=str(uuid.uuid4());second=str(uuid.uuid4());directory=str(uuid.uuid4());job=str(uuid.uuid4())
                intent=discovery.runner.intent(digest,'fixture-release')
                payload=discovery.runner.xml_digest(sealed.payload(sealed.manifest['releases'][0]))
                class Client:
                    lists=0
                    def call(self,path):
                        if '/submissions/' in path:return {'intent':intent,'state':'completed','payload_sha256':payload,'job_id':job,'download_dir':directory}
                        if path=='/api/v1/files/'+directory:return {'entry':{'kind':'directory','item_id':directory,'path':'/content/fixture'}}
                        self.lists+=1
                        rows=[{'kind':'file','item_id':key,'path':'/content/fixture/'+name,'size':4} for key,name in ((first,'first.bin'),(second,'second.bin'))]
                        if changed and self.lists>1:rows=rows[:1]
                        return {'entries':rows,'total':len(rows)}
                    def json(self,path,request):
                        candidate=request['current_item']
                        if candidate==first:
                            if unavailable:raise discovery.runner.HttpFailure(503)
                            if not ambiguous:raise discovery.runner.HttpFailure(409)
                        return {**{k:v for k,v in request['source'].items() if k!='source_config'},'version':1,
                                'current_item':candidate,'submission_intent':intent,'job_id':job,'download_dir':directory,
                                'ids_path':discovery.canary.links.ids_path(candidate),'source_revision':'a'*64,'record_digest':'b'*64,'payload_validated':False}
                result=discovery.discover(sealed,Client(),Path('/offline-fixture'),bound)
                self.assertEqual(result,[{'legacyItem':sealed.manifest['selectedLinks'][0]['legacyDavItemId'],
                                         'currentItem':second,'submissionIntent':intent,'sourceReleaseId':'fixture-release'}])
            finally:
                sealed.close()

    def test_exact_proof_decides_candidates_independently_of_file_names(self):self.exercise()
    def test_ambiguous_proofs_require_review(self):
        with self.assertRaisesRegex(ValueError,'ambiguous'):self.exercise(ambiguous=True)
    def test_unavailable_source_does_not_become_an_ignored_mismatch(self):
        with self.assertRaises(discovery.runner.HttpFailure):self.exercise(unavailable=True)
    def test_changed_or_unbounded_listing_is_not_accepted(self):
        with self.assertRaisesRegex(ValueError,'changed'):self.exercise(changed=True)
        with self.assertRaisesRegex(ValueError,'candidate bound'):self.exercise(bound=1)

if __name__=='__main__':unittest.main()
