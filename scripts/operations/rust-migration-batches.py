#!/usr/bin/env python3
"""Resume a frozen sealed-package schedule with one import and one canary-validation slot.

Canary validation is a local disposition, never production cutover acknowledgement.
Original libraries and legacy journals remain read-only.
"""
import argparse
import concurrent.futures
import importlib.util
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import time

SPEC=importlib.util.spec_from_file_location('canary',Path(__file__).with_name('rust-migration-canary.py'))
canary=importlib.util.module_from_spec(SPEC);SPEC.loader.exec_module(canary)
runner=canary.runner
IMPORTING={'importing','import_held'}
VALIDATING={'validating','validation_held'}
TERMINAL={'canary_validated','skipped_reviewed'}


def private_bytes(path,limit):
    fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK)
    with os.fdopen(fd,'rb') as stream:
        info=os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_nlink!=1 or info.st_mode&0o077 or info.st_size>limit:
            raise ValueError('Schedule/disposition must be a bounded owner-only regular file')
        return stream.read(limit+1)


class Schedule:
    def __init__(self,path,digest,root):
        raw=private_bytes(path,16_000_000)
        if not runner.lower_digest(digest) or runner.sha(raw)!=digest:
            raise ValueError('Priority schedule differs from retained digest')
        self.digest=digest;self.value=runner.unique_json(raw);self.root=Path(root).resolve(strict=True)
        value=self.value;count=value.get('batchCount');order=value.get('order');rows=value.get('batches')
        if value.get('schemaVersion')!=1 or type(count) is not int or not 1<=count<=10000 \
                or not runner.lower_digest(value.get('masterManifestDigest')) \
                or not isinstance(order,list) or len(order)!=count or any(type(n) is not int for n in order) \
                or sorted(order)!=list(range(count)) or not isinstance(rows,list) or len(rows)!=count:
            raise ValueError('Schedule is not a complete frozen master permutation')
        self.rows={}
        for row in rows:
            index=row.get('batchIndex')
            if type(index) is not int or index not in range(count) or index in self.rows \
                    or not runner.lower_digest(row.get('manifestDigest')):
                raise ValueError('Schedule has duplicate or invalid batch evidence')
            self.rows[index]=row

    def path(self,index):return self.root/f'batch-{index+1:04d}'

    def package(self,index):
        result=runner.Package(self.path(index),self.rows[index]['manifestDigest'])
        value=result.manifest
        if value.get('masterManifestDigest')!=self.value['masterManifestDigest'] \
                or value.get('batchIndex')!=index or value.get('batchCount')!=self.value['batchCount'] \
                or len(value['releases'])>1000 or not 1<=len(value['selectedLinks'])<=1000:
            result.close();raise ValueError('Sealed batch differs from frozen master/slot bound')
        return result


def slots(schedule,states):
    if any(type(index) is not int or index not in schedule.rows or row.get('state') not in IMPORTING|VALIDATING|TERMINAL for index,row in states.items()):
        raise ValueError('Batch journal has an unknown index or state')
    importing=[index for index,row in states.items() if row['state'] in IMPORTING]
    validating=[index for index,row in states.items() if row['state'] in VALIDATING]
    if len(importing)>1 or len(validating)>1:
        raise ValueError('Pipeline has conflicting occupied slots')
    validator=validating[0] if validating else None
    importer=importing[0] if importing else None
    if importer is None:
        importer=0 if not states else next((n for n in schedule.value['order'] if n not in states),None)
    return validator,importer


def state_root(path,scope,protected):
    root=Path(path).absolute();root=root.parent.resolve(strict=True)/root.name
    if any(canary.links.contained(p,root) or canary.links.contained(root,p) for p in protected):
        raise ValueError('Pipeline state/canaries overlap read-only source roots')
    try:root.mkdir(mode=0o700)
    except FileExistsError:pass
    fd=canary.links.root_fd(root)
    try:
        info=os.fstat(fd)
        if info.st_uid!=os.getuid() or info.st_mode&0o077:
            raise ValueError('Pipeline state directory must be private and owned')
        marker={'version':1,'scope':scope,'device':info.st_dev,'inode':info.st_ino}
        try:
            out=os.open('.rust-batch-owner.json',os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600,dir_fd=fd)
            with os.fdopen(out,'wb') as stream:stream.write(runner.canonical(marker));stream.flush();os.fsync(stream.fileno())
            os.fsync(fd)
        except FileExistsError:
            if runner.unique_json(private_bytes(root/'.rust-batch-owner.json',4096))!=marker:
                raise ValueError('Pipeline directory belongs to another immutable plan')
    finally:os.close(fd)
    return root


def child(script,args,timeout=180):
    try:
        result=subprocess.run([sys.executable,str(Path(__file__).with_name(script)),*args],stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=timeout,check=False)
    except subprocess.TimeoutExpired:
        raise RuntimeError('Batch command timed out; durable child checkpoints must be resumed') from None
    if result.returncode or len(result.stdout)>1_000_000:
        raise RuntimeError('Batch command remains held; inspect its private plan/journal')
    return runner.unique_json(result.stdout)


