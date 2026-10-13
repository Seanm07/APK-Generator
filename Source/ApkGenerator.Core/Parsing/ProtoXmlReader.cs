using System.Text;

namespace ApkGenerator.Core.Parsing;

/// <summary>
/// Reads the protobuf-encoded AndroidManifest.xml found at base/manifest/ inside an .aab
/// (aapt2's Resources.proto XmlNode). Hand-rolled so no protobuf library is needed.
/// </summary>
internal static class ProtoXmlReader
{
    public static XmlElem Parse(byte[] data)
    {
        var r = new PbReader(data, 0, data.Length);
        XmlElem? root = null;
        while (r.ReadTag(out var field, out var wire))
        {
            if (field == 1 && wire == 2)
            {
                var (s, e) = r.ReadLength();
                root = ParseElement(data, s, e);
            }
            else r.Skip(wire);
        }
        return root ?? throw new InvalidDataException("The manifest has no root element.");
    }

    // XmlNode { element = 1; text = 2; source = 3 }
    private static XmlElem? ParseNode(byte[] data, int start, int end)
    {
        var r = new PbReader(data, start, end);
        XmlElem? el = null;
        while (r.ReadTag(out var field, out var wire))
        {
            if (field == 1 && wire == 2)
            {
                var (s, e) = r.ReadLength();
                el = ParseElement(data, s, e);
            }
            else r.Skip(wire);
        }
        return el;
    }

    // XmlElement { namespace_declaration = 1; namespace_uri = 2; name = 3; attribute = 4; child = 5 }
    private static XmlElem ParseElement(byte[] data, int start, int end)
    {
        var el = new XmlElem();
        var r = new PbReader(data, start, end);
        while (r.ReadTag(out var field, out var wire))
        {
            if (wire == 2 && field == 3) el.Name = r.ReadString();
            else if (wire == 2 && field == 4)
            {
                var (s, e) = r.ReadLength();
                ParseAttribute(data, s, e, el);
            }
            else if (wire == 2 && field == 5)
            {
                var (s, e) = r.ReadLength();
                var child = ParseNode(data, s, e);
                if (child != null) el.Children.Add(child);
            }
            else r.Skip(wire);
        }
        return el;
    }

    // XmlAttribute { namespace_uri = 1; name = 2; value = 3; source = 4; resource_id = 5; compiled_item = 6 }
    private static void ParseAttribute(byte[] data, int start, int end, XmlElem owner)
    {
        string? ns = null, name = null, value = null, compiled = null;
        var r = new PbReader(data, start, end);
        while (r.ReadTag(out var field, out var wire))
        {
            if (wire == 2 && field == 1) ns = r.ReadString();
            else if (wire == 2 && field == 2) name = r.ReadString();
            else if (wire == 2 && field == 3) value = r.ReadString();
            else if (wire == 2 && field == 6)
            {
                var (s, e) = r.ReadLength();
                compiled = ParseItem(data, s, e);
            }
            else r.Skip(wire);
        }
        if (string.IsNullOrEmpty(name)) return;
        owner.Attrs[XmlElem.KeyFor(ns, name)] = string.IsNullOrEmpty(value) ? (compiled ?? "") : value;
    }

    // Item { ref = 1; str = 2; raw_str = 3; styled_str = 4; file = 5; id = 6; prim = 7 }
    private static string? ParseItem(byte[] data, int start, int end)
    {
        var r = new PbReader(data, start, end);
        while (r.ReadTag(out var field, out var wire))
        {
            if (wire == 2 && (field == 2 || field == 3))
            {
                var (s, e) = r.ReadLength();
                var sr = new PbReader(data, s, e);                // String { value = 1 }
                while (sr.ReadTag(out var f2, out var w2))
                {
                    if (f2 == 1 && w2 == 2) return sr.ReadString();
                    sr.Skip(w2);
                }
                return "";
            }
            if (wire == 2 && field == 7)
            {
                var (s, e) = r.ReadLength();
                return ParsePrimitive(data, s, e);
            }
            r.Skip(wire);
        }
        return null;
    }

    // Primitive { ... int_decimal_value = 6; int_hexadecimal_value = 7; boolean_value = 8 ... }
    private static string? ParsePrimitive(byte[] data, int start, int end)
    {
        var r = new PbReader(data, start, end);
        while (r.ReadTag(out var field, out var wire))
        {
            if (wire == 0 && field == 6) return ((int)r.ReadVarint()).ToString();
            if (wire == 0 && field == 7) return ((uint)r.ReadVarint()).ToString();
            if (wire == 0 && field == 8) return r.ReadVarint() != 0 ? "true" : "false";
            r.Skip(wire);
        }
        return null;
    }
}

/// <summary>Bare-bones protobuf wire-format reader over a slice of a byte array.</summary>
internal sealed class PbReader
{
    private readonly byte[] _d;
    private int _p;
    private readonly int _end;

    public PbReader(byte[] data, int start, int end)
    {
        _d = data;
        _p = start;
        _end = end;
    }

    public bool ReadTag(out int field, out int wire)
    {
        if (_p >= _end) { field = 0; wire = 0; return false; }
        var t = ReadVarint();
        field = (int)(t >> 3);
        wire = (int)(t & 7);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (_p >= _end) throw new InvalidDataException("Truncated protobuf data.");
            var b = _d[_p++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("Malformed protobuf varint.");
        }
    }

    public (int start, int end) ReadLength()
    {
        var len = ReadVarint();
        if (len > (ulong)(_end - _p)) throw new InvalidDataException("Protobuf length exceeds the data.");
        var s = _p;
        _p += (int)len;
        return (s, _p);
    }

    public string ReadString()
    {
        var (s, e) = ReadLength();
        return Encoding.UTF8.GetString(_d, s, e - s);
    }

    public void Skip(int wire)
    {
        switch (wire)
        {
            case 0: ReadVarint(); break;
            case 1: _p += 8; break;
            case 2: ReadLength(); break;
            case 5: _p += 4; break;
            default: throw new InvalidDataException($"Unsupported protobuf wire type {wire}.");
        }
        if (_p > _end) throw new InvalidDataException("Truncated protobuf data.");
    }
}
