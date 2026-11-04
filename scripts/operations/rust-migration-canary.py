#!/usr/bin/env python3
"""Validate preserved legacy IDs through the current Rust mount and create a separate canary library.

Original libraries and sealed packages remain read-only. Sampling is not whole-file
health certification. This command never acknowledges production cutover.
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import urllib.parse

import rust_migration_links as links

SPEC=importlib.util.spec_from_file_location('rust_import_runner',Path(__file__).with_name('rust-import-runner.py'))
runner=importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)


def private_key(path):
    fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK)
    with os.fdopen(fd) as stream:
        info=os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_nlink!=1 or info.st_mode&0o077:
            raise ValueError('API key file must be owner-only and regular')
        return stream.read(4097).strip()


def proof_request(package,leaf,release,source):
    return {'source_config':str(source),'package_digest':package.digest,'legacy_item':leaf['legacyDavItemId'],
            'identity_kind':leaf['identityKind'],'identity_digest':leaf['identityDigest'],
            'payload_sha256':runner.xml_digest(package.payload(release)),'size':leaf['fileSize']}


def same_proof(previous,current):
    names=('version','package_digest','legacy_item','current_item','identity_kind','identity_digest',
           'payload_sha256','size','source_revision','ids_path','record_digest','payload_validated',
           'submission_intent','job_id','download_dir')
    if any(previous.get(name)!=current.get(name) for name in names) or current.get('payload_validated') is not False:
        raise ValueError('Current source proof changed; preserve the plan and validate again')


def load_mapping(path,expected,package,commit):
    fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK)
    with os.fdopen(fd,'rb') as stream:
        info=os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_nlink!=1 or info.st_mode&0o077 or info.st_size>16_000_000:
            raise ValueError('Reimport map must be a bounded owner-only regular file')
        raw=stream.read(16_000_001)
    if not runner.lower_digest(expected) or runner.sha(raw)!=expected:
        raise ValueError('Reimport map differs from its retained digest')
    value=runner.unique_json(raw)
    if set(value)!={'version','packageDigest','serverCommit','bindings'} or value['version']!=1 \
            or value['packageDigest']!=package.digest or value['serverCommit']!=commit:
        raise ValueError('Reimport map belongs to a different package or Rust source')
    selected={link['legacyDavItemId'] for link in package.manifest['selectedLinks']}
    releases={leaf['legacyDavItemId']:release['sourceReleaseId'] for release in package.manifest['releases'] for leaf in release['leaves']}
    result={};current=set()
    if not isinstance(value['bindings'],list) or len(value['bindings'])>1000:
        raise ValueError('Reimport map exceeds member bound')
    for row in value['bindings']:
        if set(row)!={'legacyItem','currentItem','submissionIntent','sourceReleaseId'}:
            raise ValueError('Invalid reimport map binding')
        old=row['legacyItem'];new=row['currentItem'];links.ids_path(new)
        if old not in selected or old in result or new in current \
                or row['sourceReleaseId']!=releases[old] \
                or row['submissionIntent']!=runner.intent(package.digest,releases[old]):
            raise ValueError('Duplicate or mismatched reimport member binding')
        result[old]=row;current.add(new)
    if set(result)!=selected:
        raise ValueError('Reimport map does not cover every selected member')
    return result


def fetch_proof(client,request,entry=None):
    if entry is None:
        return client.json('/api/v1/migration/legacy-proof',request)
    receipt=client.call('/api/v1/migration/submissions/'+entry['submissionIntent'])
    if not receipt or receipt.get('state')!='completed' or receipt.get('intent')!=entry['submissionIntent'] \
            or receipt.get('payload_sha256')!=request['payload_sha256']:
        raise ValueError('Reimport has no exact completed acceptance; validation remains pending')
    proof=client.json('/api/v1/migration/reimport-proof',{'source':request,'current_item':entry['currentItem'],
                     'submission_intent':entry['submissionIntent'],'source_release_id':entry['sourceReleaseId']})
    if proof.get('submission_intent')!=entry['submissionIntent'] or proof.get('job_id')!=receipt.get('job_id') \
            or proof.get('download_dir')!=receipt.get('download_dir'):
        raise ValueError('Reimport proof differs from its durable accepted job')
    return proof


def read_worker():
    raw=sys.stdin.buffer.read(128_001)
    if len(raw)>128_000:
        raise ValueError('Read request exceeds bound')
    request=runner.unique_json(raw)
    fd=links.root_fd(request['mount'])
    try:
        links.check_probe(fd,request['probe'])
        result={'mountDevice':os.fstat(fd).st_dev,'mountInode':os.fstat(fd).st_ino}
        if request.get('proof'):
            result['sampledValidation']=links.sample(fd,request['proof'],request['extension'])
        print(json.dumps(result))
    finally:
        os.close(fd)


def sample_mount(mount,probe,proof=None,extension=''):
    request={'mount':str(mount),'probe':probe,'proof':proof,'extension':extension}
    try:
        result=subprocess.run([sys.executable,__file__,'--read-worker'],input=runner.canonical(request),
                              stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=60,check=False)
    except subprocess.TimeoutExpired:
        raise RuntimeError('Canary read timed out; validation remains pending') from None
    if result.returncode or len(result.stdout)>128_000:
        raise RuntimeError('Canary mount/source validation failed; original links remain unchanged')
    return runner.unique_json(result.stdout)


def save_plan(path,plan):
    fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
    with os.fdopen(fd,'wb') as stream:
        stream.write(runner.canonical(plan));stream.flush();os.fsync(stream.fileno())
    parent=os.open(Path(path).parent,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)
    try:
        os.fsync(parent)
    finally:
        os.close(parent)


def load_plan(path):
    fd=os.open(path,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK)
    with os.fdopen(fd,'rb') as stream:
        info=os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_nlink!=1 or info.st_mode&0o077 or info.st_size>16_000_000:
            raise ValueError('Canary plan must be a bounded owner-only regular file')
        value=runner.unique_json(stream.read(16_000_001))
    links.check_plan(value)
    return value


def main():
    if sys.argv[1:]==['--read-worker']:
        read_worker();return
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('package','manifest-digest','url','server-commit','api-key-file','legacy-config',
                 'original-library','canary-mount','canary-library','plan-file'):
        parser.add_argument('--'+name,required=name in ('canary-library','plan-file'))
    parser.add_argument('--reimport-map')
    parser.add_argument('--reimport-map-digest')
    parser.add_argument('--max-members',type=int,default=50)
    parser.add_argument('--apply',action='store_true',help='Create links only in the separate owned canary directory')
    parser.add_argument('--rollback',action='store_true',help='Conditionally remove links owned by the saved canary plan')
    args=parser.parse_args()
    if args.apply and args.rollback or not 1<=args.max_members<=1000:
        parser.error('Choose one canary action and a bounded member count')
    if args.rollback:
        path=Path(args.plan_file).absolute();plan=load_plan(path)
        if Path(args.canary_library).absolute()!=Path(plan['destination']):
            raise ValueError('Rollback destination differs from the saved canary plan')
        journal=runner.Journal(str(path)+'.links.jsonl',plan['planDigest'])
        try:
            print(json.dumps({'rolledBack':links.rollback_canary(plan,journal),'originalLibraryChanged':False}))
        finally:
            journal.close()
        return
    if any(not getattr(args,name) for name in ('package','manifest_digest','url','server_commit','api_key_file','legacy_config','original_library','canary_mount')):
        parser.error('Validation requires a sealed package, exact Rust source, private key, offline config and explicit library/mount roots')
    if bool(args.reimport_map)!=bool(args.reimport_map_digest):
        parser.error('Reimport mapping requires its independently retained digest')
    package=runner.Package(args.package,args.manifest_digest)
    try:
        source=Path(args.legacy_config).absolute();mount=Path(args.canary_mount).resolve(strict=True)
        original=Path(args.original_library).resolve(strict=True);destination=Path(args.canary_library).absolute()
        client=runner.Client(args.url,private_key(args.api_key_file))
        status=client.call('/api/v1/status')
        if not (len(args.server_commit)==40 and all(c in '0123456789abcdef' for c in args.server_commit)) \
                or status.get('commit')!=args.server_commit or status.get('source_repository')!='johoja12/infinidysk':
            raise ValueError('Rust source identity differs from this canary plan')
        mapping=load_mapping(args.reimport_map,args.reimport_map_digest,package,args.server_commit) if args.reimport_map else {}
        scope=runner.digest({'version':1,'manifest':package.digest,'server':client.url,'commit':args.server_commit,
                             'sourceConfig':str(source),'mount':str(mount),'original':str(original),'destination':str(destination),
                             'reimportMap':args.reimport_map_digest})
        path=Path(args.plan_file).absolute()
        protected=[original,mount,source.resolve(strict=True),Path(args.package).resolve(strict=True)]
        if any(links.contained(root,path) or links.contained(root,destination) for root in protected):
            raise ValueError('Plan/journals/canary output must be outside read-only source roots')
        selected=package.manifest['selectedLinks']
        if not 1<=len(selected)<=args.max_members:
            raise ValueError('Selection is empty or exceeds this canary member bound')
        release_by_leaf={leaf['legacyDavItemId']:(leaf,release) for release in package.manifest['releases'] for leaf in release['leaves']}
        validation=runner.Journal(str(path)+'.validation.jsonl',scope)
        try:
            bindings=[]
            for link in selected:
                leaf,release=release_by_leaf[link['legacyDavItemId']]
                key=leaf['legacyDavItemId']
                request=proof_request(package,leaf,release,source)
                validation.append(key,'validation_pending')
                try:
                    entry=mapping.get(key)
                    current=entry['currentItem'] if entry else key
                    proof=fetch_proof(client,request,entry)
                    if any(proof.get(name)!=expected for name,expected in {
                        'version':1,'package_digest':package.digest,'legacy_item':key,'current_item':current,
                        'identity_kind':leaf['identityKind'],'identity_digest':leaf['identityDigest'],
                        'payload_sha256':request['payload_sha256'],'size':leaf['fileSize'],
                        'ids_path':links.ids_path(current),'payload_validated':False}.items()) \
                            or proof.get('payload_validated') is not False or not runner.lower_digest(proof.get('source_revision')) or not runner.lower_digest(proof.get('record_digest')):
                        raise ValueError('Member proof does not bind the selected sealed identity')
                    challenge=client.call('/api/v1/migration/mount-probe',b'')
                    read=sample_mount(mount,challenge,proof,Path(link['libraryRelativePath']).suffix)
                    fresh=fetch_proof(client,request,entry);same_proof(proof,fresh)
                    sample_mount(mount,client.call('/api/v1/migration/mount-probe',b''))
                    binding={'link':link,'proof':proof,'sampledValidation':read['sampledValidation']}
                    validation.append(key,'sampled_validation_passed',binding)
                    bindings.append(binding)
                except (ValueError,RuntimeError,OSError):
                    validation.append(key,'validation_held')
                    raise
            candidate=links.plan(original,mount,destination,bindings,scope)
            if path.exists():
                plan=load_plan(path)
                if plan['planDigest']!=candidate['planDigest']:
                    raise ValueError('Saved canary plan changed; preserve it and use a new plan/destination')
            else:
                plan=candidate;save_plan(path,plan)
            created=0
            if args.apply:
                journal=runner.Journal(str(path)+'.links.jsonl',plan['planDigest'])
                def recheck(previous):
                    leaf,release=release_by_leaf[previous['legacy_item']]
                    current=fetch_proof(client,proof_request(package,leaf,release,source),mapping.get(previous['legacy_item']))
                    same_proof(previous,current)
                    sample_mount(mount,client.call('/api/v1/migration/mount-probe',b''))
                try:
                    created=links.apply_canary(plan,journal,recheck)
                finally:
                    journal.close()
            print(json.dumps({'planDigest':plan['planDigest'],'sampledMembers':len(bindings),'createdCanaryLinks':created,
                              'originalLibraryChanged':False,'productionAcknowledged':False,'wholeLibraryHealthCertified':False}))
        finally:
            validation.close()
    finally:
        package.close()


if __name__=='__main__':
    try:
        main()
    except (ValueError,RuntimeError,OSError,KeyError,TypeError) as error:
        raise SystemExit(str(error)) from None
