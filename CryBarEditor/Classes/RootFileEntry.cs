using System.IO;
using System.Collections.Generic;

namespace CryBarEditor.Classes;

public class RootFileEntry
{
    public string Name { get; }
    public string DirectoryPath { get; }
    public string RelativePath { get; }  
    public string Extension { get; }
    public List<RootFileEntry> Children { get; } = new();
    public bool IsBAR => Extension is ".BAR";

    public RootFileEntry(string root_dir, string file_path)
    {
        Name = Path.GetFileName(file_path);
        RelativePath = Path.GetRelativePath(root_dir, file_path);

        DirectoryPath = Path.GetDirectoryName(RelativePath) ?? "";
        if (DirectoryPath.Length > 0) DirectoryPath += "\\";

        Extension = Path.GetExtension(file_path).ToUpper();
    }

    public override string ToString() => RelativePath;
    
}