def advance_import(schedule,index,args,root):
    result=child('rust-import-runner.py',['--run','--package',str(schedule.path(index)),
        '--manifest-digest',schedule.rows[index]['manifestDigest'],'--url',args.url,'--server-commit',args.server_commit,
        '--api-key-file',args.api_key_file,'--journal',str(root/f'batch-{index:04d}.acceptance.jsonl'),
        '--category','migration-canary','--import-slots','2'])
    if result.get('manifestDigest')!=schedule.rows[index]['manifestDigest'] or type(result.get('releaseCount')) is not int:
        raise ValueError('Import response differs from the sealed batch')
    return result


def validate_batch(schedule,index,args,root):
    shared=['--package',str(schedule.path(index)),'--manifest-digest',schedule.rows[index]['manifestDigest'],
        '--url',args.url,'--server-commit',args.server_commit,'--api-key-file',args.api_key_file,'--legacy-config',args.legacy_config]
    mapping=root/f'batch-{index:04d}.mapping.json'
    discovered=child('rust-migration-discover.py',shared+['--output',str(mapping),'--max-candidates',str(getattr(args,'max_candidates',100))],timeout=getattr(args,'phase_timeout',600))
    if not runner.lower_digest(discovered.get('mapDigest')):
        raise ValueError('Discovery did not return an exact map receipt')
    package=schedule.package(index)
    try:
        selected=len(package.manifest['selectedLinks'])
        canary.load_mapping(mapping,discovered['mapDigest'],package,args.server_commit)
    finally:package.close()
    plan=root/f'batch-{index:04d}.plan.json';destination=root/f'batch-{index:04d}.canary'
    result=child('rust-migration-canary.py',shared+['--original-library',args.original_library,'--canary-mount',args.canary_mount,
        '--canary-library',str(destination),'--plan-file',str(plan),'--reimport-map',str(mapping),
        '--reimport-map-digest',discovered['mapDigest'],'--max-members','1000','--apply'],timeout=getattr(args,'phase_timeout',600))
    saved=canary.load_plan(plan)
    if result.get('planDigest')!=saved['planDigest'] or result.get('sampledMembers')!=selected \
            or result.get('originalLibraryChanged') is not False or result.get('productionAcknowledged') is not False:
        raise ValueError('Canary command did not prove this complete selection')
    return {'planDigest':saved['planDigest'],'mapDigest':discovered['mapDigest'],'sampledMembers':selected,
            'originalLibraryChanged':False,'productionAcknowledged':False}


def tick(schedule,args,root,journal):
    states={int(key.removeprefix('batch:')):row for key,row in journal.latest.items() if key.startswith('batch:')}
    validator,importer=slots(schedule,states)
    if importer is not None and importer in states and states[importer].get('receipt',{}).get('allCompleted') is True and validator is None:
        journal.append('batch:'+str(importer),'validating',states[importer]['receipt']);states[importer]=journal.latest['batch:'+str(importer)]
        validator,importer=slots(schedule,states)
    actions=[]
    if validator is not None:actions.append((validator,'validation',validate_batch))
    if importer is not None:
        package=schedule.package(importer);package.close()
        journal.append('batch:'+str(importer),'importing',states.get(importer,{}).get('receipt'))
        actions.append((importer,'import',advance_import))
    deadline=time.monotonic()+2*getattr(args,'phase_timeout',600)
    import_passes=0
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        pending={pool.submit(action,schedule,index,args,root):(index,phase,action) for index,phase,action in actions}
        while pending:
            finished,_=concurrent.futures.wait(pending,return_when=concurrent.futures.FIRST_COMPLETED)
            for future in finished:
                index,phase,action=pending.pop(future)
                try:
                    result=future.result()
                    state='canary_validated' if phase=='validation' else 'import_held' if result.get('allTerminal') is True and result.get('allCompleted') is not True else 'importing'
                    journal.append('batch:'+str(index),state,result)
                    # Continue the same import batch while the independent validator
                    # is occupied. Never admit a third registered batch.
                    import_passes+=int(phase=='import')
                    if phase=='import' and result.get('allTerminal') is not True and import_passes<1000 \
                            and any(value[1]=='validation' for value in pending.values()) and time.monotonic()<deadline:
                        time.sleep(1)
                        pending[pool.submit(action,schedule,index,args,root)]=(index,phase,action)
                except (RuntimeError,ValueError,OSError,KeyError,TypeError):
                    previous=journal.latest.get('batch:'+str(index),{}).get('receipt')
                    journal.append('batch:'+str(index),'validation_held' if phase=='validation' else 'import_held',previous)
    rows=[row for key,row in journal.latest.items() if key.startswith('batch:')]
    return {'registered':len(rows),'canaryValidated':sum(row['state']=='canary_validated' for row in rows),
            'held':sum(row['state'].endswith('_held') for row in rows),'batchCount':schedule.value['batchCount'],
            'skippedReviewed':sum(row['state']=='skipped_reviewed' for row in rows),
            'allDisposed':len(rows)==schedule.value['batchCount'] and all(row['state'] in TERMINAL for row in rows),
            'productionAcknowledged':False,'originalLibraryChanged':False}


