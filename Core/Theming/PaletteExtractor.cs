using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace DesktopFences.Core.Theming
{
    public sealed record PaletteSwatch(Color Color, double Share, double Chroma, double Score)
    {
        public override string ToString() =>
            $"{ColorSpace.ToHex(Color)}  {Share * 100:F1}%  chroma {Chroma:F3}  score {Score:F3}";
    }

    public static class PaletteExtractor
    {
        private readonly record struct LabPoint(float L, float A, float B);

        public static IReadOnlyList<PaletteSwatch> Extract(
            byte[] bgraPixels, int width, int height, int clusters = 6, int maxIterations = 24)
        {
            if (bgraPixels is null || width <= 0 || height <= 0 || clusters <= 0 || maxIterations <= 0)
                return Array.Empty<PaletteSwatch>();

            var points = SampleToOklab(bgraPixels, width, height);
            if (points.Count < clusters) return Array.Empty<PaletteSwatch>();

            ReadOnlySpan<LabPoint> pointSpan = CollectionsMarshal.AsSpan(points);
            var centroids = InitialiseCentroids(pointSpan, clusters);
            var assignment = new int[pointSpan.Length];
            var sums = new (double L, double A, double B, int N)[clusters];

            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                bool moved = false;

                for (int i = 0; i < pointSpan.Length; i++)
                {
                    LabPoint p = pointSpan[i];
                    int best = 0;
                    float bestDistance = float.MaxValue;
                    for (int c = 0; c < clusters; c++)
                    {
                        float d = SquaredDistance(p, centroids[c]);
                        if (d < bestDistance) { bestDistance = d; best = c; }
                    }
                    if (assignment[i] != best) { assignment[i] = best; moved = true; }
                }

                if (!moved && iteration > 0) break;

                Array.Clear(sums);
                for (int i = 0; i < pointSpan.Length; i++)
                {
                    int c = assignment[i];
                    LabPoint p = pointSpan[i];
                    sums[c] = (sums[c].L + p.L, sums[c].A + p.A, sums[c].B + p.B, sums[c].N + 1);
                }
                for (int c = 0; c < clusters; c++)
                {
                    if (sums[c].N > 0)
                    {
                        centroids[c] = new LabPoint(
                            (float)(sums[c].L / sums[c].N),
                            (float)(sums[c].A / sums[c].N),
                            (float)(sums[c].B / sums[c].N));
                    }
                }
            }

            var counts = new int[clusters];
            foreach (int c in assignment) counts[c]++;
            double total = pointSpan.Length;

            var result = new List<PaletteSwatch>(clusters);
            for (int c = 0; c < clusters; c++)
            {
                if (counts[c] == 0) continue;

                LabPoint centroid = centroids[c];
                double chroma = Math.Sqrt(centroid.A * centroid.A + centroid.B * centroid.B);
                double share = counts[c] / total;

                double extremity = 1.0 - Math.Pow(Math.Abs(centroid.L - 0.5) * 2.0, 3);
                double score = share * (0.25 + chroma * 14.0) * Math.Max(0.15, extremity);

                result.Add(new PaletteSwatch(ColorSpace.OklabToRgb(centroid.L, centroid.A, centroid.B), share, chroma, score));
            }

            result.Sort((x, y) => y.Score.CompareTo(x.Score));
            return result;
        }

        public static Color? SelectSeed(IReadOnlyList<PaletteSwatch> palette)
        {
            if (palette is null || palette.Count == 0) return null;

            var usable = palette.FirstOrDefault(s => s.Chroma >= 0.02);
            return (usable ?? palette[0]).Color;
        }

        private static List<LabPoint> SampleToOklab(byte[] bgra, int width, int height)
        {
            const int TargetSamples = 8_192;
            int totalPixels = width * height;
            int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(totalPixels / (double)TargetSamples)));
            int capacity = Math.Min(TargetSamples, ((height + step - 1) / step) * ((width + step - 1) / step));

            var points = new List<LabPoint>(capacity);

            for (int y = 0; y < height; y += step)
            {
                int row = y * width * 4;
                for (int x = 0; x < width; x += step)
                {
                    int i = row + x * 4;
                    if (i + 3 >= bgra.Length) continue;
                    if (bgra[i + 3] < 8) continue;

                    var (L, a, b) = ColorSpace.RgbToOklab(bgra[i + 2], bgra[i + 1], bgra[i]);
                    points.Add(new LabPoint((float)L, (float)a, (float)b));
                }
            }
            return points;
        }

        private static LabPoint[] InitialiseCentroids(ReadOnlySpan<LabPoint> points, int k)
        {
            var rng = new Random(20260720);
            var centroids = new LabPoint[k];
            centroids[0] = points[rng.Next(points.Length)];

            var nearest = new float[points.Length];
            for (int i = 0; i < points.Length; i++) nearest[i] = SquaredDistance(points[i], centroids[0]);

            for (int c = 1; c < k; c++)
            {
                double sum = 0;
                for (int i = 0; i < nearest.Length; i++) sum += nearest[i];

                double target = rng.NextDouble() * sum;
                int chosen = nearest.Length - 1;
                double running = 0;
                for (int i = 0; i < nearest.Length; i++)
                {
                    running += nearest[i];
                    if (running >= target) { chosen = i; break; }
                }

                centroids[c] = points[chosen];
                for (int i = 0; i < points.Length; i++)
                    nearest[i] = Math.Min(nearest[i], SquaredDistance(points[i], centroids[c]));
            }
            return centroids;
        }

        private static float SquaredDistance(LabPoint p, LabPoint q)
        {
            float dL = p.L - q.L, dA = p.A - q.A, dB = p.B - q.B;
            return dL * dL + dA * dA + dB * dB;
        }
    }
}
