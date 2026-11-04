#!/usr/bin/env python3
"""Generated end-to-end qualification; no production roots or credentials are used."""
import ctypes,ctypes.util,hashlib,importlib.util,json,os,select,struct,subprocess,sys,time,urllib.request,uuid
from pathlib import Path
import argparse
parser=argparse.ArgumentParser(description="Generated native migration and cache qualification. Requires a disposable root-capable FUSE environment.")
parser.add_argument('--binary',required=True,type=Path)
parser.add_argument('--output',required=True,type=Path)
args=parser.parse_args()
BASE=Path(__file__).resolve().parents[2]
CODE=subprocess.check_output(['git','rev-parse','HEAD'],cwd=BASE,text=True).strip()
BINARY=args.binary.resolve(strict=True)
OUTPUT=args.output.absolute()
if OUTPUT.exists():raise SystemExit('Choose a new qualification output path')
spec=importlib.util.spec_from_file_location('fixture',BASE/'scripts/operations/test_rust_import_runner.py');fixture=importlib.util.module_from_spec(spec);sys.path.insert(0,str(BASE/'scripts/operations'));spec.loader.exec_module(fixture)
canon=fixture.runner.canonical;sha=fixture.runner.sha
process=None;root=None;log=None

def api(url,path,body=None,method=None):
 raw=canon(body) if body is not None else None
 request=urllib.request.Request(url+path,data=raw,method=method,headers={'Content-Type':'application/json'})
 with urllib.request.urlopen(request,timeout=60) as response:return json.load(response)

def varint(n):
 if n<=127:return bytes([n])
 if n<=255:return b'\x87'+bytes([n])
 return b'\x85'+struct.pack('<H',n)
def obj(fields):return bytes([len(fields)])+b''.join(varint(len(f)) for f in fields)+b''.join(fields)
def mstring(s):
 raw=s.encode();return struct.pack('<ii',~len(raw),len(s))+raw

def zstd(raw):
 lib=ctypes.CDLL(ctypes.util.find_library('zstd'))
 lib.ZSTD_compressBound.argtypes=[ctypes.c_size_t];lib.ZSTD_compressBound.restype=ctypes.c_size_t
 lib.ZSTD_compress.argtypes=[ctypes.c_void_p,ctypes.c_size_t,ctypes.c_void_p,ctypes.c_size_t,ctypes.c_int];lib.ZSTD_compress.restype=ctypes.c_size_t
 cap=lib.ZSTD_compressBound(len(raw));out=ctypes.create_string_buffer(cap)
 length=lib.ZSTD_compress(out,cap,raw,len(raw),1);assert length<=cap
 return out.raw[:length]

def run(args,timeout=240):
 result=subprocess.run([sys.executable,*map(str,args)],stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=timeout,env={**os.environ,'PYTHONDONTWRITEBYTECODE':'1'})
 if result.returncode:raise RuntimeError('Generated CLI failed: '+result.stderr.decode()[-2000:])
 return json.loads(result.stdout)

