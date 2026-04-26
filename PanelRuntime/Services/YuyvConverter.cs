namespace PanelRuntime.Services;

public static class YuyvConverter
{
    public static byte[] BgraToYuyv(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (bgra.Length != width * height * 4)
        {
            throw new ArgumentException($"BGRA size {bgra.Length} != {width * height * 4}");
        }
        if ((width & 1) != 0)
        {
            throw new ArgumentException("YUYV conversion requires an even width.");
        }

        var output = new byte[width * height * 2];
        var oi = 0;
        for (var i = 0; i < bgra.Length; i += 8)
        {
            var b0 = bgra[i];
            var g0 = bgra[i + 1];
            var r0 = bgra[i + 2];
            var b1 = bgra[i + 4];
            var g1 = bgra[i + 5];
            var r1 = bgra[i + 6];

            RgbToYuv(r0, g0, b0, out var y0, out var u0, out var v0);
            RgbToYuv(r1, g1, b1, out var y1, out var u1, out var v1);

            output[oi++] = y0;
            output[oi++] = (byte)((u0 + u1 + 1) >> 1);
            output[oi++] = y1;
            output[oi++] = (byte)((v0 + v1 + 1) >> 1);
        }
        return output;
    }

    public static byte[] MakeSolid(int width, int height, byte r, byte g, byte b)
    {
        RgbToYuv(r, g, b, out var y, out var u, out var v);
        var output = new byte[width * height * 2];
        for (var i = 0; i < output.Length; i += 4)
        {
            output[i] = y;
            output[i + 1] = u;
            output[i + 2] = y;
            output[i + 3] = v;
        }
        return output;
    }

    private static void RgbToYuv(byte r, byte g, byte b, out byte y, out byte u, out byte v)
    {
        y = Clamp(0.299 * r + 0.587 * g + 0.114 * b);
        u = Clamp(-0.168736 * r - 0.331264 * g + 0.5 * b + 128);
        v = Clamp(0.5 * r - 0.418688 * g - 0.081312 * b + 128);
    }

    private static byte Clamp(double value)
    {
        if (value <= 0) return 0;
        if (value >= 255) return 255;
        return (byte)Math.Round(value);
    }
}
