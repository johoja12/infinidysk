"""Rooted canary reads and owned link plans. Original libraries stay read-only."""
import hashlib
import json
import os
from pathlib import Path
import stat
import uuid

LIMIT=16_384
MARKER='.rust-migration-canary.json'


def canonical(value):
    return json.dumps(value,sort_keys=True,separators=(',',':'),ensure_ascii=False).encode()


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def parts(relative):
    if not isinstance(relative,str) or not relative or relative.startswith('/') or '\\' in relative \
            or any(ord(c)<32 for c in relative) or any(p in ('','.','..') for p in relative.split('/')):
        raise ValueError('Unsafe relative canary path')
    return relative.split('/')


def ids_path(item):
    parsed=str(uuid.UUID(item))
    if parsed!=item:
        raise ValueError('Noncanonical source UUID')
    return '/.ids/'+'/'.join(item[:5])+'/'+item


def root_fd(root):
    return os.open(root,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW)


def parent_fd(root,relative,create=False,journal=None):
    names=parts(relative)
    parent=os.dup(root)
    try:
        for name in names[:-1]:
            if create:
                try:
                    os.mkdir(name,0o700,dir_fd=parent)
                    os.fsync(parent)
                except FileExistsError:
                    pass
            child=os.open(name,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW,dir_fd=parent)
            os.close(parent)
            parent=child
        return parent,names[-1]
    except BaseException:
        os.close(parent)
        raise


def source_link(root,relative,expected):
    parent,name=parent_fd(root,relative)
    try:
        info=os.stat(name,dir_fd=parent,follow_symlinks=False)
        if not stat.S_ISLNK(info.st_mode) or os.readlink(name,dir_fd=parent)!=expected:
            raise ValueError('Original library entry changed or is not a symlink')
        return {'device':info.st_dev,'inode':info.st_ino,'mtimeNs':info.st_mtime_ns,
                'ctimeNs':info.st_ctime_ns,'target':expected}
    finally:
        os.close(parent)


def open_virtual(root,path):
    parent,name=parent_fd(root,path.lstrip('/'))
    try:
        fd=os.open(name,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK,dir_fd=parent)
        if not stat.S_ISREG(os.fstat(fd).st_mode):
            os.close(fd)
            raise ValueError('Canary target is not a regular virtual file')
        return fd
    finally:
        os.close(parent)


def check_probe(root,probe):
    path=probe['ids_path']
    if not path.startswith('/.ids/') or len(path)>256 or probe['size']!=16:
        raise ValueError('Invalid process-bound mount challenge')
    parts(path.lstrip('/'))
    fd=open_virtual(root,path)
    try:
        if os.fstat(fd).st_size!=probe['size']:
            raise ValueError('Mount challenge size differs')
        raw=os.pread(fd,probe['size']+1,0)
        if len(raw)!=probe['size'] or digest(raw)!=probe['sha256']:
            raise ValueError('Canary mount does not match the current Rust process')
    finally:
        os.close(fd)


