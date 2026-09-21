using System.Text.Json;
using System.Text.Json.Nodes;

namespace NzbWebDAV.UsenetMigration.Provenance;

internal static class NzbDavLedgerEvidence
{
    internal static string WithCorrelation(string? existingFlags, string correlationEvidence)
    {
        JsonObject existing;
        JsonObject correlation;
        try
        {
            existing = JsonNode.Parse(existingFlags ?? string.Empty) as JsonObject
                       ?? throw new InvalidDataException("NzbDav package evidence must be a JSON object.");
            correlation = JsonNode.Parse(correlationEvidence) as JsonObject
                          ?? throw new InvalidDataException("NzbDav correlation evidence must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("NzbDav ledger evidence contains invalid JSON.", exception);
        }

        if (existing["packageSha256"] is not JsonValue digestValue
            || !digestValue.TryGetValue<string>(out var digest)
            || !IsSha256(digest))
            throw new InvalidDataException("NzbDav ledger evidence lacks a verified package digest.");

        existing["correlation"] = correlation.DeepClone();
        return existing.ToJsonString();
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
