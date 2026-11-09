#!/usr/bin/env python3
"""Freeze migration order from a complete Rust Plex prediction snapshot; never refresh it on resume."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import sys
import time

SPEC=importlib.util.spec_from_file_location('canary',Path(__file__).with_name('rust-migration-canary.py'))
canary=importlib.util.module_from_spec(SPEC);SPEC.loader.exec_module(canary)
runner=canary.runner
TIERS={'playback':0,'next_episode':0,'partial_movie':0,'hub':1,'collection':2,'library':2}


def build(root,master,count,snapshot,commit,now,max_age=3600):
    updated=snapshot.get('updated_at')
    if snapshot.get('stale') is not False or snapshot.get('refreshing') is not False or snapshot.get('error') \
            or type(updated) is not int or updated>now+30_000 or now-updated>max_age*1000 \
            or not isinstance(snapshot.get('predictions'),list) or len(snapshot['predictions'])>10000:
        raise ValueError('Plex priority requires a complete current prediction snapshot')
    if not runner.lower_digest(master) or type(count) is not int or not 1<=count<=10000:
        raise ValueError('Exact master digest and bounded batch count are required')
    ranked={}
    for row in snapshot['predictions']:
        item=row.get('item');source=row.get('source')
        if not item or source not in TIERS or row.get('warning'):continue
        canary.links.ids_path(item)
        rank=TIERS[source]
        if item not in ranked or rank<ranked[item]['tier']:
            ranked[item]={'item':item,'tier':rank,'source':source}
    batches=[]
    for index in range(count):
        path=Path(root)/f'batch-{index+1:04d}'
        fd=canary.links.root_fd(path)
        try:
            # Snapshot creation establishes the retained digest; all subsequent
            # execution verifies this exact value through Schedule/Package.
            raw=canary.links.open_virtual(fd,'manifest.json')
            with os.fdopen(raw,'rb') as stream:data=stream.read(runner.MAX_JSON+1)
        finally:os.close(fd)
        if len(data)>runner.MAX_JSON:raise ValueError('Batch manifest exceeds bound')
        digest=runner.sha(data);package=runner.Package(path,digest)
        try:
            m=package.manifest
            if m.get('masterManifestDigest')!=master or m.get('batchIndex')!=index or m.get('batchCount')!=count:
                raise ValueError('Priority input batch belongs to another sealed master')
            matches=[ranked[link['legacyDavItemId']] for link in m['selectedLinks'] if link['legacyDavItemId'] in ranked]
            counts=[sum(row['tier']==tier for row in matches) for tier in range(3)]
            batches.append({'batchIndex':index,'manifestDigest':digest,'countsByTier':counts,'matchedItems':matches})
        finally:package.close()
    order=sorted(range(count),key=lambda i:(next((rank for rank,n in enumerate(batches[i]['countsByTier']) if n),3),
                                            *(-n for n in batches[i]['countsByTier']),i))
    return {'schemaVersion':1,'masterManifestDigest':master,'batchCount':count,'order':order,'batches':batches,
            'source':{'kind':'rust-plex-predictions','commit':commit,'snapshotDigest':runner.digest(snapshot),'updatedAt':updated},
            'createdAt':now}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('batches','master-digest','url','server-commit','api-key-file','output'):
        parser.add_argument('--'+name,required=True)
    parser.add_argument('--batch-count',required=True,type=int)
    parser.add_argument('--max-age',type=int,default=3600)
    args=parser.parse_args()
    if not 1<=args.max_age<=86400:parser.error('Snapshot age must be bounded to at most one day')
    client=runner.Client(args.url,canary.private_key(args.api_key_file));status=client.call('/api/v1/status')
    if status.get('commit')!=args.server_commit or status.get('source_repository')!='johoja12/infinidysk' \
            or len(args.server_commit)!=40 or any(c not in '0123456789abcdef' for c in args.server_commit):
        raise ValueError('Rust source differs from priority plan')
    snapshot=client.call('/api/v1/prefetch/predictions')
    plan=build(args.batches,args.master_digest,args.batch_count,snapshot,args.server_commit,time.time_ns()//1_000_000,args.max_age)
    output=Path(args.output).absolute();output=output.parent.resolve(strict=True)/output.name
    if canary.links.contained(Path(args.batches).resolve(strict=True),output):
        raise ValueError('Priority output must be outside sealed package roots')
    canary.save_plan(output,plan)
    print(json.dumps({'scheduleDigest':runner.digest(plan),'batches':args.batch_count,'sourceUpdatedAt':snapshot['updated_at'],'dispatched':False}))


if __name__=='__main__':
    try:main()
    except (ValueError,RuntimeError,OSError,KeyError,TypeError) as error:raise SystemExit(str(error)) from None