try:
 assert os.getuid()==0 and BINARY.exists()
 log=open(str(OUTPUT)+'.server.log','x')
 process=subprocess.Popen([str(BINARY)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,bufsize=1,env={**os.environ,'TOKIO_WORKER_THREADS':'4'})
 deadline=time.monotonic()+90
 while time.monotonic()<deadline:
  if not select.select([process.stdout],[],[],.25)[0]:continue
  line=process.stdout.readline();log.write(line);log.flush()
  if line.startswith('/tmp/infinidysk-fork-local-') and line.strip().endswith('/receipt.json'):
   receipt=json.loads(Path(line.strip()).read_text());root=Path(receipt['root']);break
 assert root and (root/'.fork-local-fixture').read_text()=='generated-seed-1025-v1'
 url=receipt['url'];assert url.startswith('http://127.0.0.1:')
 deadline=time.monotonic()+60
 while True:
  try:
   status=api(url,'/api/v1/status')
   if status.get('commit')==CODE and os.path.ismount(root/'mount'):break
  except (OSError,ValueError):pass
  assert time.monotonic()<deadline,'Native server/mount not ready'
  time.sleep(.2)
 assert status['source_repository']=='johoja12/infinidysk',status
 work=root/'migration-poc';work.mkdir(mode=0o700)
 original=work/'original';(original/'fixture').mkdir(parents=True)
 source=work/'offline-config';source.mkdir(mode=0o700)
 old=str(uuid.uuid4());meta=uuid.uuid4();nzb=uuid.uuid4();size=8388608
 ids=[f'bench-{i:04}@loopback' for i in range(32)]
 xml=('<nzb><file poster="fixture" date="0" subject="fixture.bin"><groups><group>fixture</group></groups><segments>'+''.join(f'<segment bytes="262144" number="{i+1}">{item}</segment>' for i,item in enumerate(ids))+'</segments></file></nzb>').encode()
 h=hashlib.sha256()
 def text(s):
  raw=s.encode();h.update(struct.pack('>i',len(raw)));h.update(raw)
 text('nzbdav-direct-v1');h.update(struct.pack('>i',len(ids)))
 for i,item in enumerate(ids):h.update(struct.pack('>iq',i+1,262144));text(item)
 identity=h.hexdigest()
 strings=struct.pack('<i',32)+b''.join(mstring(item) for item in ids)
 ranges=struct.pack('<i',32)+b''.join(obj([struct.pack('<q',i*262144),struct.pack('<q',(i+1)*262144)]) for i in range(32))
 metadata=obj([meta.bytes_le,strings,ranges])
 for ident,body in ((meta,zstd(metadata)),(nzb,xml)):
  hexx=ident.hex;path=source/'blobs'/hexx[:2]/hexx[2:4]/str(ident);path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(body)
 import sqlite3
 cs=sqlite3.connect(source/'db.sqlite')
 cs.execute('CREATE TABLE DavItems(Id TEXT,ParentId TEXT,Name TEXT,Path TEXT,SubType INTEGER,FileSize INTEGER,FileBlobId TEXT,NzbBlobId TEXT,CreatedAt TEXT,ReleaseDate INTEGER,LastHealthCheck INTEGER,NextHealthCheck INTEGER,HealthRepairPending INTEGER)')
 cs.execute("INSERT INTO DavItems VALUES(?,NULL,'fixture.bin','/content/fixture.bin',201,?,?,?,'2025-01-01',NULL,NULL,NULL,0)",(old,size,str(meta),str(nzb)));cs.commit();cs.close()
 original_link=original/'fixture/file.bin';os.symlink('/legacy/.ids/'+old,original_link);before=original_link.lstat();source_before=sha((source/'db.sqlite').read_bytes())
 sealed=work/'sealed';sealed.mkdir();package=sealed/'batch-0001'
 fixture.package(package)
 m=json.loads((package/'manifest.json').read_text());leaf=m['releases'][0]['leaves'][0]
 leaf.update(legacyDavItemId=old,legacyPath='/content/fixture/file.bin',fileSize=size,identityDigest=identity)
 m['selectedLinks']=[{'libraryRelativePath':'fixture/file.bin','originalTarget':'/legacy/.ids/'+old,'legacyDavItemId':old}]
 m['payloads'][0].update(length=len(xml),sha256=sha(xml));m['checksums'][0]['sha256']=sha(xml)
 files={'manifest.json':canon(m),'success.json':canon([leaf]),'exclusions.json':b'[]','payloads/fixture-release.nzb':xml}
 for name,data in files.items():(package/name).write_bytes(data)
 (package/'SHA256SUMS').write_text(''.join(sha(data)+'  '+name+'\n' for name,data in sorted(files.items())))
 digest=sha(files['manifest.json'])
 plan={'schemaVersion':1,'masterManifestDigest':'b'*64,'batchCount':1,'order':[0],'batches':[{'batchIndex':0,'manifestDigest':digest}]}
 schedule=work/'priority.json';schedule.write_bytes(canon(plan));schedule.chmod(0o600)
 key=work/'key';key.write_text('generated-fixture');key.chmod(0o600)
 args=[BASE/'scripts/operations/rust-migration-batches.py','--run','--schedule',schedule,'--schedule-digest',sha(canon(plan)),'--batches',sealed,'--url',url,'--server-commit',CODE,'--api-key-file',key,'--legacy-config',source,'--original-library',original,'--canary-mount',root/'mount','--state-directory',work/'state','--phase-timeout','90']
 observations=[]
 for _ in range(20):
  result=run(args);observations.append(result)
  if result['held']:
   journal=(work/'state/pipeline.jsonl').read_text();raise RuntimeError('Native pipeline held: '+journal[-2500:])
  if result['allDisposed']:break
  time.sleep(.5)
 assert result['canaryValidated']==1 and result['allDisposed'],observations
 canary=work/'state/batch-0000.canary/fixture/file.bin';target=os.readlink(canary)
 mapping=json.loads((work/'state/batch-0000.mapping.json').read_text());current=mapping['bindings'][0]['currentItem'];assert current!=old
 with canary.open('rb') as stream:body=stream.read()
 assert len(body)==size and sha(body)==receipt['sha256']
 after=original_link.lstat();assert (after.st_ino,after.st_ctime_ns,os.readlink(original_link))==(before.st_ino,before.st_ctime_ns,'/legacy/.ids/'+old)
 assert sha((source/'db.sqlite').read_bytes())==source_before
 resumed=run(args);assert resumed['canaryValidated']==1 and resumed['allDisposed']
 details=api(url,'/api/v1/files/'+current)
 settings=api(url,'/api/v1/settings')['settings'];settings['providers']=[];api(url,'/api/v1/settings',settings,'PUT')
 time.sleep(.5)
 with canary.open('rb') as stream:cached=stream.read()
 assert sha(cached)==receipt['sha256']
 proof={'passed':True,'qualified_source':CODE,'binary_sha256':sha(BINARY.read_bytes()),'root':str(root),'work':str(work),'generatedLoopbackImport':True,'newIdDiffersFromLegacy':True,'actualKernelMountChallenge':True,'canaryValidated':1,'resumeWithoutNewDispatch':True,'originalLinkUnchanged':True,'offlineSourceUnchanged':True,'fullGeneratedBytesVerified':size,'fullReadWithoutProviders':True,'cachedRanges':details['cached_ranges'],'scope':'Actual Rust native API/queue/NNTP/FUSE/cache and real Python pipeline. Generated binary fixture and generated MemoryPack source, not real media, original C# package generation, Plex, Arr or production cutover.'}
 # Prove persistent cache reuse with another native process before its first read.
 (root/'stop').touch();process.wait(timeout=30)
 def restart(providers):
  global process
  process=subprocess.Popen([str(BINARY),'--resume',str(root)],stdout=subprocess.PIPE,stderr=log,text=True,bufsize=1,env={**os.environ,'TOKIO_WORKER_THREADS':'4'})
  end=time.monotonic()+90
  while time.monotonic()<end:
   if select.select([process.stdout],[],[],.2)[0]:
    line=process.stdout.readline()
    if line.strip().endswith('/receipt.json'):
     fresh=json.loads(Path(line.strip()).read_text());break
  else:raise RuntimeError('Native restart did not initialize')
  while True:
   try:
    view=api(fresh['url'],'/api/v1/status')
    if view['commit']==CODE and os.path.ismount(root/'mount'):break
   except OSError:pass
   assert time.monotonic()<end;time.sleep(.2)
  if not providers:
   settings=api(fresh['url'],'/api/v1/settings')['settings'];settings['providers']=[];api(fresh['url'],'/api/v1/settings',settings,'PUT')
  return fresh
 restart(True)
 with canary.open('rb') as stream:body=stream.read()
 assert len(body)==size and sha(body)==receipt['sha256']
 fresh=json.loads((root/'receipt.json').read_text())
 deadline=time.monotonic()+10
 while True:
  details=api(fresh['url'],'/api/v1/files/'+current)
  if details['cached_ranges']==[{'start':0,'end':size}]:break
  assert time.monotonic()<deadline,'Cold full read did not persist complete cache coverage';time.sleep(.1)
 (root/'stop').touch();process.wait(timeout=30)
 fresh=restart(False)
 started=time.monotonic()
 with canary.open('rb') as stream:body=stream.read()
 assert len(body)==size and sha(body)==receipt['sha256']
 for offset in (12345,size//2+123,size-65536):
  with canary.open('rb') as stream:stream.seek(offset);data=stream.read(min(65536,size-offset))
  assert data==body[offset:offset+len(data)]
 proof.update(persistentCacheRestartWithoutProviders=True,cachedRanges=details['cached_ranges'],providerlessFullReadMs=(time.monotonic()-started)*1000)
 with OUTPUT.open('x') as artifact:artifact.write(json.dumps(proof,indent=2))
 print(json.dumps(proof))
finally:
 if root and (root/'.fork-local-fixture').is_file():(root/'stop').touch()
 if process:
  try:process.wait(timeout=30)
  except subprocess.TimeoutExpired:process.terminate();process.wait(timeout=10)
 if log:log.close()
