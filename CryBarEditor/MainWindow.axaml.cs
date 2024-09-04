using CryBar;
using System.IO;
using Avalonia.Controls;
using System.Collections.Generic;
using System.Text;

namespace CryBarEditor;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        using var file = File.OpenRead(@"C:\Program Files (x86)\Steam\steamapps\common\Age of Mythology Retold\game\data\Data.bar");
        var bar = new BarFile(file);

        if (bar.Load())
        {
            foreach (var e in bar.Entries)
            {
                var data = e.ReadDataDecompressed(file);
                if (e.IsXMB)
                {
                    var document = BarFileEntry.ConvertXMBtoXML(data.Span)!.InnerXml;
                }
                else if (e.IsTextFile)
                {
                    var text = Encoding.UTF8.GetString(data.Span);
                    // ...
                }
            }
        }
    }
}