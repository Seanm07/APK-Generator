namespace ApkGenerator.Core.Parsing;

/// <summary>Minimal XML element – all we need to read an AndroidManifest.</summary>
internal sealed class XmlElem
{
    public const string AndroidNs = "http://schemas.android.com/apk/res/android";

    public string Name { get; set; } = "";

    /// <summary>Attribute values keyed "android:name" for the android namespace, or plain "package" etc.</summary>
    public Dictionary<string, string> Attrs { get; } = new();

    public List<XmlElem> Children { get; } = new();

    public string? Attr(string key) => Attrs.TryGetValue(key, out var v) ? v : null;

    public static string KeyFor(string? ns, string name) => ns == AndroidNs ? "android:" + name : name;

    public IEnumerable<XmlElem> ChildrenNamed(string name) => Children.Where(c => c.Name == name);
}
