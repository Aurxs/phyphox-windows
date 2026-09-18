// SPDX-License-Identifier: GPL-3.0-or-later
// Ported semantics: phyphox Android Analysis.java and official analysis_reference.py.
using System.Xml.Linq;

namespace Phyphox.Core;
public static class AnalysisKernel
{
    public static IReadOnlySet<string> Supported { get; } = new HashSet<string>(("timer formula add subtract multiply divide power gcd lcm abs round log sin cos tan sinh cosh tanh asin acos atan atan2 average count first append const ramp differentiate integrate min max threshold split subrange movingaverage interpolate sort match if gausssmooth autocorrelation crosscorrelation binning reduce rangefilter fft butterworth loess map eventstream periodicity imagedecode info").Split(' '));

    public static double[]? [] Evaluate(XElement m, XElement[] tags, double[][] input, int[] sizes, double time, IExperimentInfoProvider? infoProvider = null, AnalysisTimeContext? timing = null)
    {
        string name = m.Name.LocalName;
        double[] Get(string alias, int fallback = -1)
        {
            var i = Array.FindIndex(tags, t => t.Attr("as") == alias);
            return i >= 0 ? input[i] : fallback >= 0 && fallback < input.Length ? input[fallback] : [];
        }

        double Last(string alias, double fallback = double.NaN, int index = -1)
        {
            var a = Get(alias, index);
            return a.Length > 0 ? a[^1] : fallback;
        }

        double[] x = input.FirstOrDefault() ?? [];
        double[] y = input.Skip(1).FirstOrDefault() ?? [];
        double[] Map(Func<double, double> f) => x.Select(f).ToArray();
        double[] Fold(Func<double[], double> f) => input.Length == 0 || input.Any(a => a.Length == 0) ? [] : Enumerable.Range(0, input.Max(a => a.Length)).Select(i => f(input.Select(a => a[Math.Min(i, a.Length - 1)]).ToArray())).ToArray();
        bool deg = m.Flag("deg");
        double ang = deg ? Math.PI / 180 : 1;
        switch (name)
        {
            case "timer":
                return m.Children("output").Select(o => new[] { o.Attr("as") == "offset1970" ? (m.Flag("linearTime") ? timing?.LinearOffset1970 : timing?.Offset1970) ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 : m.Flag("linearTime") ? timing?.LinearTime ?? time : time }).ToArray();
            case "formula":
            {
                var formula = new Formula(m.Attr("formula"));
                var output = new List<double>();
                for (int i = 0; i < (input.Length == 0 ? 0 : input.Max(a => a.Length)); i++)
                {
                    try
                    {
                        output.Add(formula.Evaluate(input, i));
                    }
                    catch (IndexOutOfRangeException)
                    {
                        break;
                    }
                }

                return[output.ToArray()];
            }

            case "add":
                return[Fold(a => a.Aggregate(0.0, (v, b) => v + b))];
            case "subtract":
                return[Fold(a => a.Skip(1).Aggregate(a[0], (v, b) => v - b))];
            case "multiply":
                return[Fold(a => a.Aggregate(1.0, (v, b) => v * b))];
            case "divide":
                return[Fold(a => a.Skip(1).Aggregate(a[0], (v, b) => v / b))];
            case "power":
                return[Fold(a => Math.Pow(a[0], a[1]))];
            case "gcd":
            case "lcm":
                return[Fold(a => GcdLcm(a[0], a[1], name == "lcm"))];
            case "abs":
                return[Map(Math.Abs)];
            case "round":
                return[Map(v => m.Flag("floor") ? Math.Floor(v) : m.Flag("ceil") ? Math.Ceiling(v) : Math.Round(v, MidpointRounding.AwayFromZero))];
            case "log":
                return[Map(Math.Log)];
            case "sin":
                return[Map(v => Math.Sin(v * ang))];
            case "cos":
                return[Map(v => Math.Cos(v * ang))];
            case "tan":
                return[Map(v => Math.Tan(v * ang))];
            case "asin":
                return[Map(v => Math.Asin(v) / ang)];
            case "acos":
                return[Map(v => Math.Acos(v) / ang)];
            case "atan":
                return[Map(v => Math.Atan(v) / ang)];
            case "atan2":
                return[Fold(a => Math.Atan2(a[0], a[1]) / ang)];
            case "sinh":
                return[Map(Math.Sinh)];
            case "cosh":
                return[Map(Math.Cosh)];
            case "tanh":
                return[Map(Math.Tanh)];
            case "average":
                var finite = x.Where(double.IsFinite).ToArray();
                var avg = finite.Length == 0 ? double.NaN : finite.Sum() / finite.Length;
                var std = finite.Length < 2 ? double.NaN : Math.Sqrt(finite.Sum(v => (v - avg) * (v - avg)) / (finite.Length - 1));
                return m.Children("output").Select(o => new[] { o.Attr("as") == "stddev" ? std : avg }).ToArray();
            case "count":
                return[[x.Length]];
            case "first":
                return input.Select(a => a.Take(1).ToArray()).ToArray();
            case "append":
                return[input.SelectMany(a => a).ToArray()];
            case "const":
            case "ramp":
            {
                var len = Last("length", sizes.FirstOrDefault());
                if (!double.IsFinite(len) || len < 0)
                    return[[]];
                if (len > 10_000_000)
                    throw new InvalidOperationException("Generator size exceeds safety limit.");
                int n = (int)len;
                var start = Last("start", 0);
                var stop = Last("stop", 1);
                var value = Last("value", 0, 0);
                return[Enumerable.Range(0, n).Select(i => name == "const" ? value : n == 1 ? start : start + (stop - start) / (n - 1) * i).ToArray()];
            }

            case "differentiate":
                return[Enumerable.Range(0, Math.Max(0, x.Length - 1)).Select(i => x[i + 1] - x[i]).ToArray()];
            case "integrate":
                double sum = 0;
                return[Map(v => sum += v)];
            case "min":
            case "max":
            {
                var data = Get("y", 0);
                var axis = Get("x");
                var values = new List<double>();
                var positions = new List<double>();
                bool minimum = name == "min", multiple = m.Flag("multiple");
                double threshold = Last("threshold", 0);
                double current = minimum ? double.PositiveInfinity : double.NegativeInfinity, pos = double.NaN;
                bool found = false;
                int count = axis.Length == 0 ? data.Length : Math.Min(data.Length, axis.Length);
                void Flush()
                {
                    values.Add(found ? current : double.NaN);
                    positions.Add(found ? pos : double.NaN);
                    current = minimum ? double.PositiveInfinity : double.NegativeInfinity;
                    pos = double.NaN;
                    found = false;
                }

                for (int i = 0; i < count; i++)
                {
                    var v = data[i];
                    if (multiple && (minimum ? v > threshold : v < threshold))
                    {
                        if (found)
                            Flush();
                        continue;
                    }

                    if (!multiple || (minimum ? v <= threshold : v >= threshold))
                    {
                        if (minimum ? v < current : v > current)
                        {
                            current = v;
                            pos = axis.Length == 0 ? i : axis[i];
                            found = true;
                        }
                    }
                }

                if (!multiple || found)
                    Flush();
                return m.Children("output").Select(o => o.Attr("as") == "position" ? positions.ToArray() : values.ToArray()).ToArray();
            }

            case "threshold":
            {
                var data = Get("y", 0);
                var axis = Get("x");
                double threshold = Last("threshold", 0);
                bool? previous = null;
                for (int i = 0; i < data.Length; i++)
                {
                    bool fires = m.Flag("falling") ? data[i] < threshold : data[i] > threshold;
                    if (fires && previous == false)
                        return[[i < axis.Length ? axis[i] : i]];
                    previous = fires;
                }

                return[[double.NaN]];
            }

            case "split":
            {
                var data = Get("data", 0);
                double idx = Last("index", data.Length), overlap = Last("overlap", 0);
                if (!double.IsFinite(idx) || !double.IsFinite(overlap))
                    return[[], []];
                int k = (int)Math.Clamp(idx, 0, data.Length), j = (int)Math.Clamp(k - overlap, 0, data.Length);
                return[data.Take(k).ToArray(), data.Skip(j).ToArray()];
            }

            case "subrange":
            {
                var data = tags.Select((t, i) => (t, i)).Where(p => p.t.Attr("as") == "" || p.t.Attr("as") == "data").Select(p => input[p.i]).ToArray();
                var from = Last("from", 0);
                var to = Last("to", data.Length == 0 ? 0 : data.Max(a => a.Length));
                var length = Last("length");
                if (tags.Any(t => t.Attr("as") == "length"))
                    to = Math.Max(0, from) + length;
                if (!double.IsFinite(from) || !double.IsFinite(to))
                    return data.Select(_ => Array.Empty<double>()).ToArray();
                return data.Select(a => a.Skip(Math.Max(0, (int)from)).Take(Math.Max(0, (int)to - Math.Max(0, (int)from))).ToArray()).ToArray();
            }

            case "movingaverage":
            {
                var data = Get("data", 0);
                var width = Last("width", 10);
                if (!double.IsFinite(width) || width < 0)
                    return[[]];
                int w = (int)width;
                return[Enumerable.Range(m.Flag("dropIncomplete") ? Math.Min(w, data.Length) : 0, data.Length - (m.Flag("dropIncomplete") ? Math.Min(w, data.Length) : 0)).Select(i =>
                {
                    var window = data.Skip(Math.Max(0, i - w)).Take(Math.Min(w, i) + 1).Where(double.IsFinite).ToArray();
                    return window.Length == 0 ? double.NaN : window.Average();
                }).ToArray()];
            }

            case "interpolate":
            {
                var axis = Get("x", 0);
                var data = Get("y", 1);
                var query = Get("xi", 2);
                int count = Math.Min(axis.Length, data.Length), j = 0;
                return[query.Select(v =>
                {
                    if (count == 0)
                        return double.NaN;
                    if (count == 1)
                        return data[0];
                    while (j < count && axis[j] < v)
                        j++;
                    if (j == 0)
                        return data[0];
                    if (j == count)
                        return data[^1];
                    if (axis[j] == v)
                        return data[j];
                    return m.Attr("method", "linear") switch
                    {
                        "previous" => data[j - 1],
                        "next" => data[j],
                        "nearest" => v - axis[j - 1] < axis[j] - v ? data[j - 1] : data[j],
                        _ => data[j - 1] + (data[j] - data[j - 1]) * (v - axis[j - 1]) / (axis[j] - axis[j - 1])};
                }).ToArray()];
            }

            case "sort":
            case "match":
            {
                int count = input.Length == 0 ? 0 : input.Min(a => a.Length);
                IEnumerable<int> order = Enumerable.Range(0, count);
                if (name == "match")
                    order = order.Where(i => input.All(a => double.IsFinite(a[i])));
                else
                {
                    var cmp = Comparer<double>.Create((a, b) => double.IsNaN(a) ? double.IsNaN(b) ? 0 : 1 : double.IsNaN(b) ? -1 : a.CompareTo(b));
                    order = m.Flag("descending") ? order.OrderByDescending(i => x[i], cmp) : order.OrderBy(i => x[i], cmp);
                }

                var indices = order.ToArray();
                return input.Select(a => indices.Select(i => a[i]).ToArray()).ToArray();
            }

            case "if":
            {
                var a = Get("a", 0);
                var b = Get("b", 1);
                if (a.Length == 0 || b.Length == 0)
                    return[null ];
                bool condition = m.Flag("less") && a[^1] < b[^1] || m.Flag("equal") && a[^1] == b[^1] || m.Flag("greater") && a[^1] > b[^1];
                var alias = condition ? "true" : "false";
                int idx = Array.FindIndex(tags, t => t.Attr("as") == alias);
                return[idx < 0 ? null : input[idx]];
            }

            case "autocorrelation":
            {
                var data = Get("y", 0);
                var axis = Get("x");
                int n = axis.Length == 0 ? data.Length : Math.Min(data.Length, axis.Length);
                var ox = new List<double>();
                var oy = new List<double>();
                double min = Last("minX", double.NegativeInfinity), max = Last("maxX", double.PositiveInfinity);
                for (int i = 0; i < n; i++)
                {
                    var lag = axis.Length == 0 ? i : axis[i] - axis[0];
                    if (lag < min || lag > max)
                        continue;
                    double total = 0;
                    for (int j = 0; j < n - i; j++)
                        total += data[j] * data[j + i];
                    ox.Add(lag);
                    oy.Add(total / (n - i));
                }

                return m.Children("output").Select(o => o.Attr("as") == "x" ? ox.ToArray() : oy.ToArray()).ToArray();
            }

            case "crosscorrelation":
            {
                var a = x.Length > y.Length ? x : y;
                var b = x.Length > y.Length ? y : x;
                return[Enumerable.Range(0, a.Length - b.Length).Select(i =>
                {
                    double total = 0;
                    for (int j = 0; j < b.Length; j++)
                        total += a[j + i] * b[j];
                    return total;
                }).ToArray()];
            }

            case "binning":
            {
                double x0 = Last("x0", 0), dx = Last("dx", 1);
                var data = Get("data", 0);
                if (!double.IsFinite(x0) || !double.IsFinite(dx) || dx <= 0)
                    return[[], []];
                var bins = data.Where(double.IsFinite).GroupBy(v => Math.Floor((v - x0) / dx)).ToDictionary(g => g.Key, g => (double)g.Count());
                if (bins.Count == 0)
                    return[[], []];
                double lo = bins.Keys.Min(), hi = bins.Keys.Max();
                if (hi - lo > 10_000_000)
                    throw new InvalidOperationException("Binning range exceeds safety limit.");
                var starts = Enumerable.Range(0, (int)(hi - lo + 1)).Select(i => x0 + (lo + i) * dx).ToArray();
                var counts = Enumerable.Range(0, starts.Length).Select(i => bins.GetValueOrDefault(lo + i)).ToArray();
                return m.Children("output").Select(o => o.Attr("as") == "binStarts" ? starts : counts).ToArray();
            }

            case "reduce":
            {
                var data = Get("x");
                var other = Get("y");
                double factor = Last("factor");
                if (!double.IsFinite(factor) || factor <= 0)
                    return[[], []];
                int n = other.Length == 0 ? data.Length : Math.Min(data.Length, other.Length);
                var ox = new List<double>();
                var oy = new List<double>();
                int step = (int)Math.Floor((factor > 1 ? factor : 1 / factor) + 0.5);
                if (step > 10_000_000)
                    throw new InvalidOperationException("Reduction factor exceeds safety limit.");
                for (int i = 0; i < n; i += factor > 1 ? step : 1)
                {
                    if (factor > 1)
                    {
                        int used = Math.Min(step, n - i);
                        ox.Add(m.Flag("averageX") ? data.Skip(i).Take(used).Sum() / used : data[i]);
                        var chunk = other.Length == 0 ? 0 : other.Skip(i).Take(used).Sum();
                        oy.Add(m.Flag("averageY") ? chunk / used : m.Flag("sumY") ? chunk : other.Length == 0 ? 0 : other[i]);
                    }
                    else
                    {
                        ox.AddRange(Enumerable.Repeat(data[i], step));
                        oy.AddRange(Enumerable.Repeat(other.Length == 0 ? 0 : other[i], step));
                    }
                }

                return m.Children("output").Select(o => o.Attr("as") == "y" ? oy.ToArray() : ox.ToArray()).ToArray();
            }

            case "rangefilter":
            {
                // Android ioBlockParser maps repeating slots into triples [in,min,max].
                // A bound can precede its data input; it belongs to the current group,
                // advancing only when that group's same slot is already occupied.
                var slots = new List<double[]?>();
                for (int i = 0; i < tags.Length; i++)
                {
                    int offset = tags[i].Attr("as") switch { "min" => 1, "max" => 2, _ => 0 };
                    int target = offset;
                    while (target - offset + 3 < slots.Count) target += 3;
                    while (slots.Count <= target) slots.Add(null);
                    while (slots[target] != null)
                    {
                        target += 3;
                        while (slots.Count <= target) slots.Add(null);
                    }
                    slots[target] = input[i];
                }
                var groups = new List<(double[] data, double min, double max)>();
                for (int i = 0; i < slots.Count; i += 3)
                    groups.Add((slots[i] ?? [], i + 1 < slots.Count ? slots[i + 1]?.LastOrDefault(double.NegativeInfinity) ?? double.NegativeInfinity : double.NegativeInfinity,
                        i + 2 < slots.Count ? slots[i + 2]?.LastOrDefault(double.PositiveInfinity) ?? double.PositiveInfinity : double.PositiveInfinity));

                var results = groups.Select(_ => new List<double>()).ToArray();
                for (int i = 0; i < groups.Max(g => g.data.Length); i++)
                {
                    var row = groups.Select(g => i < g.data.Length ? g.data[i] : double.NaN).ToArray();
                    if (groups.Select((g, k) => row[k] < g.min || row[k] > g.max).Any(v => v))
                        continue;
                    for (int k = 0; k < row.Length; k++)
                        results[k].Add(row[k]);
                }

                return results.Select(a => a.ToArray()).ToArray();
            }

            case "butterworth":
            {
                var data = Get("y", 0);
                var axis = Get("x", 1);
                double order = Last("n"), cutoff = Last("cutoff"), low = Last("cutoffLow", 0);
                if (double.IsNaN(order) || double.IsNaN(cutoff))
                    return[[]];
                return[Enumerable.Range(0, Math.Min(data.Length, axis.Length)).Select(i =>
                {
                    var f = Math.Abs(axis[i]);
                    double gain;
                    if (double.IsNaN(low) || low <= 0)
                        gain = 1 / Math.Sqrt(1 + Math.Pow(Math.Pow(f / cutoff, 2), order));
                    else if (f == 0)
                        gain = 0;
                    else
                        gain = 1 / Math.Sqrt(1 + Math.Pow(Math.Pow((f * f - low * cutoff) / (f * (cutoff - low)), 2), order));
                    return data[i] * gain;
                }).ToArray()];
            }

            case "fft":
            {
                var re = Get("re", 0);
                var im = Get("im");
                int n = im.Length == 0 ? re.Length : Math.Min(re.Length, im.Length);
                if (n < 2)
                    return[[], []];
                var a = FourierTransform.Forward(Enumerable.Range(0, n).Select(i => new System.Numerics.Complex(re[i], im.Length == 0 ? 0 : im[i])).ToArray());
                return m.Children("output").Select(o => a.Select(v => o.Attr("as") == "im" ? v.Imaginary : v.Real).ToArray()).ToArray();
            }

            case "loess":
            {
                var axis = Get("x");
                var data = Get("y");
                var queries = Get("xi");
                double d = Last("d");
                if (!double.IsFinite(d) || d <= 0)
                    return[[], [], []];
                var results = new[]
                {
                    new List<double>(),
                    new List<double>(),
                    new List<double>()
                };
                foreach (var q in queries)
                {
                    var powers = new double[5];
                    var rhs = new double[3];
                    for (int j = 0; j < Math.Min(axis.Length, data.Length); j++)
                    {
                        if (double.IsNaN(axis[j]) || double.IsNaN(data[j]))
                            continue;
                        double dx = axis[j] - q;
                        if (Math.Abs(dx) > d)
                            continue;
                        double weight = Math.Pow(1 - Math.Pow(Math.Abs(dx / d), 3), 3);
                        for (int k = 0; k < 5; k++)
                            powers[k] += weight * Math.Pow(dx, k);
                        for (int k = 0; k < 3; k++)
                            rhs[k] += weight * Math.Pow(dx, k) * data[j];
                    }

                    var matrix = new double[3, 4];
                    for (int r = 0; r < 3; r++)
                    {
                        for (int c = 0; c < 3; c++)
                            matrix[r, c] = powers[r + c];
                        matrix[r, 3] = rhs[r];
                    }

                    bool singular = false;
                    for (int k = 0; k < 3; k++)
                    {
                        int pivot = k;
                        for (int r = k + 1; r < 3; r++)
                            if (Math.Abs(matrix[r, k]) > Math.Abs(matrix[pivot, k]))
                                pivot = r;
                        if (matrix[pivot, k] == 0)
                        {
                            singular = true;
                            break;
                        }

                        for (int c = k; c < 4; c++)
                            (matrix[k, c], matrix[pivot, c]) = (matrix[pivot, c], matrix[k, c]);
                        double scale = matrix[k, k];
                        for (int c = k; c < 4; c++)
                            matrix[k, c] /= scale;
                        for (int r = 0; r < 3; r++)
                            if (r != k)
                            {
                                double factor = matrix[r, k];
                                for (int c = k; c < 4; c++)
                                    matrix[r, c] -= factor * matrix[k, c];
                            }
                    }

                    for (int k = 0; k < 3; k++)
                        results[k].Add(singular ? double.NaN : matrix[k, 3]);
                }

                return m.Children("output").Select(o => results[o.Attr("as", "yi0") switch
                {
                    "yi1" => 1,
                    "yi2" => 2,
                    _ => 0
                }].ToArray()).ToArray();
            }

            case "map":
            {
                double width = Last("mapWidth"), height = Last("mapHeight"), minx = Last("minX"), maxx = Last("maxX"), miny = Last("minY"), maxy = Last("maxY");
                if (new[]
                {
                    width,
                    height,
                    minx,
                    maxx,
                    miny,
                    maxy
                }.Any(v => !double.IsFinite(v)) || width < 1 || height < 1)
                    return[[], [], []];
                if (width * height > 10_000_000)
                    throw new InvalidOperationException("Map size exceeds safety limit.");
                int w = (int)width, h = (int)height;
                var xs = Get("x");
                var ys = Get("y");
                var zs = Get("z");
                var sums = new double[w * h];
                var counts = new double[w * h];
                int n = zs.Length == 0 ? Math.Min(xs.Length, ys.Length) : Math.Min(zs.Length, Math.Min(xs.Length, ys.Length));
                for (int i = 0; i < n; i++)
                {
                    double z = zs.Length == 0 ? 1 : zs[i];
                    if (!double.IsFinite(xs[i]) || !double.IsFinite(ys[i]) || !double.IsFinite(z))
                        continue;
                    double xx = (w - 1) * (xs[i] - minx) / (maxx - minx), yy = (h - 1) * (ys[i] - miny) / (maxy - miny);
                    if (!double.IsFinite(xx) || !double.IsFinite(yy))
                        continue;
                    int xi = (int)Math.Round(Math.Clamp(xx, -1, w), MidpointRounding.AwayFromZero), yi = (int)Math.Round(Math.Clamp(yy, -1, h), MidpointRounding.AwayFromZero);
                    if (xi < 0 || yi < 0 || xi >= w || yi >= h)
                        continue;
                    sums[yi * w + xi] += z;
                    counts[yi * w + xi]++;
                }

                var ox = new double[w * h];
                var oy = new double[w * h];
                var oz = new double[w * h];
                for (int i = 0; i < w * h; i++)
                {
                    ox[i] = minx + (i % w) * (maxx - minx) / (w - 1);
                    oy[i] = miny + (i / w) * (maxy - miny) / (h - 1);
                    oz[i] = m.Attr("zMode", "average") switch
                    {
                        "count" => counts[i],
                        "sum" => sums[i],
                        _ => sums[i] / counts[i]
                    };
                }

                return m.Children("output").Select(o => o.Attr("as") switch
                {
                    "x" => ox,
                    "y" => oy,
                    _ => oz
                }).ToArray();
            }

            case "eventstream":
            {
                var data = Get("data", 0);
                double threshold = Last("threshold", 0), distanceValue = Last("distance", 0), indexValue = Last("index", 0), skipValue = Last("skip", 0), last = Last("last");
                if (!double.IsFinite(distanceValue) || !double.IsFinite(indexValue) || !double.IsFinite(skipValue))
                    return m.Children("output").Select(_ => Array.Empty<double>()).ToArray();
                int distance = JavaInt(distanceValue), index = JavaInt(indexValue), skip = JavaInt(skipValue), i = 0;
                var events = new List<double>();
                string mode = m.Attr("triggerMode", "above").ToLowerInvariant();
                while (i < data.Length)
                {
                    if (skip > 0)
                    {
                        int steps = Math.Min(skip, data.Length - i);
                        skip -= steps;
                        i += steps;
                        last = data[i - 1];
                        continue;
                    }

                    double v = data[i];
                    bool triggered = mode switch
                    {
                        "above" => v > threshold,
                        "below" => v < threshold,
                        "aboveabsolute" => Math.Abs(v) > threshold,
                        "belowabsolute" => Math.Abs(v) < threshold,
                        "abovederivative" => v - last > threshold,
                        "belowderivative" => v - last < threshold,
                        "abovederivativeabsolute" => Math.Abs(v - last) > threshold,
                        "belowderivativeabsolute" => Math.Abs(v - last) < threshold,
                        _ => throw new FormatException("Unknown event trigger mode.")};
                    if (triggered)
                    {
                        events.Add(unchecked(i + index));
                        skip = distance;
                    }

                    last = v;
                    i++;
                }

                return m.Children("output").Select(o => o.Attr("as") switch
                {
                    "index" => new[] { (double)unchecked(index + i) },
                    "skip" => new[] { (double)skip },
                    "last" => new[] { last },
                    _ => events.ToArray()}).ToArray();
            }

            case "periodicity":
            {
                var axis = Get("x", 0);
                var data = Get("y", 1);
                int n = Math.Min(axis.Length, data.Length), dx = JavaInt(Last("dx", double.NaN, 2)), overlap = JavaInt(Last("overlap", 0));
                if (dx <= 0)
                    return m.Children("output").Select(_ => Array.Empty<double>()).ToArray();
                bool userRange = tags.Any(t => t.Attr("as")is "min" or "max");
                int min = JavaInt(Math.Floor(Last("min", 0))), max = JavaInt(Math.Ceiling(Last("max", int.MaxValue)));
                if (min < 0 || overlap < 0)
                    throw new FormatException("Negative periodicity search range or overlap is unsupported.");
                var times = new List<double>();
                var periods = new List<double>();
                for (int stepX = 0; stepX <= n - dx; stepX += dx)
                {
                    int x1 = Math.Max(0, stepX - overlap), x2 = Math.Min((long)stepX + dx + overlap, n)is var edge ? (int)edge : n;
                    max = Math.Min(max, x2 - x1);
                    int firstNegative = -1, maxPosition = -1, step = userRange ? 1 : 2;
                    double maxValue = double.NegativeInfinity, left = double.NegativeInfinity, right = double.NegativeInfinity, lastSum = double.NegativeInfinity;
                    for (int i = min; i < max; i += step)
                    {
                        double total = 0;
                        for (int j = x1; j < x2 - i; j++)
                            total += data[j] * data[j + i];
                        total /= x2 - x1 - i;
                        if (!userRange && firstNegative < 0)
                        {
                            if (total < 0)
                            {
                                firstNegative = i;
                                i = 3 * firstNegative + 1;
                                step = 1;
                            }
                        }
                        else if (!userRange && i > 5 * firstNegative)
                            break;
                        else if (userRange || i > 3 * firstNegative)
                        {
                            if (total > maxValue)
                            {
                                maxValue = total;
                                maxPosition = i;
                                left = lastSum;
                                right = double.NegativeInfinity;
                            }
                            else if (i == maxPosition + 1)
                                right = total;
                        }

                        lastSum = total;
                    }

                    double period = double.NaN;
                    if (maxPosition > 0 && maxValue > 0 && left > 0 && right > 0)
                    {
                        double fraction = 0.5 * (right - left) / (2 * maxValue - left - right);
                        period = axis[x1 + maxPosition] + 0.5 * fraction * (axis[x1 + maxPosition + 1] - axis[x1 + maxPosition - 1]) - axis[x1];
                    }

                    times.Add(axis[x1]);
                    periods.Add(period);
                }

                return m.Children("output").Select(o => o.Attr("as") == "period" ? periods.ToArray() : times.ToArray()).ToArray();
            }

            case "info":
                return m.Children("output").Select(o =>
                {
                    string key = o.Attr("as");
                    if (infoProvider == null || !infoProvider.TryGetValue(key, out var value))
                        throw new NotSupportedException($"Device information unavailable: {key}");
                    return new[]
                    {
                        value
                    };
                }).ToArray();
            case "imagedecode":
                return ImageAnalysis.Decode(x, m.Children("output").Select(o => o.Attr("as")).ToArray());
            case "gausssmooth":
            {
                var sigma = double.Parse(m.Attr("sigma", "3"), System.Globalization.CultureInfo.InvariantCulture);
                if (!double.IsFinite(sigma) || sigma <= 0)
                    throw new FormatException("Invalid Gaussian sigma.");
                int w = (int)Math.Floor(3 * sigma + 0.5);
                return[Enumerable.Range(0, x.Length).Select(i =>
                {
                    double s = 0, norm = 0;
                    for (int j = -w; j <= w; j++)
                        if (i + j >= 0 && i + j < x.Length)
                        {
                            var k = Math.Exp(-(j * j) / (2 * sigma * sigma));
                            s += k * x[i + j];
                            norm += k;
                        }

                    return s / norm;
                }).ToArray()];
            }

            default:
                throw new NotSupportedException($"Unsupported analysis module: {name}");
        }
    }

    static int JavaInt(double v) => double.IsNaN(v) ? 0 : v >= int.MaxValue ? int.MaxValue : v <= int.MinValue ? int.MinValue : (int)v;
    static double GcdLcm(double a, double b, bool lcm)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b) || a < 0 || b < 0 || a >= 18446744073709551616d || b >= 18446744073709551616d)
            return double.NaN;
        var aa = new System.Numerics.BigInteger(Math.Round(a, MidpointRounding.AwayFromZero));
        var bb = new System.Numerics.BigInteger(Math.Round(b, MidpointRounding.AwayFromZero));
        var gcd = System.Numerics.BigInteger.GreatestCommonDivisor(aa, bb);
        var result = lcm ? (gcd == 0 ? 0 : aa / gcd * bb) : gcd;
        return result >= (System.Numerics.BigInteger.One << 64) ? double.NaN : (double)result;
    }
}

public sealed record AnalysisTimeContext(double LinearTime, double Offset1970, double LinearOffset1970);
