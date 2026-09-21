using System.Text.Json;
using NzbWebDAV.UsenetMigration.Provenance;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class NzbDavLedgerEvidenceTests
{
    [Fact]
    public void WithCorrelation_PreservesVerifiedPackageEvidence()
    {
        var digest = new string('a', 64);
        var existing = JsonSerializer.Serialize(new
        {
            packageSha256 = digest,
            payloadLength = 42,
        });

        var merged = NzbDavLedgerEvidence.WithCorrelation(
            existing,
            """{"match":"article-identity","candidates":[]}""");

        using var document = JsonDocument.Parse(merged);
        Assert.Equal(digest, document.RootElement.GetProperty("packageSha256").GetString());
        Assert.Equal(42, document.RootElement.GetProperty("payloadLength").GetInt32());
        Assert.Equal(
            "article-identity",
            document.RootElement.GetProperty("correlation").GetProperty("match").GetString());
    }

    [Theory]
    [InlineData("not-json", "{}")]
    [InlineData("[]", "{}")]
    [InlineData("{}", "{}")]
    [InlineData("{\"packageSha256\":\"short\"}", "{}")]
    [InlineData("{\"packageSha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}", "{}")]
    [InlineData("{\"packageSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}", "[]")]
    public void WithCorrelation_RejectsUnverifiableEvidence(string existing, string correlation)
    {
        Assert.Throws<InvalidDataException>(() =>
            NzbDavLedgerEvidence.WithCorrelation(existing, correlation));
    }
}