def sample(root,proof,extension):
    if proof['ids_path']!=ids_path(proof['current_item']) or type(proof['size']) is not int or proof['size']<=0:
        raise ValueError('Invalid current-source path or size')
    fd=open_virtual(root,proof['ids_path'])
    try:
        info=os.fstat(fd)
        if info.st_size!=proof['size']:
            raise ValueError('Canary source size differs')
        length=min(LIMIT,info.st_size)
        offsets=sorted({0,max(0,info.st_size//2-length//2),max(0,info.st_size-length)})
        readings=[]
        for offset in offsets:
            data=os.pread(fd,length,offset)
            if len(data)!=length:
                raise ValueError('Canary source read is incomplete')
            if offset==0:
                ext=extension.lower()
                if ext in ('.mkv','.webm') and not data.startswith(b'\x1aE\xdf\xa3'):
                    raise ValueError('Canary media header is invalid')
                if ext in ('.mp4','.m4v','.mov') and data[4:8] not in (b'ftyp',b'moov',b'mdat',b'wide',b'free',b'skip'):
                    raise ValueError('Canary media header is invalid')
                if ext=='.avi' and not (data.startswith(b'RIFF') and data[8:12]==b'AVI '):
                    raise ValueError('Canary media header is invalid')
            readings.append({'offset':offset,'length':length,'sha256':digest(data)})
        after=os.fstat(fd)
        if (after.st_dev,after.st_ino,after.st_size)!=(info.st_dev,info.st_ino,info.st_size):
            raise ValueError('Canary source changed during sampled reads')
        return {'method':'sampled_reads','bytesRead':sum(p['length'] for p in readings),'samples':readings,
                'wholeFileVerified':info.st_size<=LIMIT}
    finally:
        os.close(fd)


def contained(a,b):
    return a==b or a in b.parents


def plan(original_root,mount_root,destination,bindings,scope):
    original=Path(original_root).resolve(strict=True)
    mount=Path(mount_root).resolve(strict=True)
    output=Path(destination).absolute()
    parent=output.parent.resolve(strict=True)
    output=parent/output.name
    if any(contained(protected,output) or contained(output,protected) for protected in (original,mount)):
        raise ValueError('Canary destination overlaps the original library or source mount')
    if output in (Path('/'),Path('/etc'),Path('/usr'),Path('/var'),Path('/home'),Path('/opt')):
        raise ValueError('Canary destination must be a separate owned directory')
    source=root_fd(original)
    try:
        rows=[]
        seen=set()
        for binding in bindings:
            link=binding['link'];proof=binding['proof']
            relative=link['libraryRelativePath'];parts(relative)
            if relative in seen or proof['current_item']!=link['legacyDavItemId'] or proof['ids_path']!=ids_path(proof['current_item']):
                raise ValueError('Canary plan has ambiguous source bindings')
            seen.add(relative)
            witness=source_link(source,relative,link['originalTarget'])
            rows.append({'relative':relative,'target':str(mount)+proof['ids_path'],'original':witness,
                         'proof':{k:v for k,v in proof.items() if k!='observed_at'},'sampledValidation':binding['sampledValidation']})
        payload={'version':1,'scope':scope,'originalRoot':str(original),'mountRoot':str(mount),
                 'destination':str(output),'parentDevice':parent.stat().st_dev,'parentInode':parent.stat().st_ino,'originalDevice':os.fstat(source).st_dev,'originalInode':os.fstat(source).st_ino,
                 'links':rows}
        return dict(payload,planDigest=digest(canonical(payload)))
    finally:
        os.close(source)


def check_plan(value):
    payload={k:v for k,v in value.items() if k!='planDigest'}
    if value.get('version')!=1 or value.get('planDigest')!=digest(canonical(payload)):
        raise ValueError('Canary plan digest differs')


def prepare_root(value,create=True):
    check_plan(value)
    destination=Path(value['destination'])
    parent=root_fd(destination.parent)
    try:
        info=os.fstat(parent)
        if (info.st_dev,info.st_ino)!=(value['parentDevice'],value['parentInode']):
            raise ValueError('Canary parent directory changed')
        fresh=False
        if create:
            try:
                os.mkdir(destination.name,0o700,dir_fd=parent);os.fsync(parent);fresh=True
            except FileExistsError:
                pass
        fd=os.open(destination.name,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW,dir_fd=parent)
        try:
            info=os.fstat(fd)
            if info.st_mode&0o077 or info.st_uid!=os.geteuid():
                raise ValueError('Canary directory must remain owner-only')
            marker={'version':1,'planDigest':value['planDigest'],'device':info.st_dev,'inode':info.st_ino}
            if fresh:
                out=os.open(MARKER,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600,dir_fd=fd)
                with os.fdopen(out,'wb') as stream:
                    stream.write(canonical(marker));stream.flush();os.fsync(stream.fileno())
                os.fsync(fd)
            else:
                markerfd=os.open(MARKER,os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK,dir_fd=fd)
                with os.fdopen(markerfd,'rb') as stream:
                    state=os.fstat(stream.fileno())
                    if not stat.S_ISREG(state.st_mode) or state.st_nlink!=1 or state.st_size>4096 or state.st_mode&0o077 or json.loads(stream.read(4097))!=marker:
                        raise ValueError('Canary directory belongs to a different plan')
            return fd
        except BaseException:
            os.close(fd)
            raise
    finally:
        os.close(parent)


def apply_canary(value,journal,recheck):
    """Create new canary links only. Recheck returns a fresh, identical source proof."""
    check_plan(value)
    original=root_fd(value['originalRoot'])
    destination=prepare_root(value)
    try:
        info=os.fstat(original)
        if (info.st_dev,info.st_ino)!=(value['originalDevice'],value['originalInode']):
            raise ValueError('Original library root changed')
        created=0
        for row in value['links']:
            if source_link(original,row['relative'],row['original']['target'])!=row['original']:
                raise ValueError('Original library witness changed')
            recheck(row['proof'])
            key=digest(canonical([value['planDigest'],row['relative']]))
            parent,name=parent_fd(destination,row['relative'],create=True)
            try:
                try:
                    info=os.stat(name,dir_fd=parent,follow_symlinks=False)
                except FileNotFoundError:
                    info=None
                if info is not None:
                    previous=journal.latest.get(key,{})
                    if not stat.S_ISLNK(info.st_mode) or os.readlink(name,dir_fd=parent)!=row['target'] or previous.get('state') not in ('link_intent','link_created'):
                        raise ValueError('Canary entry changed or is not owned by this plan')
                else:
                    journal.append(key,'link_intent',{'planDigest':value['planDigest'],'relative':row['relative'],'target':row['target']})
                    os.symlink(row['target'],name,dir_fd=parent);os.fsync(parent);created+=1
                info=os.stat(name,dir_fd=parent,follow_symlinks=False)
                journal.append(key,'link_created',{'planDigest':value['planDigest'],'relative':row['relative'],'target':row['target'],'device':info.st_dev,'inode':info.st_ino})
            finally:
                os.close(parent)
            try:
                recheck(row['proof'])
            except BaseException:
                rollback_canary(value,journal)
                raise
        return created
    finally:
        os.close(original);os.close(destination)


def rollback_canary(value,journal):
    check_plan(value)
    destination=prepare_root(value,create=False)
    removed=0
    try:
        for row in reversed(value['links']):
            key=digest(canonical([value['planDigest'],row['relative']]))
            previous=journal.latest.get(key,{})
            if previous.get('state') not in ('link_intent','link_created','rollback_intent'):
                continue
            try:
                parent,name=parent_fd(destination,row['relative'])
            except FileNotFoundError:
                continue
            try:
                try:
                    info=os.stat(name,dir_fd=parent,follow_symlinks=False)
                except FileNotFoundError:
                    continue
                receipt=previous.get('receipt',{})
                if not stat.S_ISLNK(info.st_mode) or os.readlink(name,dir_fd=parent)!=row['target']:
                    continue
                if previous['state'] in ('link_created','rollback_intent') and (info.st_dev,info.st_ino)!=(receipt.get('device'),receipt.get('inode')):
                    continue
                journal.append(key,'rollback_intent',receipt)
                os.unlink(name,dir_fd=parent);os.fsync(parent);removed+=1
                journal.append(key,'rolled_back',receipt)
            finally:
                os.close(parent)
        return removed
    finally:
        os.close(destination)
