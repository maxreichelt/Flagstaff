using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Flagstaff.ManifestGen;

/// <summary>
/// Serializes the manifest deterministically: ordinal sort (done by the reader), 2-space indent, LF, no timestamps.
/// Optional members (baseType, contentProperty, enumMembers) are written only when present.
/// </summary>
internal static class ManifestWriter
{
    public const int GzipThresholdBytes = 2 * 1024 * 1024;

    public static string ToJson(ManifestModel model)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true,
            IndentSize = 2,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();
            w.WriteNumber("formatVersion", 1);
            w.WriteStartArray("sources");
            foreach (var s in model.Sources) w.WriteStringValue(s);
            w.WriteEndArray();

            w.WriteStartArray("types");
            foreach (var t in model.Types)
            {
                w.WriteStartObject();
                w.WriteString("fullName", t.FullName);
                w.WriteString("name", t.Name);
                w.WriteString("namespace", t.Namespace);
                if (t.BaseType is not null) w.WriteString("baseType", t.BaseType);
                if (t.IsAbstract) w.WriteBoolean("isAbstract", true);
                if (t.IsControl) w.WriteBoolean("isControl", true);
                if (t.IsUIElement) w.WriteBoolean("isUIElement", true);
                if (t.ContentProperty is not null) w.WriteString("contentProperty", t.ContentProperty);
                WriteProperties(w, "properties", t.Properties);
                WriteAttached(w, t.AttachedProperties);
                if (t.Events.Count > 0)
                {
                    w.WriteStartArray("events");
                    foreach (var e in t.Events)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", e.Name);
                        w.WriteString("argsTypeName", e.ArgsTypeName);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("attachedOwners");
            foreach (var o in model.AttachedOwners)
            {
                w.WriteStartObject();
                w.WriteString("fullName", o.FullName);
                w.WriteString("name", o.Name);
                w.WriteString("namespace", o.Namespace);
                WriteAttached(w, o.AttachedProperties);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("valueTypes");
            foreach (var v in model.ValueTypes)
            {
                w.WriteStartObject();
                w.WriteString("fullName", v.FullName);
                w.WriteString("name", v.Name);
                w.WriteString("namespace", v.Namespace);
                if (v.IsEnum) w.WriteBoolean("isEnum", true);
                w.WriteBoolean("isValueType", v.IsValueType);
                if (v.EnumMembers is not null) WriteStrings(w, "enumMembers", v.EnumMembers);
                if (v.Fields is not null) WriteProperties(w, "fields", v.Fields);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private static void WriteProperties(Utf8JsonWriter w, string name, IReadOnlyList<PropertyInfoModel> props)
    {
        if (props.Count == 0) return;
        w.WriteStartArray(name);
        foreach (var p in props)
        {
            w.WriteStartObject();
            w.WriteString("name", p.Name);
            w.WriteString("typeName", p.TypeName);
            if (p.IsDependencyProperty) w.WriteBoolean("isDependencyProperty", true);
            if (p.IsCollection) w.WriteBoolean("isCollection", true);
            if (p.IsReadOnly) w.WriteBoolean("isReadOnly", true);
            if (p.EnumMembers is not null) WriteStrings(w, "enumMembers", p.EnumMembers);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteAttached(Utf8JsonWriter w, IReadOnlyList<AttachedModel> attached)
    {
        if (attached.Count == 0) return;
        w.WriteStartArray("attachedProperties");
        foreach (var a in attached)
        {
            w.WriteStartObject();
            w.WriteString("name", a.Name);
            w.WriteString("typeName", a.TypeName);
            if (a.EnumMembers is not null) WriteStrings(w, "enumMembers", a.EnumMembers);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }

    public static byte[] Gzip(string json)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            gz.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    public static string Gunzip(byte[] data)
    {
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var sr = new StreamReader(gz, Encoding.UTF8);
        return sr.ReadToEnd();
    }
}
