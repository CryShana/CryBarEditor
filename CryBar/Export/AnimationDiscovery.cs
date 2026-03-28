using System.Xml;

namespace CryBar.Export;

/// <summary>
/// Discovers TMA animation references from AnimXML file content.
/// </summary>
public static class AnimationDiscovery
{
    /// <summary>Parsed animation reference from an AnimXML file.</summary>
    public readonly record struct AnimRef(string AnimName, string TmaPath);

    /// <summary>
    /// Extracts animation name + TMA path pairs from AnimXML content.
    /// An anim element can contain multiple TMAnimation assetreferences (variants).
    /// Each variant becomes a separate AnimRef with the same base name.
    /// </summary>
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

                // mixed content: <anim>AnimName<assetreference type="TMAnimation"><file>path</file></assetreference>...</anim>
                using var subtree = reader.ReadSubtree();
                subtree.Read(); // move into <anim>

                string animName = "";
                var paths = new List<string>();

                while (subtree.Read())
                {
                    if (subtree.NodeType == XmlNodeType.Text && animName.Length == 0)
                    {
                        animName = subtree.Value.Trim();
                    }
                    else if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "assetreference")
                    {
                        var type = subtree.GetAttribute("type");
                        if (type == "TMAnimation" && subtree.ReadToDescendant("file"))
                            paths.Add(subtree.ReadElementContentAsString().Trim());
                    }
                }

                for (int i = 0; i < paths.Count; i++)
                    results.Add(new AnimRef(animName, paths[i]));
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
