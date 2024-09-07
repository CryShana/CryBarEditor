using System.Collections.Generic;
using System.IO;

namespace CryBarEditor.Classes;

public class FileEntry
{
    public string RelativePath { get; }  
    public string Extension { get; }
    public List<FileEntry> Children { get; } = new();

    public FileEntry(string root_dir, string file_path)
    {
        RelativePath = Path.GetRelativePath(root_dir, file_path);
        Extension = Path.GetExtension(file_path).ToUpper();
    }

    public override string ToString() => RelativePath;
    
}
