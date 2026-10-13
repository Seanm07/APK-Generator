using System.Text;

namespace ApkGenerator.Core.Parsing;

/// <summary>Reads the compiled binary AndroidManifest.xml (AXML) stored inside an .apk.</summary>
internal static class AxmlReader
{
    private const int ResXml = 0x0003;
    private const int StringPool = 0x0001;
    private const int StartElement = 0x0102;
    private const int EndElement = 0x0103;

    public static XmlElem Parse(byte[] d)
    {
        if (d.Length < 8 || U16(d, 0) != ResXml)
            throw new InvalidDataException("Not a compiled Android XML file.");

        var total = (int)Math.Min(U32(d, 4), (uint)d.Length);
        var pos = U16(d, 2);
        var strings = Array.Empty<string>();
        XmlElem? root = null;
        var stack = new Stack<XmlElem>();

        while (pos + 8 <= total)
        {
            var type = U16(d, pos);
            var headerSize = U16(d, pos + 2);
            var size = (int)U32(d, pos + 4);
            if (size < 8 || pos + size > total) break;

            switch (type)
            {
                case StringPool:
                    strings = ReadStringPool(d, pos, headerSize);
                    break;

                case StartElement:
                {
                    var el = ReadStartElement(d, pos, headerSize, strings);
                    if (stack.Count > 0) stack.Peek().Children.Add(el);
                    else root ??= el;
                    stack.Push(el);
                    break;
                }

                case EndElement:
                    if (stack.Count > 0) stack.Pop();
                    break;
            }
            pos += size;
        }
        return root ?? throw new InvalidDataException("The manifest has no root element.");
    }

    private static XmlElem ReadStartElement(byte[] d, int pos, int headerSize, string[] strings)
    {
        // chunk header (8) + line number (4) + comment (4) = headerSize, then the attribute extension
        var ext = pos + headerSize;
        var nameIdx = I32(d, ext + 4);
        var attrStart = U16(d, ext + 8);
        var attrCount = U16(d, ext + 12);

        var el = new XmlElem { Name = Str(strings, nameIdx) ?? "" };
        var a = ext + attrStart;
        for (var i = 0; i < attrCount; i++, a += 20)
        {
            var ns = Str(strings, I32(d, a));
            var name = Str(strings, I32(d, a + 4));
            var raw = I32(d, a + 8);
            var dataType = d[a + 15];
            var data = U32(d, a + 16);
            if (string.IsNullOrEmpty(name)) continue;

            string? value;
            if (raw >= 0) value = Str(strings, raw);
            else
            {
                value = dataType switch
                {
                    0x03 => Str(strings, (int)data),
                    0x10 => ((int)data).ToString(),
                    0x11 => "0x" + data.ToString("x"),
                    0x12 => data != 0 ? "true" : "false",
                    _ => "@0x" + data.ToString("x"),
                };
            }
            el.Attrs[XmlElem.KeyFor(ns, name)] = value ?? "";
        }
        return el;
    }

    private static string[] ReadStringPool(byte[] d, int pos, int headerSize)
    {
        var count = I32(d, pos + 8);
        var flags = I32(d, pos + 16);
        var stringsStart = I32(d, pos + 20);
        var utf8 = (flags & 0x100) != 0;
        var result = new string[count];
        var offsets = pos + headerSize;
        var dataBase = pos + stringsStart;

        for (var i = 0; i < count; i++)
        {
            var p = dataBase + (int)U32(d, offsets + i * 4);
            if (utf8)
            {
                ReadUtf8Length(d, ref p);                 // length in characters (unused)
                var byteLen = ReadUtf8Length(d, ref p);
                result[i] = Encoding.UTF8.GetString(d, p, byteLen);
            }
            else
            {
                var len = U16(d, p);
                p += 2;
                if ((len & 0x8000) != 0)
                {
                    len = ((len & 0x7FFF) << 16) | U16(d, p);
                    p += 2;
                }
                result[i] = Encoding.Unicode.GetString(d, p, len * 2);
            }
        }
        return result;
    }

    private static int ReadUtf8Length(byte[] d, ref int p)
    {
        int len = d[p++];
        if ((len & 0x80) != 0) len = ((len & 0x7F) << 8) | d[p++];
        return len;
    }

    private static string? Str(string[] strings, int idx) => idx >= 0 && idx < strings.Length ? strings[idx] : null;
    private static int U16(byte[] d, int p) => d[p] | (d[p + 1] << 8);
    private static uint U32(byte[] d, int p) => BitConverter.ToUInt32(d, p);
    private static int I32(byte[] d, int p) => BitConverter.ToInt32(d, p);
}
