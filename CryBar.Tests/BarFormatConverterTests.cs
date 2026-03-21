using System.Text;
using System.Xml;

using CryBar;
using CryBar.Bar;

namespace CryBar.Tests;

public class BarFormatConverterTests
{
    #region XMBtoXML Tests

    [Fact]
    public void XMBtoXML_InvalidData_ReturnsNull()
    {
        byte[] invalidData = [0, 1, 2, 3, 4, 5];
        var result = BarFormatConverter.XMBtoXML(invalidData);
        Assert.Null(result);
    }

    [Fact]
    public void XMBtoXML_EmptyData_ReturnsNull()
    {
        byte[] emptyData = [];
        var result = BarFormatConverter.XMBtoXML(emptyData);
        Assert.Null(result);
    }

    [Fact]
    public void XMBtoXML_OnlyX1Header_ReturnsNull()
    {
        // "X1" header but invalid data length
        byte[] data = [88, 49, 0xFF, 0xFF, 0xFF, 0xFF];
        var result = BarFormatConverter.XMBtoXML(data);
        Assert.Null(result);
    }

    [Fact]
    public void XMBtoXML_X1HeaderWithZeroLength_ReturnsNull()
    {
        // "X1" + data_length=0 means no XR header follows
        byte[] data = [88, 49, 0, 0, 0, 0];
        var result = BarFormatConverter.XMBtoXML(data);
        Assert.Null(result);
    }

    [Fact]
    public void XMBtoXML_MissingXRMarker_ReturnsNull()
    {
        // "X1" + data_length that's valid, but no XR follows
        byte[] data = new byte[30];
        data[0] = 88; data[1] = 49; // X1
        data[2] = 20; data[3] = 0; data[4] = 0; data[5] = 0; // data_length = 20
        // No XR marker at offset 6
        data[6] = 0; data[7] = 0;

        var result = BarFormatConverter.XMBtoXML(data);
        Assert.Null(result);
    }

    #endregion

    #region XMLtoXMB Roundtrip Tests

    [Fact]
    public void XMLtoXMB_Roundtrip_SimpleDocument_NoCompression()
    {
        var xml = new XmlDocument();
        xml.LoadXml("<root><child>text</child></root>");

        var xmb = BarFormatConverter.XMLtoXMB(xml, CompressionType.None);
        var restored = BarFormatConverter.XMBtoXML(xmb.Span);

        Assert.NotNull(restored);
        Assert.NotNull(restored.DocumentElement);
        Assert.Equal("root", restored.DocumentElement.Name);

        // Use formatted XML comparison for roundtrip verification
        var original_formatted = BarFormatConverter.FormatXML(xml);
        var restored_formatted = BarFormatConverter.FormatXML(restored);
        Assert.Equal(original_formatted, restored_formatted);
    }

    [Fact]
    public void XMLtoXMB_Roundtrip_WithAttributes_NoCompression()
    {
        var xml = new XmlDocument();
        xml.LoadXml("<config version=\"2\" name=\"test\"><item id=\"1\">value</item></config>");

        var xmb = BarFormatConverter.XMLtoXMB(xml, CompressionType.None);
        var restored = BarFormatConverter.XMBtoXML(xmb.Span);

        Assert.NotNull(restored);
        Assert.NotNull(restored.DocumentElement);
        Assert.Equal("config", restored.DocumentElement.Name);
        Assert.Equal("2", restored.DocumentElement.GetAttribute("version"));
        Assert.Equal("test", restored.DocumentElement.GetAttribute("name"));

        var original_formatted = BarFormatConverter.FormatXML(xml);
        var restored_formatted = BarFormatConverter.FormatXML(restored);
        Assert.Equal(original_formatted, restored_formatted);
    }

    [Fact]
    public void XMLtoXMB_Roundtrip_MultipleChildren_NoCompression()
    {
        var xml = new XmlDocument();
        xml.LoadXml("<root><a>1</a><b>2</b><c>3</c></root>");

        var xmb = BarFormatConverter.XMLtoXMB(xml, CompressionType.None);
        var restored = BarFormatConverter.XMBtoXML(xmb.Span);

        Assert.NotNull(restored);
        Assert.NotNull(restored.DocumentElement);

        var original_formatted = BarFormatConverter.FormatXML(xml);
        var restored_formatted = BarFormatConverter.FormatXML(restored);
        Assert.Equal(original_formatted, restored_formatted);
    }

    [Fact]
    public void XMLtoXMB_Roundtrip_NestedElements_NoCompression()
    {
        var xml = new XmlDocument();
        xml.LoadXml("<root><level1><level2><level3>deep</level3></level2></level1></root>");

        var xmb = BarFormatConverter.XMLtoXMB(xml, CompressionType.None);
        var restored = BarFormatConverter.XMBtoXML(xmb.Span);

        Assert.NotNull(restored);

        var original_formatted = BarFormatConverter.FormatXML(xml);
        var restored_formatted = BarFormatConverter.FormatXML(restored);
        Assert.Equal(original_formatted, restored_formatted);
    }

    [Fact]
    public void XMLtoXMB_Roundtrip_EmptyElement_NoCompression()
    {
        var xml = new XmlDocument();
        xml.LoadXml("<root><empty /></root>");

        var xmb = BarFormatConverter.XMLtoXMB(xml, CompressionType.None);
        var restored = BarFormatConverter.XMBtoXML(xmb.Span);

        Assert.NotNull(restored);
        Assert.NotNull(restored.DocumentElement);
        Assert.Equal("root", restored.DocumentElement.Name);
        // Find the 'empty' element child (XMB may add text nodes)
        var emptyNode = restored.DocumentElement.SelectSingleNode("empty");
        Assert.NotNull(emptyNode);
        Assert.Equal("", emptyNode.InnerText);
    }

