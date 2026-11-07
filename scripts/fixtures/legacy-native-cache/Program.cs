using System.Security.Cryptography;
using System.Text.Json;
using MemoryPack;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services.NativeCache;
using ZstdSharp;

if (args.Length == 2 && args[0] == "verify-eviction") {
    using var receipt = JsonDocument.Parse(File.ReadAllBytes(args[1]));
    var cases = 0;
    foreach (var row in receipt.RootElement.EnumerateArray()) {
        var fixtureRoot = Path.GetFullPath(row.GetProperty("fixture_root").GetString()!);
        if (!Path.GetFileName(fixtureRoot).StartsWith("id-compatible-cache-", StringComparison.Ordinal)
            || File.ReadAllText(Path.Combine(fixtureRoot,"fixture-owner")) != "generated-native-cache-write-tests") throw new Exception("Not an owned generated fixture.");
        var metadata = Path.GetFullPath(row.GetProperty("metadata_path").GetString()!);
        var payload = Path.GetFullPath(row.GetProperty("folder_path").GetString()!);
        if (!metadata.StartsWith(fixtureRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !payload.StartsWith(fixtureRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new Exception("Fixture path escaped its owned root.");
        var folder = new NativeCacheFolder { Id=row.GetProperty("folder_id").GetString()!,Path=payload,MinFreeBytes=0,MaxBytes=1_000_000_000 };
        var generation = row.GetProperty("generation").GetString()!;
        var length = row.GetProperty("length").GetInt64();
        var removed = new NativeCacheIdentity(row.GetProperty("removed").GetString()!,generation,length);
        var remaining = new NativeCacheIdentity(row.GetProperty("remaining").GetString()!,generation,length);
        var database = Path.Combine(metadata,"catalogue.db");
        await using (var store = new NativeCacheStore(database,[folder])) {
            var buffer = new byte[NativeCacheStore.BlockSize];
            if (await store.ReadBlockAsync(removed,0,buffer) != 0) throw new Exception("A staged eviction was still advertised.");
            await store.ScanAsync(folder.Id);
            if (await store.ReadBlockAsync(removed,0,buffer) != 0) throw new Exception("Original C# scanner resurrected a staged eviction.");
            var count = await store.ReadBlockAsync(remaining,0,buffer);
            if (count != buffer.Length || !string.Equals(Convert.ToHexString(SHA256.HashData(buffer)),row.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase)) throw new Exception("Eviction changed an unrelated cached entry.");
        }
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}")) {
            db.Open();using var command=db.CreateCommand();
            command.CommandText="SELECT COUNT(*) FROM RetiredEntries WHERE Key=$key";
            command.Parameters.AddWithValue("$key",removed.Key);
            if ((long)command.ExecuteScalar()! != 1) throw new Exception("Staged ownership was lost.");
        }
        cases++;
    }
    Console.WriteLine(JsonSerializer.Serialize(new { passed=true,cases,reader="original C#",eviction_resurrection=false,unrelated_bytes_unchanged=true }));
    return;
}

if (args.Length == 2 && args[0] == "verify-rust") {
    using var receipt = JsonDocument.Parse(File.ReadAllBytes(args[1]));
    var checkedCases = 0;
    foreach (var row in receipt.RootElement.EnumerateArray()) {
        var fixtureRoot = Path.GetFullPath(row.GetProperty("fixture_root").GetString()!);
        if (!Path.GetFileName(fixtureRoot).StartsWith("id-compatible-cache-", StringComparison.Ordinal)
            || File.ReadAllText(Path.Combine(fixtureRoot,"fixture-owner")) != "generated-native-cache-write-tests") throw new Exception("Not an owned generated fixture.");
        var metadata = Path.GetFullPath(row.GetProperty("metadata_path").GetString()!);
        var payload = Path.GetFullPath(row.GetProperty("folder_path").GetString()!);
        if (!metadata.StartsWith(fixtureRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !payload.StartsWith(fixtureRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new Exception("Fixture path escaped its owned root.");
        var folder = new NativeCacheFolder { Id=row.GetProperty("folder_id").GetString()!,Path=payload,MinFreeBytes=0,MaxBytes=1_000_000_000 };
        var identity = new NativeCacheIdentity(row.GetProperty("item").GetString()!,row.GetProperty("generation").GetString()!,row.GetProperty("length").GetInt64());
        var database = Path.Combine(metadata,"catalogue.db");
        async Task Verify(NativeCacheStore store) {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[NativeCacheStore.BlockSize];
            for (long offset=0;offset<identity.Length;offset+=buffer.Length) {
                var expected=(int)Math.Min(buffer.Length,identity.Length-offset);
                var count=await store.ReadBlockAsync(identity,offset,buffer.AsMemory(0,expected));
                if (count!=expected) throw new Exception("Original C# reader could not read a Rust-written committed block.");
                digest.AppendData(buffer,0,count);
            }
            if (!string.Equals(Convert.ToHexString(digest.GetHashAndReset()),row.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase)) throw new Exception("Rust-written bytes differ in the original C# reader.");
        }
        await using (var store = new NativeCacheStore(database,[folder])) { await Verify(store); }
        // Destroy only disposable catalogue coverage, then prove the original C# scanner
        // reconstructs it from Rust-written manifests and journals without changing payloads.
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}")) {
            db.Open();using var command=db.CreateCommand();command.CommandText="DELETE FROM Blocks; UPDATE Entries SET Dirty=1,PendingBytes=0";command.ExecuteNonQuery();
        }
        await using (var store = new NativeCacheStore(database,[folder])) { await store.ScanAsync(folder.Id); await Verify(store); }
        checkedCases++;
    }
    Console.WriteLine(JsonSerializer.Serialize(new { passed=true,cases=checkedCases,reader="original C#",journal_reconstruction=true }));
    return;
}

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
