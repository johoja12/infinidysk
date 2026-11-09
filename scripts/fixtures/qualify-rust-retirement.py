#!/usr/bin/env python3
"""Qualify guarded retirement in a generated native server, loopback Arr and real FUSE mount."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import select
import sqlite3
import subprocess
import time
import urllib.request

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--binary',required=True,type=Path)
parser.add_argument('--output',required=True,type=Path)
args=parser.parse_args()
base=Path(__file__).resolve().parents[2]
commit=subprocess.check_output(['git','rev-parse','HEAD'],cwd=base,text=True).strip()
binary=args.binary.resolve(strict=True)
output=args.output.absolute()
if output.exists():raise SystemExit('Choose a new qualification output path')
if os.getuid()!=0:raise SystemExit('Run in a disposable root-capable FUSE environment')
process=None;root=None;reader=None
log=open(str(output)+'.server.log','x')

def api(path,value=None,method=None):
    request=urllib.request.Request(url+path,data=json.dumps(value).encode() if value is not None else None,method=method,headers={'Content-Type':'application/json'})
    with urllib.request.urlopen(request,timeout=30) as response:return json.load(response)

def wait(check,message,seconds=60):
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        try:
            result=check()
            if result:return result
        except (OSError,ValueError):pass
        time.sleep(.2)
    raise RuntimeError(message)

def row():
    rows=api('/api/v1/health/legacy-repairs')
    return next(r for r in rows if r['evidence_id']=='generated-poc-request')

def due():
    # Only the private generated fixture scheduling clock changes; never its proof.
    db=sqlite3.connect(root/'infinidysk.sqlite')
    db.execute("UPDATE legacy_repair_holds SET observed_at=0 WHERE evidence_id='generated-poc-request'")
    db.commit();db.close()

try:
    process=subprocess.Popen([str(binary),'--retirement-poc'],stdout=subprocess.PIPE,stderr=log,text=True,env={**os.environ,'TOKIO_WORKER_THREADS':'4'})
    end=time.monotonic()+90
    while time.monotonic()<end:
        if select.select([process.stdout],[],[],.2)[0]:
            line=process.stdout.readline()
            if line.startswith('/tmp/infinidysk-fork-local-') and line.strip().endswith('/receipt.json'):
                receipt=json.loads(Path(line.strip()).read_text());root=Path(receipt['root']);break
    if root is None:raise RuntimeError('Native fixture did not initialize')
    assert (root/'.fork-local-fixture').read_text()=='generated-seed-1025-v1'
    url=receipt['url'];assert url.startswith('http://127.0.0.1:')
    wait(lambda:api('/api/v1/status')['commit']==commit and os.path.ismount(root/'mount'),'Native source/mount not ready')
    with Path(str(output)+'.active.json').open('x') as artifact:artifact.write(json.dumps({'root':str(root),'url':url,'source_commit':commit}))
    legacy=receipt['legacy_retirement']['item']
    target=root/'mount'/('.ids/'+'/'.join(legacy[:5])+'/'+legacy)
    reader=os.open(target,os.O_RDONLY);assert len(os.pread(reader,1,0))==1
    (root/'replacement-observed').touch()
    due();wait(lambda:row()['resolved_at'] is not None,'Replacement observation did not complete')
    assert row()['source_present'] and row()['retired_at'] is None
    due();wait(lambda:row()['observed_at']>0,'Reader-held retirement was not attempted')
    assert row()['source_present'],'Active FUSE reader lost its source'
    settings=api('/api/v1/settings')['settings'];settings['health']['auto_repair']=False
    api('/api/v1/settings',settings,'PUT')
    os.close(reader);reader=None
    due();wait(lambda:row()['observed_at']>0,'Disabled retirement was not checked')
    assert row()['source_present'],'Disabled automatic repair retired the source'
    settings=api('/api/v1/settings')['settings'];settings['health']['auto_repair']=True
    api('/api/v1/settings',settings,'PUT')
    # A real last-reader close must wake the observer without changing its clock.
    reader=os.open(target,os.O_RDONLY);assert len(os.pread(reader,1,0))==1
    os.close(reader);reader=None
    audit=wait(lambda:(r if (r:=row())['retired_at'] is not None else None),'Reader-free source was not retired')
    assert not audit['source_present']
    assert not (root/'unexpected-arr-write').exists()
    with (root/'mount'/receipt['source_path'].lstrip('/')).open('rb') as stream:data=stream.read()
    assert len(data)==receipt['size'] and hashlib.sha256(data).hexdigest()==receipt['sha256']
    result={'passed':True,'source_commit':commit,'binary_sha256':hashlib.sha256(binary.read_bytes()).hexdigest(),'root':str(root),'url':url,'actualFuseReaderProtected':True,'disabledAutomaticRepairProtected':True,'freshReplacementRetiredOldSource':True,'immutableAuditRetained':True,'arrWrites':0,'unrelatedSourceHashMatches':True,'scope':'Generated native app, actual kernel FUSE reader and loopback Arr. No live Arr, provider or production data.'}
    with output.open('x') as artifact:artifact.write(json.dumps(result,indent=2))
    print(json.dumps(result))
finally:
    if reader is not None:os.close(reader)
    if root and (root/'.fork-local-fixture').is_file():(root/'stop').touch()
    if process:
        try:process.wait(timeout=30)
        except subprocess.TimeoutExpired:process.terminate();process.wait(timeout=10)
    log.close()