def skip_batch(schedule,index,reason,client,root,journal):
    if index not in schedule.rows:
        raise ValueError('Disposition batch is outside this frozen schedule')
    previous=journal.latest.get('batch:'+str(index))
    if previous is None or previous['state'] in TERMINAL:
        raise ValueError('Disposition requires a registered unfinished batch')
    why=private_bytes(reason,64_000)
    if not why.strip():raise ValueError('Explicit disposition reason is required')
    package=schedule.package(index)
    try:
        receipts=[]
        for release in package.manifest['releases']:
            key=runner.intent(package.digest,release['sourceReleaseId'])
            receipt=client.call('/api/v1/migration/submissions/'+key)
            if not receipt or receipt.get('intent')!=key or receipt.get('payload_sha256')!=runner.xml_digest(package.payload(release)) \
                    or receipt.get('state') not in ('completed','failed','accepted_history_unavailable','completed_source_missing'):
                raise ValueError('Active or uncertain dispatch cannot release a pipeline slot')
            receipts.append({'intent':key,'job_id':receipt['job_id'],'state':receipt['state']})
    finally:package.close()
    plan=root/f'batch-{index:04d}.plan.json'
    if plan.exists():
        child('rust-migration-canary.py',['--rollback','--plan-file',str(plan),'--canary-library',str(root/f'batch-{index:04d}.canary')])
    journal.append('batch:'+str(index),'skipped_reviewed',{'reasonDigest':runner.sha(why),'observedReceipts':receipts,
                   'originalLibraryChanged':False,'productionAcknowledged':False})
    return {'skippedReviewed':index,'originalLibraryChanged':False,'productionAcknowledged':False}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('schedule','schedule-digest','batches'):
        parser.add_argument('--'+name,required=True)
    for name in ('url','server-commit','api-key-file','legacy-config','original-library','canary-mount','state-directory'):
        parser.add_argument('--'+name)
    parser.add_argument('--max-candidates',type=int,default=100)
    parser.add_argument('--phase-timeout',type=int,default=600)
    parser.add_argument('--run',action='store_true')
    parser.add_argument('--skip-batch',type=int)
    parser.add_argument('--disposition-file')
    args=parser.parse_args()
    if not 1<=args.max_candidates<=1000:parser.error('Candidate count must be bounded to at most 1000')
    if not 1<=args.phase_timeout<=43200:parser.error('Phase timeout must be bounded to at most 12 hours')
    schedule=Schedule(args.schedule,args.schedule_digest,args.batches)
    if (args.skip_batch is not None or args.disposition_file) and (not args.run or args.skip_batch is None or not args.disposition_file):
        parser.error('Explicit disposition requires --run, --skip-batch and a private reason file')
    if not args.run:
        for index in schedule.rows:
            package=schedule.package(index);package.close()
        print(json.dumps({'verified':True,'batches':len(schedule.rows),'scheduleDigest':schedule.digest,'dispatched':False}));return
    required=('url','server_commit','api_key_file','legacy_config','original_library','canary_mount','state_directory')
    if any(not getattr(args,key) for key in required):parser.error('Running requires explicit source, private key and separate state/library/mount roots')
    client=runner.Client(args.url,canary.private_key(args.api_key_file));status=client.call('/api/v1/status')
    if status.get('commit')!=args.server_commit or status.get('source_repository')!='johoja12/infinidysk' \
            or len(args.server_commit)!=40 or any(c not in '0123456789abcdef' for c in args.server_commit):
        raise ValueError('Rust source differs from frozen batch plan')
    scope=runner.digest({'schedule':schedule.digest,'url':client.url,'commit':args.server_commit,
                         **{key:str(Path(getattr(args,key)).resolve(strict=True)) for key in ('legacy_config','original_library','canary_mount')}})
    protected=[schedule.root,*[Path(getattr(args,key)).resolve(strict=True) for key in ('legacy_config','original_library','canary_mount')]]
    root=state_root(args.state_directory,scope,protected);journal=runner.Journal(root/'pipeline.jsonl',scope)
    try:
        result=skip_batch(schedule,args.skip_batch,args.disposition_file,client,root,journal) if args.skip_batch is not None else tick(schedule,args,root,journal)
        print(json.dumps(result))
    finally:journal.close()


if __name__=='__main__':
    try:main()
    except (RuntimeError,ValueError,OSError,KeyError,TypeError) as error:raise SystemExit(str(error)) from None
