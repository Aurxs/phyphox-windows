// SPDX-License-Identifier: GPL-3.0-or-later
// Unnormalized forward DFT, matching phyphox FFT direction and output shape.
using System.Numerics;

namespace Phyphox.Core;
internal static class FourierTransform
{
    public static Complex[] Forward(Complex[] source)
    {
        int n = source.Length;
        if (n < 2)
            return[];
        if (n > 4_194_304)
            throw new InvalidOperationException("FFT input exceeds configured memory budget.");
        if ((n & (n - 1)) == 0)
        {
            var result = (Complex[])source.Clone();
            Radix2(result);
            return result;
        }

        // Bluestein convolution preserves arbitrary input length instead of silently padding the experiment signal.
        int length = 1;
        while (length < 2 * n - 1)
            length <<= 1;
        var a = new Complex[length];
        var b = new Complex[length];
        for (int i = 0; i < n; i++)
        {
            double angle = Math.PI * ((long)i * i % (2L * n)) / n;
            var chirp = Complex.FromPolarCoordinates(1, angle);
            a[i] = source[i] * Complex.Conjugate(chirp);
            b[i] = chirp;
            if (i != 0)
                b[length - i] = chirp;
        }

        Radix2(a);
        Radix2(b);
        for (int i = 0; i < length; i++)
            a[i] = Complex.Conjugate(a[i] * b[i]);
        Radix2(a);
        var output = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            double angle = Math.PI * ((long)i * i % (2L * n)) / n;
            output[i] = Complex.Conjugate(a[i]) / length * Complex.FromPolarCoordinates(1, -angle);
        }

        return output;
    }

    static void Radix2(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
                (a[i], a[j]) = (a[j], a[i]);
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            var root = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int j = 0; j < len / 2; j++)
                {
                    var u = a[i + j];
                    var v = a[i + j + len / 2] * w;
                    a[i + j] = u + v;
                    a[i + j + len / 2] = u - v;
                    w *= root;
                }
            }
        }
    }
}
