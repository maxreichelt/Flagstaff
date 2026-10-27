namespace Flagstaff.Compiler;

// The xaml { } escape hatch.
public sealed partial class XamlGenerator
{
    private void EmitRawXaml(Element el)
    {
        if (el.Body is RawXamlBody raw)
        {
            foreach (var line in raw.Xaml.Split('\n'))
                Line(line.TrimEnd());
        }
    }
}
