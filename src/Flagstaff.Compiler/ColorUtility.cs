namespace Flagstaff.Compiler;

/// <summary>
/// Computes the WinUI 3 SystemAccentColor family (base + Light1-3 + Dark1-3)
/// from a single hex color using HSL lightness adjustments.
/// </summary>
public static class ColorUtility
{
    /// <summary>
    /// Returns 7 hex strings: [base, light1, light2, light3, dark1, dark2, dark3].
    /// </summary>
    public static string[] ComputeAccentFamily(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        var (h, s, l) = RgbToHsl(r, g, b);

        return
        [
            hex.StartsWith('#') ? hex : "#" + hex,
            HslToHex(h, s, Math.Min(1.0, l + 0.15)),
            HslToHex(h, s, Math.Min(1.0, l + 0.30)),
            HslToHex(h, s, Math.Min(1.0, l + 0.45)),
            HslToHex(h, s, Math.Max(0.0, l - 0.10)),
            HslToHex(h, s, Math.Max(0.0, l - 0.20)),
            HslToHex(h, s, Math.Max(0.0, l - 0.30)),
        ];
    }

    private static (int R, int G, int B) ParseHex(string hex)
    {
        var h = hex.TrimStart('#');
        return (
            Convert.ToInt32(h[0..2], 16),
            Convert.ToInt32(h[2..4], 16),
            Convert.ToInt32(h[4..6], 16)
        );
    }

    private static (double H, double S, double L) RgbToHsl(int r, int g, int b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double l = (max + min) / 2.0;

        if (max == min)
            return (0, 0, l);

        double d = max - min;
        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        double h;

        if (max == rd)
            h = (gd - bd) / d + (gd < bd ? 6 : 0);
        else if (max == gd)
            h = (bd - rd) / d + 2;
        else
            h = (rd - gd) / d + 4;

        h /= 6.0;
        return (h, s, l);
    }

    private static string HslToHex(double h, double s, double l)
    {
        double r, g, b;

        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3.0);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3.0);
        }

        int ri = (int)Math.Round(r * 255);
        int gi = (int)Math.Round(g * 255);
        int bi = (int)Math.Round(b * 255);
        return $"#{ri:X2}{gi:X2}{bi:X2}";
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }
}