    #endregion

    #region XMLtoXMB Compression Tests

    [Fact]
    public void XMLtoXMB_WithAlz4Compression_ProducesAlz4Data()
    {
        var xml = new XmlDocument();
        xml.LoadXml("<root><child>text</child></root>");

        var xmb = BarFormatConverter.XMLtoXMB(xml, CompressionType.Alz4);

        Assert.True(xmb.Span.IsAlz4());
    }

    [Fact]
    public void XMLtoXMB_WithAlz4Compression_RoundtripWorks()
    {
        var xml = new XmlDocument();
        xml.LoadXml("<root attr=\"val\"><child>text</child></root>");

        var xmb = BarFormatConverter.XMLtoXMB(xml, CompressionType.Alz4);

        // Decompress first, then parse
        var decompressed = BarCompression.DecompressAlz4(xmb.Span);
        Assert.NotNull(decompressed);

        var restored = BarFormatConverter.XMBtoXML(decompressed);
        Assert.NotNull(restored);
        Assert.Equal("root", restored.DocumentElement!.Name);
        Assert.Equal("val", restored.DocumentElement.GetAttribute("attr"));
    }

    [Fact]
    public void XMLtoXMB_WithL33tCompression_ProducesL33tData()
    {
        var xml = new XmlDocument();
        xml.LoadXml("<root><child>text</child></root>");

        var xmb = BarFormatConverter.XMLtoXMB(xml, CompressionType.L33t);

        Assert.True(xmb.Span.IsL33t());
    }

    [Fact]
    public void XMLtoXMB_WithL33tCompression_RoundtripWorks()
    {
        var xml = new XmlDocument();
        xml.LoadXml("<root attr=\"val\"><child>text</child></root>");

        var xmb = BarFormatConverter.XMLtoXMB(xml, CompressionType.L33t);

        var decompressed = BarCompression.DecompressL33t(xmb.Span);
        Assert.NotNull(decompressed);

        var restored = BarFormatConverter.XMBtoXML(decompressed);
        Assert.NotNull(restored);
        Assert.Equal("root", restored.DocumentElement!.Name);
        Assert.Equal("val", restored.DocumentElement.GetAttribute("attr"));
    }

    [Fact]
    public void XMLtoXMB_NoCompression_StartsWithX1()
    {
        var xml = new XmlDocument();
        xml.LoadXml("<root />");

        var xmb = BarFormatConverter.XMLtoXMB(xml, CompressionType.None);
        var span = xmb.Span;

        Assert.True(span.Length >= 2);
        Assert.Equal(88, span[0]); // 'X'
        Assert.Equal(49, span[1]); // '1'
    }

    #endregion

    #region XMLtoXMB Error Tests

    [Fact]
    public void XMLtoXMB_EmptyDocument_Throws()
    {
        var xml = new XmlDocument();
        Assert.Throws<Exception>(() => BarFormatConverter.XMLtoXMB(xml));
    }

    #endregion

    #region XMBtoFormattedXmlString Tests

    [Theory]
    [InlineData("<root><child>text</child></root>")]
    [InlineData("<config version=\"2\" name=\"test\"><item id=\"1\">value</item></config>")]
    [InlineData("<root><level1><level2><level3>deep</level3></level2></level1></root>")]
    [InlineData("<root><a>1</a><b>2</b><c>3</c></root>")]
    [InlineData("<root><empty /></root>")]
    public void XMBtoFormattedXmlString_MatchesXMBtoXML_FormatXML(string xmlInput)
    {
        var xml = new XmlDocument();
        xml.LoadXml(xmlInput);
        var xmb = BarFormatConverter.XMLtoXMB(xml, CompressionType.None);

        var oldResult = BarFormatConverter.FormatXML(BarFormatConverter.XMBtoXML(xmb.Span)!);
        var newResult = BarFormatConverter.XMBtoFormattedXmlString(xmb.Span);

        Assert.NotNull(newResult);
        Assert.Equal(oldResult, newResult);
    }

    [Fact]
    public void XMBtoFormattedXmlString_InvalidData_ReturnsNull()
    {
        byte[] invalidData = [0, 1, 2, 3, 4, 5];
        var result = BarFormatConverter.XMBtoFormattedXmlString(invalidData);
        Assert.Null(result);
    }

    #endregion

    #region FormatXML Tests

    [Fact]
    public void FormatXML_ProducesIndentedOutput()
    {
        var xml = new XmlDocument();
        xml.LoadXml("<root><child><nested>text</nested></child></root>");

        var formatted = BarFormatConverter.FormatXML(xml);

        // Should contain tab indentation
        Assert.Contains("\t", formatted);
        // Should contain newlines
        Assert.Contains("\n", formatted);
        // Should contain the element names
        Assert.Contains("<root>", formatted);
        Assert.Contains("<child>", formatted);
        Assert.Contains("<nested>text</nested>", formatted);
    }

    [Fact]
    public void FormatXML_OmitsXmlDeclaration()
    {
        var xml = new XmlDocument();
        xml.LoadXml("<root />");

        var formatted = BarFormatConverter.FormatXML(xml);

        Assert.DoesNotContain("<?xml", formatted);
    }

    [Fact]
    public void FormatXML_PreservesAttributes()
    {
        var xml = new XmlDocument();
        xml.LoadXml("<root version=\"1\" name=\"test\" />");

        var formatted = BarFormatConverter.FormatXML(xml);

        Assert.Contains("version=\"1\"", formatted);
        Assert.Contains("name=\"test\"", formatted);
    }

    #endregion
}
