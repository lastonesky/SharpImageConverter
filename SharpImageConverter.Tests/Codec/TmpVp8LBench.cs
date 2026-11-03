using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Xunit;
using SharpImageConverter;
using SharpImageConverter.Formats.Webp;

namespace SharpImageConverter.Tests.Codec;

/// <summary>临时体积/耗时测量（分析用，完成后删除）。</summary>
[Collection("WebpEncodePolicy")]
public class TmpVp8LBench
{
    [Fact]
    public void Measure()
    {
        var samples = new (string Name, byte[] Rgba, int W, int H)[]
        {
            ("gradient_linear", GradientLinear(512, 512, 255), 512, 512),
            ("gradient_diag", GradientDiag(512, 512, 255), 512, 512),
            ("gradient_radial", GradientRadial(512, 512), 512, 512),
            ("alpha_soft", AlphaSoft(512, 512), 512, 512),
            ("alpha_logo", AlphaLogo(512, 512), 512, 512),
            ("alpha_binary", AlphaBinary(512, 512), 512, 512),
            ("photo_opaque", Photo(512, 512, 11), 512, 512),
            ("alpha_gradient_small", LegacyAlphaGradient(64, 48), 64, 48),
        };

        string carPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", "car.png");
        if (File.Exists(carPath))
        {
            var frame = ImageFrame.Load(carPath).ToRgba32();
            samples = Append(samples, ("car_png", frame.Pixels, frame.Width, frame.Height));
        }
        foreach (string name in new[] { "gradient_alpha.png", "transparent.png", "peppers_rgba.png", "scene.png" })
        {
            string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", name);
            if (!File.Exists(path)) continue;
            var frame = ImageFrame.Load(path).ToRgba32();
            samples = Append(samples, (name, frame.Pixels, frame.Width, frame.Height));
        }

        var sb = new StringBuilder();
        sb.AppendLine("sample,w,h,ours_bytes,lib_bytes,ratio,ours_ms,lib_ms,time_ratio,ours_ok");
        foreach (var s in samples)
        {
            byte[] ours = WebpEncoder.EncodeRgba(s.Rgba, s.W, s.H);
            byte[] lib = WebpCodec.EncodeLosslessRgbaForDispatch(s.Rgba, s.W, s.H);

            double oursMs = Time(() => WebpEncoder.EncodeRgba(s.Rgba, s.W, s.H));
            double libMs = Time(() => WebpCodec.EncodeLosslessRgbaForDispatch(s.Rgba, s.W, s.H));

            byte[] decoded = WebpCodec.DecodeRgba(ours, out int dw, out int dh);
            bool ok = dw == s.W && dh == s.H && PixelsEqual(s.Rgba, decoded);

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2},{3},{4},{5:F3},{6:F1},{7:F1},{8:F2},{9}",
                s.Name, s.W, s.H, ours.Length, lib.Length,
                (double)ours.Length / lib.Length, oursMs, libMs, oursMs / libMs, ok ? 1 : 0));
        }

        string outPath = Path.Combine(Path.GetTempPath(), "sic-vp8l-bench.csv");
        File.WriteAllText(outPath, sb.ToString());

        foreach (var line in sb.ToString().Split('\n'))
            Console.WriteLine(line);

        // ---- 候选配置拆解：定位差距落在哪一段 ----
        var probes = new (string Name, byte[] Rgba, int W, int H)[]
        {
            ("gradient_linear", GradientLinear(512, 512, 255), 512, 512),
            ("alpha_binary", AlphaBinary(512, 512), 512, 512),
            ("alpha_soft", AlphaSoft(512, 512), 512, 512),
            ("gradient_diag", GradientDiag(512, 512, 255), 512, 512),
        };
        foreach (string name in new[] { "transparent.png", "gradient_alpha.png", "peppers_rgba.png", "scene.png" })
        {
            string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "examples", name);
            if (!File.Exists(path)) continue;
            var frame = ImageFrame.Load(path).ToRgba32();
            probes = Append(probes, (name, frame.Pixels, frame.Width, frame.Height));
        }

        var configs = new (string Label, Vp8LEncoderOptions Options)[]
        {
            ("literal-only", Vp8LEncoderOptions.LiteralOnly),
            ("sg+pred(uniform)+lz", new(true, false, true, uniformPredictor: true)),
            ("sg+pred(auto)+lz(nocc)", new(true, false, true, autoPredictor: true, autoCrossColor: false)),
            ("sg+pred(auto)+lz+cc", new(true, false, true, autoPredictor: true)),
            ("sg+pred+lz+cache(nocc)", new(true, true, true, autoPredictor: true, autoCrossColor: false)),
            ("full(auto)", new(true, true, true, autoPredictor: true)),
            ("full(nometa)", new(true, true, true, autoPredictor: true, useMetaHuffman: false)),
            ("nocc(nometa)", new(true, true, true, autoPredictor: true, autoCrossColor: false, useMetaHuffman: false)),
        };
        sb.Clear();
        sb.AppendLine("probe,config,bytes,lib,header_bits,cache_bits,groups,meta_bits,table_bits,"
            + "token_bits,literals,cache_syms,copies,copied_px,m<8,m<16,m<32,m<64,m<256,m>=256,"
            + "pred_mode,pred_bits,tile_modes,cross_color");
        foreach (var p in probes)
        {
            foreach (var c in configs)
            {
                var diag = new Vp8LEncoder.Vp8LEncodeDiagnostics();
                byte[] enc = Vp8LEncoder.EncodeVp8L(p.Rgba, p.W, p.H, c.Options, diag);
                byte[] lib = WebpCodec.EncodeLosslessRgbaForDispatch(p.Rgba, p.W, p.H);
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},"
                    + "{14},{15},{16},{17},{18},{19},{20},{21},{22},{23}",
                    p.Name, c.Label, enc.Length, lib.Length,
                    diag.HeaderBits, diag.CacheBits, diag.NumGroups, diag.MetaImageBits,
                    diag.TableBits, diag.TokenBits, diag.LiteralCount, diag.CacheSymbolCount,
                    diag.CopyCount, diag.CopiedPixels,
                    diag.MatchLengthBuckets[0], diag.MatchLengthBuckets[1], diag.MatchLengthBuckets[2],
                    diag.MatchLengthBuckets[3], diag.MatchLengthBuckets[4], diag.MatchLengthBuckets[5],
                    diag.UniformPredictorMode, diag.PredictorBits, diag.UsesTileModes ? 1 : 0,
                    diag.HasCrossColor ? diag.CrossColorBits : 0);
                sb.AppendLine();
            }
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "sic-vp8l-diag.csv"), sb.ToString());
        foreach (var line in sb.ToString().Split('\n'))
            Console.WriteLine(line);

        // ---- predictor 模式全扫描（LZ77 开/关），验证"熵最优 ≠ 体积最优" ----
        sb.Clear();
        sb.AppendLine("probe,mode,bytes_nolz,token_bits_nolz,bytes_lz,copies,m>=256,pred_bits");
        foreach (var p in probes)
        {
            if (p.Name != "gradient_linear" && p.Name != "alpha_binary" && p.Name != "alpha_soft") continue;
            for (int mode = 0; mode < 14; mode++)
            {
                var set = new Vp8LEncoderOptions(true, false, false, autoPredictor: false,
                    autoCrossColor: false, forcedPredictorMode: mode);
                var d1 = new Vp8LEncoder.Vp8LEncodeDiagnostics();
                byte[] noLz = Vp8LEncoder.EncodeVp8L(p.Rgba, p.W, p.H, set, d1);

                var set2 = new Vp8LEncoderOptions(true, false, true, autoPredictor: false,
                    autoCrossColor: false, forcedPredictorMode: mode);
                var d2 = new Vp8LEncoder.Vp8LEncodeDiagnostics();
                byte[] withLz = Vp8LEncoder.EncodeVp8L(p.Rgba, p.W, p.H, set2, d2);

                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5},{6},{7}", p.Name, mode, noLz.Length, d1.TokenBits,
                    withLz.Length, d2.CopyCount, d2.MatchLengthBuckets[5], d2.PredictorBits);
                sb.AppendLine();
            }
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "sic-vp8l-modes.csv"), sb.ToString());
        foreach (var line in sb.ToString().Split('\n'))
            Console.WriteLine(line);

        // ---- 大图耗时构成 ----
        sb.Clear();
        sb.AppendLine("probe,config,ms,bytes");
        var timingConfigs = new (string Label, Vp8LEncoderOptions Options)[]
        {
            ("literal-only", Vp8LEncoderOptions.LiteralOnly),
            ("nocache+nocc", new(true, false, true, autoPredictor: true, autoCrossColor: false)),
            ("nocache+cc", new(true, false, true, autoPredictor: true)),
            ("cache+nocc", new(true, true, true, autoPredictor: true, autoCrossColor: false)),
            ("cache+cc", new(true, true, true, autoPredictor: true)),
            ("auto-select", Vp8LEncoderOptions.Auto),
        };
        foreach (var p in probes)
        {
            if (p.Name != "peppers_rgba.png" && p.Name != "scene.png") continue;
            foreach (var c in timingConfigs)
            {
                double ms = Time(() => Vp8LEncoder.EncodeVp8L(p.Rgba, p.W, p.H, c.Options));
                int bytes = Vp8LEncoder.EncodeVp8L(p.Rgba, p.W, p.H, c.Options).Length;
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2:F0},{3}", p.Name, c.Label, ms, bytes);
                sb.AppendLine();
            }
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "sic-vp8l-time.csv"), sb.ToString());
        foreach (var line in sb.ToString().Split('\n'))
            Console.WriteLine(line);
    }

    private static (string, byte[], int, int)[] Append((string Name, byte[] Rgba, int W, int H)[] a,
        (string Name, byte[] Rgba, int W, int H) b)
    {
        var result = new (string, byte[], int, int)[a.Length + 1];
        for (int i = 0; i < a.Length; i++) result[i] = a[i];
        result[a.Length] = b;
        return result;
    }

    private static double Time(Action action)
    {
        action();
        double best = double.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    private static bool PixelsEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static byte[] GradientLinear(int w, int h, byte alpha)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            px[o] = (byte)(x * 255 / (w - 1));
            px[o + 1] = (byte)(y * 255 / (h - 1));
            px[o + 2] = (byte)(220 - (x * 120 / (w - 1)));
            px[o + 3] = alpha;
        }
        return px;
    }

    private static byte[] GradientDiag(int w, int h, byte alpha)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            px[o] = (byte)((x + y) * 255 / (w + h - 2));
            px[o + 1] = (byte)((x * 2 + y) * 255 / (2 * w + h - 3));
            px[o + 2] = (byte)((y * 2 + x) * 255 / (2 * h + w - 3));
            px[o + 3] = alpha;
        }
        return px;
    }

    private static byte[] GradientRadial(int w, int h)
    {
        var px = new byte[w * h * 4];
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            double dx = (x - cx) / cx, dy = (y - cy) / cy;
            double r = Math.Min(1.0, Math.Sqrt(dx * dx + dy * dy));
            px[o] = (byte)Math.Round(255 * (1 - r));
            px[o + 1] = (byte)Math.Round(200 * r);
            px[o + 2] = (byte)Math.Round(120 + 100 * (1 - r));
            px[o + 3] = 255;
        }
        return px;
    }

    private static byte[] AlphaSoft(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            px[o] = (byte)(x * 255 / (w - 1));
            px[o + 1] = (byte)(y * 255 / (h - 1));
            px[o + 2] = (byte)((x + y) * 255 / (w + h - 2));
            px[o + 3] = (byte)((x * 255 / (w - 1)) + (y * 255 / (h - 1)) > 255
                ? 255
                : ((x * 255 / (w - 1)) + (y * 255 / (h - 1))));
        }
        return px;
    }

    private static byte[] AlphaLogo(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            double cx = w / 2.0, cy = h / 2.0;
            double dx = x - cx, dy = y - cy;
            double r = Math.Sqrt(dx * dx + dy * dy);
            bool inside = r < w * 0.42;
            double ramp = Math.Min(1.0, Math.Max(0.0, (w * 0.42 - r) / 6.0));
            px[o] = (byte)(120 + (x * 40 / (w - 1)));
            px[o + 1] = (byte)(60 + (y * 50 / (h - 1)));
            px[o + 2] = 200;
            px[o + 3] = inside ? (byte)Math.Round(255 * ramp + 0.0) : (byte)0;
        }
        return px;
    }

    private static byte[] AlphaBinary(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            bool inside = ((x / 32) + (y / 32)) % 2 == 0;
            px[o] = (byte)(x * 255 / (w - 1));
            px[o + 1] = (byte)(y * 255 / (h - 1));
            px[o + 2] = 160;
            px[o + 3] = inside ? (byte)255 : (byte)0;
        }
        return px;
    }

    private static byte[] Photo(int w, int h, int seed)
    {
        var px = new byte[w * h * 4];
        var rnd = new Random(seed);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            double fx = x / (double)(w - 1);
            double fy = y / (double)(h - 1);
            double wave = Math.Sin(fx * 6.28) * Math.Cos(fy * 3.14);
            px[o + 0] = ClampByte(128 + 90 * fx + 30 * wave + rnd.Next(-6, 7));
            px[o + 1] = ClampByte(96 + 110 * fy + 25 * wave + rnd.Next(-6, 7));
            px[o + 2] = ClampByte(150 + 40 * wave - 50 * fy + rnd.Next(-6, 7));
            px[o + 3] = 255;
        }
        return px;
    }

    private static byte[] LegacyAlphaGradient(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            px[o + 0] = (byte)((x * 3) & 0xFF);
            px[o + 1] = (byte)((y * 5) & 0xFF);
            px[o + 2] = (byte)(((x + y) * 2) & 0xFF);
            px[o + 3] = (byte)((x * 255 / Math.Max(1, w - 1)) ^ (y * 3));
        }
        return px;
    }

    private static byte ClampByte(double v) => v switch
    {
        < 0 => 0,
        > 255 => 255,
        _ => (byte)Math.Round(v),
    };
}
