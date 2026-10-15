using System.Xml;
using System.Xml.Linq;

namespace RSM.Parsers.Xml;

/// <summary>
/// The only way RSM turns server-supplied XML (showplans, deadlock graphs, ring buffers) into a document: DTDs are
/// prohibited, no external resolver, and the size is capped — so a hostile or huge payload cannot expand entities,
/// reach the network or exhaust memory. <c>XDocument.Parse/Load(string)</c> are banned elsewhere.
/// </summary>
public static class SafeXml
{
    public const long DefaultMaxCharacters = 32L * 1024 * 1024;

    public static XDocument Parse(string text, long maxCharacters = DefaultMaxCharacters)
    {
        ArgumentNullException.ThrowIfNull(text);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using var stringReader = new StringReader(text);
        using var reader = XmlReader.Create(stringReader, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    /// <summary>Like <see cref="Parse"/> but returns null for malformed or oversized input instead of throwing.</summary>
    public static XDocument? TryParse(string? text, long maxCharacters = DefaultMaxCharacters)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return Parse(text, maxCharacters);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
