using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CryBarEditor.Classes;

public static class AdditiveModHelper
{
    public static Dictionary<string, AdditiveModFormat> SupportedFilesForAdditiveMod = new()
    {
        { "proto.xml", new("proto_mods.xml", "<protomods>\n</protomods>") },
        { "techtree.xml", new("techtree_mods.xml", "<techtreemods>\n</techtreemods>") },
    };
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