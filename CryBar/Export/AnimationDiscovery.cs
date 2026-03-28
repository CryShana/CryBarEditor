using System.Xml;

namespace CryBar.Export;

/// <summary>
/// Discovers TMA animation references from AnimXML file content.
/// </summary>
public static class AnimationDiscovery
{
    /// <summary>Parsed animation reference from an AnimXML file.</summary>
    public readonly record struct AnimRef(string AnimName, string TmaPath);

    /// <summary>Extracts animation name + TMA path pairs from AnimXML content.</summary>
    public static List<AnimRef> FindAnimationsFromAnimXml(string xmlContent)
    {
        var results = new List<AnimRef>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(xmlContent));
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "anim")
                    continue;

                // mixed content: <anim>AnimName<assetreference type="TMAnimation"><file>path</file></assetreference></anim>
                string animName = "";
                string? tmaPath = null;

                if (!reader.Read()) break;

                // first child should be the animation name as text
                if (reader.NodeType == XmlNodeType.Text)
                {
                    animName = reader.Value.Trim();
                    reader.Read();
                }

                // then <assetreference type="TMAnimation">
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "assetreference")
                {
                    var type = reader.GetAttribute("type");
                    if (type == "TMAnimation")
                    {
                        // descend to <file>
                        if (reader.ReadToDescendant("file"))
                            tmaPath = reader.ReadElementContentAsString().Trim();
                    }
                }

                if (tmaPath != null)
                    results.Add(new AnimRef(animName, tmaPath));
            }
        }
        catch (XmlException)
        {
            // malformed XML, return what we have
        }
        return results;
    }

    /// <summary>
    /// Extracts ALL TMModel paths from an AnimXML file.
    /// Real animfiles nest assetreferences deep inside logic/tech/cinematic elements,
    /// so this searches the entire document for any assetreference type="TMModel".
    /// </summary>
    public static List<string> FindAllModelsFromAnimXml(string xmlContent)
    {
        var results = new List<string>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(xmlContent));
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "assetreference")
                    continue;

                var type = reader.GetAttribute("type");
                if (type != "TMModel") continue;

                if (reader.ReadToDescendant("file"))
                {
                    var path = reader.ReadElementContentAsString().Trim();
                    if (path.Length > 0)
                        results.Add(path);
                }
            }
        }
        catch (XmlException) { }
        return results;
    }

    /// <summary>Extracts the first TMModel path from an AnimXML file.</summary>
    public static string? FindModelFromAnimXml(string xmlContent)
    {
        var all = FindAllModelsFromAnimXml(xmlContent);
        return all.Count > 0 ? all[0] : null;
    }
}
