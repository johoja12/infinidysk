#!/usr/bin/env python3
"""Build an isolated fork trial image and write an exact-source local receipt.

No push, deployment or mutable release tags. --plan performs read-only checks.
"""
import argparse
import hashlib
import json
import pathlib
import re
import subprocess
import tarfile
import tempfile


def run(*args, cwd=None):
    return subprocess.check_output(args, cwd=cwd, text=True).strip()


def submodules(root, commit, prefix=pathlib.Path()):
    entries = subprocess.check_output(['git', 'ls-tree', '-rz', commit], cwd=root).split(b'\0')
    found = []
    for entry in filter(None, entries):
        metadata, name = entry.split(b'\t', 1)
        mode, _kind, revision = metadata.decode('ascii').split()
        if mode != '160000':
            continue
        relative = pathlib.Path(name.decode('utf-8'))
        if relative.is_absolute() or '..' in relative.parts:
            raise RuntimeError('invalid submodule path')
        source = (root / relative).resolve()
        if not source.is_relative_to(root) or pathlib.Path(run('git', 'rev-parse', '--show-toplevel', cwd=source)).resolve() != source:
            raise RuntimeError('submodule must be initialized inside its source checkout')
        if run('git', 'rev-parse', 'HEAD', cwd=source) != revision or run('git', 'status', '--porcelain', '--untracked-files=all', cwd=source):
            raise RuntimeError('submodule must be clean and checked out at its pinned commit')
        found.append((prefix / relative, source, revision))
        found.extend(submodules(source, revision, prefix / relative))
    return found


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--image', help='Fork GHCR image ending in :sha-<12 commit chars>')
    parser.add_argument('--repository', default='johoja12/infinidysk')
    parser.add_argument('--receipt', type=pathlib.Path)
    parser.add_argument('--plan', action='store_true')
    args = parser.parse_args()
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]*/[A-Za-z0-9][A-Za-z0-9_.-]*', args.repository):
        parser.error('repository must be owner/repository')
    if args.repository.lower().startswith(('infinidysk/', 'nzbdav/')):
        parser.error('fork builder refuses upstream repository identities')
    root = pathlib.Path(__file__).resolve().parents[2]
    root = pathlib.Path(run('git', 'rev-parse', '--show-toplevel', cwd=root)).resolve()
    commit = run('git', 'rev-parse', 'HEAD', cwd=root)
    if run('git', 'status', '--porcelain', '--untracked-files=all', cwd=root):
        parser.error('commit or preserve local changes before building an exact-source image')
    modules = submodules(root, commit)
    owner = args.repository.split('/')[0].lower()
    image = args.image or f'ghcr.io/{owner}/infinidysk-rust-trial:sha-{commit[:12]}'
    if not re.fullmatch(r'ghcr\.io/' + re.escape(owner) + r'/[a-z0-9][a-z0-9._/-]*:sha-' + commit[:12], image):
        parser.error('image must use the fork GHCR owner and this commit-specific tag')
    receipt = {
        'schema': 1, 'source_repository': args.repository, 'source_commit': commit,
        'image': image, 'submodules': [{'path': str(path), 'commit': revision} for path, _source, revision in modules], 'scope': 'local trial image; no push or deployment',
    }
    if args.plan:
        print(json.dumps(receipt, indent=2))
        return
    if args.receipt is None:
        parser.error('--receipt is required for a build')
    receipt_path = args.receipt.resolve()
    if receipt_path.is_relative_to(root):
        parser.error('receipt must be outside the source checkout')
    if receipt_path.exists():
        parser.error('receipt already exists; choose a new path')
    with tempfile.TemporaryDirectory(prefix='id-rust-fork-image-') as temp:
        temp = pathlib.Path(temp)
        archive = temp / 'source.tar'
        subprocess.run(['git', 'archive', '--format=tar', '--output', str(archive), commit], cwd=root, check=True)
        with archive.open('rb') as source_bytes:
            receipt['source_archive_sha256'] = hashlib.file_digest(source_bytes, 'sha256').hexdigest()
        context = temp / 'context'
        context.mkdir()
        with tarfile.open(archive) as source:
            source.extractall(context, filter='data')
        for index, (path, source_root, revision) in enumerate(modules):
            module_archive = temp / f'module-{index}.tar'
            subprocess.run(['git', 'archive', '--format=tar', '--output', str(module_archive), revision], cwd=source_root, check=True)
            with module_archive.open('rb') as source_bytes:
                receipt['submodules'][index]['archive_sha256'] = hashlib.file_digest(source_bytes, 'sha256').hexdigest()
            destination = context / path
            destination.mkdir(parents=True, exist_ok=True)
            with tarfile.open(module_archive) as source:
                source.extractall(destination, filter='data')
        subprocess.run(['docker', 'build', '--pull', '--build-arg', f'COMMIT={commit}',
                        '--build-arg', f'SOURCE_REPOSITORY={args.repository}',
                        '--tag', image, str(context)], check=True)
        inspected = json.loads(run('docker', 'image', 'inspect', image))[0]
        labels = inspected['Config'].get('Labels') or {}
        if labels.get('org.opencontainers.image.revision') != commit or labels.get('org.opencontainers.image.source') != f'https://github.com/{args.repository}':
            raise RuntimeError('image source labels do not match the requested source')
        receipt.update(image_id=inspected['Id'], repo_digests=inspected.get('RepoDigests', []),
                       architecture=inspected['Architecture'], os=inspected['Os'])
    # Exclusive creation preserves earlier receipts; no acknowledgement on failure.
    with receipt_path.open('x') as output:
        json.dump(receipt, output, indent=2)
        output.write('\n')
    print(json.dumps(receipt, indent=2))


if __name__ == '__main__':
    main()
