using CryBar;
using System.IO;
using Avalonia.Controls;
using System.Collections.Generic;
using System.Text;
using System;
using System.Diagnostics;

namespace CryBarEditor;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        using var file = File.OpenRead(@"C:\Program Files (x86)\Steam\steamapps\common\Age of Mythology Retold\game\modelcache\ArtModelCacheModelDataGreek.bar");
        var bar = new BarFile(file);

        if (bar.Load())
        {
            foreach (var e in bar.Entries)
            {
                //var d = e.ReadDataRaw(file);

                var data = e.ReadDataDecompressed(file);
                var text = "";
                if (e.IsXMB)
                {
                    text = BarFileEntry.ConvertXMBtoXML(data.Span)!.InnerXml;
                }
                else if (e.IsText)
                {
                    text = Encoding.UTF8.GetString(data.Span);
                }

                if (text.Length > 0)
                {
                    var idx = text.IndexOf(" kb");
                    if (idx >= 0)
                    {
                        Debug.WriteLine(e.RelativePath + " --> " + text[idx..(idx + 20)]);
                    }
                }
            }
        }
    }
}