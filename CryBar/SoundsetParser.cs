using System.Globalization;
using System.Xml;

namespace CryBar;

/// <summary>
/// Parses soundset definition XML files and sound manifest XML files.
/// </summary>
public static class SoundsetParser
{
    /// <summary>
    /// Parses a soundset definition XML (from soundsets_*.soundset or .soundset.XMB files).
    /// Returns all soundset definitions found in the file.
    /// Only parses soundset elements that are direct children of soundsetdef.
    /// </summary>
    public static List<SoundsetDefinition> ParseSoundsetXml(string xml)
    {
        var result = new List<SoundsetDefinition>();
        using var reader = XmlReader.Create(new StringReader(xml));

        bool inSoundsetDef = false;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "soundsetdef")
                {
                    inSoundsetDef = true;
                }
                else if (reader.Name == "soundset" && inSoundsetDef)
                {
                    var soundset = ReadSoundsetElement(reader);
                    if (soundset != null)
                        result.Add(soundset);
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "soundsetdef")
            {
                inSoundsetDef = false;
            }
        }

        return result;
    }

    static SoundsetDefinition? ReadSoundsetElement(XmlReader reader)
    {
        var name = reader.GetAttribute("name");
        if (string.IsNullOrEmpty(name)) return null;

        var volumeAttr = reader.GetAttribute("volume");
        float? volume = volumeAttr != null && float.TryParse(volumeAttr, CultureInfo.InvariantCulture, out var v) ? v : null;

        var maxNumAttr = reader.GetAttribute("maxnum");
        int? maxNum = maxNumAttr != null && int.TryParse(maxNumAttr, out var mn) ? mn : null;

        var sounds = new List<SoundsetSound>();

        // If self-closing <soundset .../>, no children to read
        if (reader.IsEmptyElement)
        {
            return sounds.Count > 0 ? new SoundsetDefinition { Name = name, Volume = volume, MaxNum = maxNum, Sounds = sounds } : null;
        }

        // Read child <sound> elements until </soundset>
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "sound")
            {
                var filename = reader.GetAttribute("filename");
                if (!string.IsNullOrEmpty(filename))
                {
                    var sVolAttr = reader.GetAttribute("volume");
                    float? sVol = sVolAttr != null && float.TryParse(sVolAttr, CultureInfo.InvariantCulture, out var sv) ? sv : null;

                    var sWeightAttr = reader.GetAttribute("weight");
                    float? sWeight = sWeightAttr != null && float.TryParse(sWeightAttr, CultureInfo.InvariantCulture, out var sw) ? sw : null;

                    sounds.Add(new SoundsetSound
                    {
                        Filename = filename,
                        Volume = sVol,
                        Weight = sWeight,
                    });
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "soundset")
            {
                break;
            }
        }

        if (sounds.Count == 0) return null;

        return new SoundsetDefinition
        {
            Name = name,
            Volume = volume,
            MaxNum = maxNum,
            Sounds = sounds,
        };
    }

    /// <summary>
    /// Finds a soundset by name from a parsed list of definitions.
    /// </summary>
    public static SoundsetDefinition? FindSoundset(List<SoundsetDefinition> definitions, string soundsetName)
    {
        return definitions.FirstOrDefault(d =>
            d.Name.Equals(soundsetName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses the soundmanifest XML and returns a lookup by filename (case-insensitive).
    /// </summary>
    public static Dictionary<string, SoundManifestEntry> ParseSoundManifest(string xml)
    {
        var result = new Dictionary<string, SoundManifestEntry>(StringComparer.OrdinalIgnoreCase);
        using var reader = XmlReader.Create(new StringReader(xml));

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "soundfile")
            {
                var entry = ReadSoundfileElement(reader);
                if (entry != null)
                    result[entry.Filename] = entry;
            }
        }

        return result;
    }

    static SoundManifestEntry? ReadSoundfileElement(XmlReader reader)
    {
        if (reader.IsEmptyElement) return null;

        string? filename = null;
        double? length = null;
        int? numSamples = null;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                var elemName = reader.Name;
                if (!reader.IsEmptyElement)
                {
                    var text = reader.ReadElementContentAsString().Trim();
                    switch (elemName)
                    {
                        case "filename":
                            filename = text;
                            break;
                        case "length":
                            if (double.TryParse(text, CultureInfo.InvariantCulture, out var l))
                                length = l;
                            break;
                        case "numsamples":
                            if (int.TryParse(text, out var ns))
                                numSamples = ns;
                            break;
                    }
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "soundfile")
            {
                break;
            }
        }

        if (string.IsNullOrEmpty(filename)) return null;

        return new SoundManifestEntry
        {
            Filename = filename,
            Length = length,
            NumSamples = numSamples,
        };
    }

    /// <summary>
    /// Enriches soundset sounds with data from the sound manifest.
    /// </summary>
    public static bool EnrichWithManifest(SoundsetDefinition soundset, Dictionary<string, SoundManifestEntry> manifest)
    {
        bool anyEnriched = false;
        foreach (var sound in soundset.Sounds)
        {
            // Try exact match, then try with normalized separators
            var key = sound.Filename;
            if (!manifest.TryGetValue(key, out var entry))
            {
                key = key.Replace('/', '\\');
                if (!manifest.TryGetValue(key, out entry))
                {
                    key = key.Replace('\\', '/');
                    manifest.TryGetValue(key, out entry);
                }
            }

            if (entry != null)
            {
                sound.Length = entry.Length;
                sound.NumSamples = entry.NumSamples;
                anyEnriched = true;
            }
        }
        return anyEnriched;
    }

    /// <summary>
    /// Extracts the event name (last path segment) from an FMOD event path.
    /// e.g. "event:/Shared/Shared VO/DeathFemale" → "DeathFemale"
    /// </summary>
    public static string? ExtractEventName(string eventPath)
    {
        if (string.IsNullOrEmpty(eventPath)) return null;

        var lastSlash = eventPath.LastIndexOf('/');
        if (lastSlash < 0 || lastSlash >= eventPath.Length - 1) return null;

        return eventPath[(lastSlash + 1)..];
    }

    /// <summary>
    /// Extracts the bank name (first path segment after "event:/") from an FMOD event path.
    /// e.g. "event:/Shared/Shared VO/DeathFemale" → "Shared"
    /// </summary>
    public static string? ExtractBankName(string eventPath)
    {
        if (string.IsNullOrEmpty(eventPath)) return null;

        const string prefix = "event:/";
        if (!eventPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = eventPath[prefix.Length..];
        var nextSlash = rest.IndexOf('/');
        if (nextSlash <= 0) return rest.Length > 0 ? rest : null;

        return rest[..nextSlash];
    }
}

/// <summary>
/// A single entry from the soundmanifest.xml file.
/// </summary>
public class SoundManifestEntry
{
    public required string Filename { get; init; }
    public double? Length { get; init; }
    public int? NumSamples { get; init; }
}
