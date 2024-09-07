using System.Collections.Generic;
using System.IO;

namespace CryBarEditor.Classes;

public class FileEntry
{
    public string RelativePath { get; }  
    public string Extension { get; }
    public List<FileEntry> Children { get; } = new();
    public bool IsBAR => Extension is ".BAR";
    public bool IsXML => Extension is ".XML" or ".XMB" or ".XAML";
    public bool IsXS => Extension is ".XS";

    public FileEntry(string root_dir, string file_path)
    {
        RelativePath = Path.GetRelativePath(root_dir, file_path);
        Extension = Path.GetExtension(file_path).ToUpper();
    }

    public override string ToString() => RelativePath;
    
}
