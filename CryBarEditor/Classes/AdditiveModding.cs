using System;
using System.IO;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace CryBarEditor.Classes;

public static class AdditiveModding
{
    public static FrozenDictionary<string, AdditiveModFormat> SupportedFilesForAdditiveMod = (new Dictionary<string, AdditiveModFormat>()
    {
        { "proto.xml", new("proto_mods.xml", "<protomods>\n</protomods>") },
        { "techtree.xml", new("techtree_mods.xml", "<techtreemods>\n</techtreemods>") },
        { "powers.xml", new("powers_mods.xml", "<powersmod>\n</powersmod>") },
        { "string_table.txt", new("stringmods.txt", "") }
        // TODO: add more here if they exist
    }).ToFrozenDictionary();

    public static bool IsSupportedFor(string? relative_path, 
        [NotNullWhen(true)] out AdditiveModFormat? format)
    {
        format = null;
        if (relative_path == null) 
            return false;

        var filename = Path.GetFileName(relative_path);
        if (filename.EndsWith(".XMB", StringComparison.OrdinalIgnoreCase))
            filename = filename[..^4];

        if (SupportedFilesForAdditiveMod.TryGetValue(filename, out format))
            return true;
        
        return false;
    }
}

public class AdditiveModFormat
{
    public string FileName { get; set; }
    public string Content { get; set; }

    public AdditiveModFormat(string name, string content)
    {
        FileName = name;
        Content = content;
    }
}