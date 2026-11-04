#!/usr/bin/env python3
"""Discover exact new-ID members of completed sealed imports; never submit or promote."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import sys
import urllib.parse

SPEC=importlib.util.spec_from_file_location('canary',Path(__file__).with_name('rust-migration-canary.py'))
canary=importlib.util.module_from_spec(SPEC);SPEC.loader.exec_module(canary)
runner=canary.runner


def candidates(client,root,limit):
    detail=client.call('/api/v1/files/'+root)
    entry=detail['entry']
    if entry.get('item_id')!=root or entry.get('kind')!='directory':
        raise ValueError('Accepted import root is unavailable')
    root_path=entry['path']
    if not root_path.startswith('/content/'):
        raise ValueError('Accepted import root is outside content')
    pending=[root_path];seen=set();files={};directories=0
    while pending:
        path=pending.pop(0)
        if path in seen:
            raise ValueError('Import subtree has duplicate directory bindings')
        seen.add(path);directories+=1
        if directories>128:
            raise ValueError('Import subtree exceeds directory bound')
        offset=0;total=None
        while True:
            page=client.call('/api/v1/files?'+urllib.parse.urlencode({'path':path,'limit':100,'offset':offset}))
            if type(page.get('total')) is not int or page['total']<0 or page['total']>limit+128:
                raise ValueError('Import listing exceeds bound')
            if total is None:total=page['total']
            if total!=page['total'] or not isinstance(page.get('entries'),list):
                raise ValueError('Import listing changed during discovery')
            rows=page['entries']
            if not rows and offset<total or len(rows)>100 or offset+len(rows)>total:
                raise ValueError('Import listing is incomplete')
            for row in rows:
                child=row['path']
                if not child.startswith(path.rstrip('/')+'/') or '/' in child[len(path.rstrip('/'))+1:]:
                    raise ValueError('Import listing escaped its parent directory')
                if row.get('kind')=='directory':
                    pending.append(child)
                elif row.get('kind')=='file':
                    key=row['item_id'];canary.links.ids_path(key)
                    if key in files:
                        raise ValueError('Import listing has duplicate member identities')
                    files[key]=row
                    if len(files)>limit:
                        raise ValueError('Import exceeds candidate bound')
                else:
                    raise ValueError('Import listing has an unresolved link')
            offset+=len(rows)
            if offset==total:break
    return files


def discover(package,client,source,limit=100):
    selected={row['legacyDavItemId'] for row in package.manifest['selectedLinks']}
    bindings=[];used=set()
    for release in package.manifest['releases']:
        leaves=[leaf for leaf in release['leaves'] if leaf['legacyDavItemId'] in selected]
        if not leaves:continue
        intent=runner.intent(package.digest,release['sourceReleaseId'])
        receipt=client.call('/api/v1/migration/submissions/'+intent)
        expected=runner.xml_digest(package.payload(release))
        if not receipt or receipt.get('state')!='completed' or receipt.get('intent')!=intent \
                or receipt.get('payload_sha256')!=expected:
            raise ValueError('Import acceptance is not exactly completed; discovery remains pending')
        root=receipt['download_dir'];canary.links.ids_path(root)
        choices=candidates(client,root,limit)
        for leaf in leaves:
            matches=[]
            for current,row in choices.items():
                # Size only prunes impossible candidates. Exact source proof is mandatory.
                if row.get('size')!=leaf['fileSize']:continue
                entry={'legacyItem':leaf['legacyDavItemId'],'currentItem':current,'submissionIntent':intent,
                       'sourceReleaseId':release['sourceReleaseId']}
                try:
                    proof=canary.fetch_proof(client,canary.proof_request(package,leaf,release,source),entry)
                except runner.HttpFailure as error:
                    if error.status==409:continue
                    raise
                if proof.get('legacy_item')!=leaf['legacyDavItemId'] or proof.get('current_item')!=current \
                        or proof.get('identity_kind')!=leaf['identityKind'] or proof.get('identity_digest')!=leaf['identityDigest'] \
                        or proof.get('payload_sha256')!=expected or proof.get('size')!=leaf['fileSize'] \
                        or proof.get('package_digest')!=package.digest or proof.get('payload_validated') is not False \
                        or type(proof.get('version')) is not int or proof['version']!=1 \
                        or proof.get('ids_path')!=canary.links.ids_path(current) \
                        or not runner.lower_digest(proof.get('source_revision')) or not runner.lower_digest(proof.get('record_digest')):
                    raise ValueError('Server member proof does not bind this sealed selection')
                matches.append(entry)
            if len(matches)!=1 or matches[0]['currentItem'] in used:
                raise ValueError('Member is missing or ambiguous; original links remain unchanged')
            used.add(matches[0]['currentItem']);bindings.extend(matches)
        if candidates(client,root,limit)!=choices:
            raise ValueError('Import subtree changed; preserve the previous map and retry discovery')
        current_receipt=client.call('/api/v1/migration/submissions/'+intent)
        if current_receipt!=receipt:
            raise ValueError('Accepted import changed during discovery')
    if len(bindings)!=len(selected):
        raise ValueError('Discovery did not cover every selected member')
    return bindings


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('package','manifest-digest','url','server-commit','api-key-file','legacy-config','output'):
        parser.add_argument('--'+name,required=True)
    parser.add_argument('--max-candidates',type=int,default=100)
    args=parser.parse_args()
    if not 1<=args.max_candidates<=1000:parser.error('Candidate bound must be between 1 and 1000')
    package=runner.Package(args.package,args.manifest_digest)
    try:
        client=runner.Client(args.url,canary.private_key(args.api_key_file))
        status=client.call('/api/v1/status')
        if status.get('commit')!=args.server_commit or status.get('source_repository')!='johoja12/infinidysk' \
                or len(args.server_commit)!=40 or any(c not in '0123456789abcdef' for c in args.server_commit):
            raise ValueError('Rust source differs from discovery plan')
        source=Path(args.legacy_config).resolve(strict=True);output=Path(args.output).absolute()
        output=output.parent.resolve(strict=True)/output.name
        if any(canary.links.contained(root,output) for root in (source,Path(args.package).resolve(strict=True))):
            raise ValueError('Mapping output must be outside read-only source roots')
        bindings=discover(package,client,source,args.max_candidates)
        value={'version':1,'packageDigest':package.digest,'serverCommit':args.server_commit,'bindings':bindings}
        raw=runner.canonical(value)
        try:
            canary.save_plan(output,value)
        except FileExistsError:
            # Resume verifies private file ownership/shape and the exact new result.
            canary.load_mapping(output,runner.sha(raw),package,args.server_commit)
        print(json.dumps({'mapDigest':runner.sha(raw),'members':len(bindings),'submitted':False,'originalLibraryChanged':False}))
    finally:
        package.close()


if __name__=='__main__':
    try:main()
    except (ValueError,RuntimeError,OSError,KeyError,TypeError) as error:
        raise SystemExit(str(error)) from None
