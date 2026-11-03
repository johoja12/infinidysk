using System.Security.Cryptography;
using System.Text.Json;
using MemoryPack;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services.NativeCache;
using ZstdSharp;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Provide a new absolute fixture directory.");
var root = args[0];
if (Directory.Exists(root)) throw new ArgumentException("Fixture directory must not exist.");
Directory.CreateDirectory(root);
var receipts = new List<object>();
foreach (var mode in new[] { "sparse", "chunked", "stored", "encrypted", "partial" }) {
    var location = Path.Combine(root, mode); Directory.CreateDirectory(location);
    var payload = Path.Combine(location, "payload"); Directory.CreateDirectory(payload);
    var folder = new NativeCacheFolder { Id = "fixture", Path = payload, MinFreeBytes = 0, MaxBytes = 1_000_000_000 };
    var item = Guid.NewGuid(); var blob = Guid.NewGuid();
    var data = Enumerable.Range(0, 2*NativeCacheStore.BlockSize + 37).Select(i => (byte)(i % 251)).ToArray();
    var ids = new[] { "fixture-0@invalid", "fixture-1@invalid", "fixture-2@invalid" };
    var sourceLength = mode == "encrypted" ? (data.Length + 15) / 16 * 16 : data.Length;
    var ranges = new[] { new LongRange(0, NativeCacheStore.BlockSize), new LongRange(NativeCacheStore.BlockSize,2L*NativeCacheStore.BlockSize), new LongRange(2L*NativeCacheStore.BlockSize,sourceLength) };
    byte[] serialized; string version; string content;
    var multipart = mode is "stored" or "encrypted";
    if (multipart) {
        var meta = new DavMultipartFile.Meta {
            PathInArchive = "member.dat", ExpectedFileSize = data.Length,
            FileParts = [new() { SegmentIds = ids, SegmentIdByteRange = new(0,sourceLength), FilePartByteRange = new(0,sourceLength), SegmentByteRanges = ranges, SegmentByteRangesTrusted = true }],
            AesParams = mode == "encrypted" ? new() { Key = Enumerable.Repeat((byte)9,32).ToArray(), Iv = Enumerable.Repeat((byte)3,16).ToArray(), DecodedSize = data.Length } : null
        };
        content = MultipartContentIdentity.Compute(meta); version = "v3";
        // The persisted identity is part of the current format and survives lazy rewrites.
        meta.ContentIdentity = content;
        serialized = MemoryPackSerializer.Serialize(new DavMultipartFile { Id = item, Metadata = meta });
    } else {
        serialized = MemoryPackSerializer.Serialize(new DavNzbFile { Id = item, SegmentIds = ids, SegmentByteRanges = ranges, SegmentByteRangesTrusted = true });
        version = "v2"; content = "";
    }
    using var compressor = new Compressor();
    var compressed = compressor.Wrap(serialized).ToArray();
    File.WriteAllBytes(Path.Combine(location,"source.blob"), compressed);
    if (!multipart) content = Convert.ToHexString(SHA256.HashData(compressed));
    string revision;
    using (var ledger = new RepairRevisionStore(Path.Combine(location,"repair-revisions.db"))) {
        using (ledger.BeginPublication([RepairRevisionStore.HashSegmentId(ids[1])])) { }
        revision = ledger.Capture(ids).Fingerprint;
    }
    var generation = $"{version}:{blob:N}:{content}:{revision}";
    var identity = new NativeCacheIdentity(item.ToString("N"),generation,data.Length);
    await using (var store = new NativeCacheStore(Path.Combine(location,"catalogue.db"),[folder], mode == "sparse" ? 0 : 8)) {
        for (var offset=0;offset<data.Length;offset+=NativeCacheStore.BlockSize) {
            if (mode == "partial" && offset == NativeCacheStore.BlockSize) continue;
            if (!await store.WriteBlockAsync(identity,offset,data.AsMemory(offset,Math.Min(NativeCacheStore.BlockSize,data.Length-offset)))) throw new Exception("Fixture write rejected.");
        }
        await store.SetPinnedAsync(identity,true);
    }
    receipts.Add(new { mode, item=item.ToString("N"), blob=blob.ToString("N"), generation, length=data.Length,
        sha256=Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), metadata_path=location, folder_id=folder.Id, folder_path=folder.Path, multipart, content });
}
File.WriteAllText(Path.Combine(root,"receipt.json"),JsonSerializer.Serialize(receipts));
Console.WriteLine(Path.Combine(root,"receipt.json"));
