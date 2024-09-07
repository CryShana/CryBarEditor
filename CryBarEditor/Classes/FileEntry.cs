using System.Collections.Generic;
using System.IO;

namespace CryBarEditor.Classes;

public class FileEntry
{
    public string Name { get; }
    public string DirectoryPath { get; }
    public string RelativePath { get; }  
    public string Extension { get; }
    public List<FileEntry> Children { get; } = new();
    public bool IsBAR => Extension is ".BAR";

    public FileEntry(string root_dir, string file_path)
    {
        Name = Path.GetFileName(file_path);
        RelativePath = Path.GetRelativePath(root_dir, file_path);
        DirectoryPath = (Path.GetDirectoryName(RelativePath) + "\\") ?? "";
        Extension = Path.GetExtension(file_path).ToUpper();
    }

    public override string ToString() => RelativePath;
    
}
