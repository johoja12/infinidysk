using System.Xml;

namespace NzbWebDAV.Models.Nzb;

public static class NzbXmlSecurity
{
    private const string StandardPublicId = "-//newzBin//DTD NZB 1.1//EN";
    private const string StandardSystemId = "http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd";

    public static async Task ValidateAsync(
        Stream stream,
        long? maxCharactersInDocument = null,
        CancellationToken cancellationToken = default)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = null,
            MaxCharactersFromEntities = 1,
        };
        if (maxCharactersInDocument is > 0)
            settings.MaxCharactersInDocument = maxCharactersInDocument.Value;

        using var reader = XmlReader.Create(stream, settings);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.DocumentType)
                continue;

            var isStandardDeclaration =
                string.Equals(reader.Name, "nzb", StringComparison.Ordinal)
                && string.Equals(reader.GetAttribute("PUBLIC"), StandardPublicId, StringComparison.Ordinal)
                && string.Equals(reader.GetAttribute("SYSTEM"), StandardSystemId, StringComparison.Ordinal)
                && reader.Value.Length == 0;
            if (!isStandardDeclaration)
                throw new XmlException("Only the standard external NZB 1.1 DOCTYPE is allowed.");
        }
    }
}
