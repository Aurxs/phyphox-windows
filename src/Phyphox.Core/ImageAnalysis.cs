// SPDX-License-Identifier: GPL-3.0-or-later
// Pixel/output semantics ported from Android Analysis.imagedecodeAM.
using StbImageSharp;

namespace Phyphox.Core;
public interface IExperimentInfoProvider
{
    /// <summary>Return false for unavailable hardware information; never invent sensor values.</summary>
    bool TryGetValue(string key, out double value);
}

internal static class ImageAnalysis
{
    public static double[][] Decode(double[] data, string[] outputs)
    {
        if (data.Length == 0)
            return outputs.Select(_ => Array.Empty<double>()).ToArray();
        byte[] encoded = data.Select(v =>
        {
            double rounded = Math.Round(v, MidpointRounding.AwayFromZero);
            return !double.IsFinite(rounded) || Math.Abs(rounded) > int.MaxValue ? (byte)0 : unchecked((byte)(int)rounded);
        }).ToArray();
        if (encoded.AsSpan().IndexOf("iCCP"u8) >= 0 || encoded.AsSpan().IndexOf("ICC_PROFILE"u8) >= 0)
            throw new NotSupportedException("ICC-profile image color conversion is not implemented; an explicit sRGB conversion is required.");
        using var stream = new MemoryStream(encoded, false);
        ImageInfo? metadata;
        try
        {
            metadata = ImageInfo.FromStream(stream);
        }
        catch (Exception e)when (e is InvalidOperationException or ArgumentException or EndOfStreamException)
        {
            return outputs.Select(_ => Array.Empty<double>()).ToArray();
        }

        if (metadata == null)
            return outputs.Select(_ => Array.Empty<double>()).ToArray();
        if ((long)metadata.Value.Width * metadata.Value.Height > 16_000_000)
            throw new InvalidOperationException("Image exceeds 16 megapixel decode budget.");
        stream.Position = 0;
        ImageResult image;
        try
        {
            image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception e)when (e is InvalidOperationException or ArgumentException or EndOfStreamException)
        {
            return outputs.Select(_ => Array.Empty<double>()).ToArray();
        }

        int count = checked(image.Width * image.Height);
        double Linear(double v) => v < 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return outputs.Select(name => name switch
        {
            "width" => [(double)image.Width],
            "height" => [(double)image.Height],
            _ => Enumerable.Range(0, count).Select(i =>
            {
                double r = image.Data[4 * i] / 255.0, g = image.Data[4 * i + 1] / 255.0, b = image.Data[4 * i + 2] / 255.0;
                return name switch
                {
                    "r" => r,
                    "g" => g,
                    "b" => b,
                    "a" => image.Data[4 * i + 3] / 255.0,
                    "luma" => 0.2126 * r + 0.7152 * g + 0.0722 * b,
                    "luminance" => 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b),
                    _ => throw new FormatException($"Unknown image output: {name}")};
            }).ToArray()}).ToArray();
    }
}
