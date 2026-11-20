using System;

namespace AwAUI.Css.Values;

/// <summary>CSS Color 4/5 的颜色空间（<c>color()</c> 的预定义空间与各颜色函数的空间）。</summary>
public enum CssColorSpace : byte
{
    /// <summary><c>srgb</c>（含 <c>rgb()</c>、hex 与命名色），分量 0–1（gamma 编码）。</summary>
    Srgb,
    /// <summary><c>srgb-linear</c>。</summary>
    SrgbLinear,
    /// <summary><c>display-p3</c>。</summary>
    DisplayP3,
    /// <summary><c>a98-rgb</c>。</summary>
    A98Rgb,
    /// <summary><c>prophoto-rgb</c>（D50）。</summary>
    ProphotoRgb,
    /// <summary><c>rec2020</c>。</summary>
    Rec2020,
    /// <summary><c>xyz-d50</c>。</summary>
    XyzD50,
    /// <summary><c>xyz-d65</c>（即 <c>xyz</c>）。</summary>
    XyzD65,
    /// <summary><c>lab()</c>（D50），L 0–100。</summary>
    Lab,
    /// <summary><c>lch()</c>（D50），L 0–100，C ≥ 0，H 度。</summary>
    Lch,
    /// <summary><c>oklab()</c>，L 0–1。</summary>
    Oklab,
    /// <summary><c>oklch()</c>，L 0–1，C ≥ 0，H 度。</summary>
    Oklch,
    /// <summary><c>hsl()</c>，H 度，S/L 0–100。</summary>
    Hsl,
    /// <summary><c>hwb()</c>，H 度，W/B 0–100。</summary>
    Hwb,
}

/// <summary>色相插值方法（CSS Color 4 §12.4）。</summary>
public enum CssHueInterpolation : byte
{
    Shorter,
    Longer,
    Increasing,
    Decreasing,
}

/// <summary>
/// 任意颜色空间中的颜色（双精度，未做色域映射），带「缺失分量」（<c>none</c>）掩码：位 0–2 为三个通道，位 3 为 alpha。
/// 计算与插值都在这一形式上进行，最后才换算为 sRGB 并裁剪（见 <see cref="CssColorMath"/>）。
/// </summary>
internal readonly struct WideColor : IEquatable<WideColor>
{
    public WideColor(CssColorSpace space, double c0, double c1, double c2, double alpha, byte missing = 0)
    {
        Space = space;
        C0 = c0;
        C1 = c1;
        C2 = c2;
        Alpha = alpha;
        Missing = missing;
    }

    public CssColorSpace Space { get; }
    public double C0 { get; }
    public double C1 { get; }
    public double C2 { get; }
    public double Alpha { get; }

    /// <summary>缺失分量的位掩码（位 3 = alpha）。</summary>
    public byte Missing { get; }

    public bool IsMissing(int i) => (Missing & (1 << i)) != 0;

    public double this[int i] => i switch { 0 => C0, 1 => C1, 2 => C2, _ => Alpha };

    public static WideColor FromSrgb(Color c) => new(CssColorSpace.Srgb, c.R, c.G, c.B, c.A);

    public bool Equals(WideColor o) => Space == o.Space && C0.Equals(o.C0) && C1.Equals(o.C1) && C2.Equals(o.C2) && Alpha.Equals(o.Alpha) && Missing == o.Missing;
    public override bool Equals(object? obj) => obj is WideColor w && Equals(w);
    public override int GetHashCode() => HashCodeCompat.Combine((int)Space, C0.GetHashCode(), C1.GetHashCode(), C2.GetHashCode()) ^ Alpha.GetHashCode() ^ Missing;
}

/// <summary>
/// 颜色空间换算、插值（<c>color-mix()</c>）与色域映射（CSS Color 4 §10–§13，矩阵取自规范的示例代码）。
/// <para>
/// 渲染用的 sRGB 结果按分量<b>裁剪</b>到 [0, 1]：这与 Chromium、Firefox、WebKit 的实际行为一致（shadcn 的
/// <c>oklch(0.577 0.245 27.325)</c> 在 Chrome 中渲染为 <c>rgb(231 0 11)</c>）。规范推荐的「CSS 色域映射算法」
/// （在 OkLCh 中二分降低色度，ΔEOK &lt; 0.02）由 <see cref="GamutMapToSrgb"/> 提供，供需要它的调用方使用（如广色域到 sRGB 的取色器）。
/// </para>
/// </summary>
public static class CssColorMath
{
    // ---------------- 矩阵（CSS Color 4 conversions.js） ----------------

    static readonly double[] LinSrgbToXyz =
    {
        506752.0 / 1228815, 87881.0 / 245763, 12673.0 / 70218,
        87098.0 / 409605, 175762.0 / 245763, 12673.0 / 175545,
        7918.0 / 409605, 87881.0 / 737289, 1001167.0 / 1053270,
    };

    static readonly double[] XyzToLinSrgb =
    {
        12831.0 / 3959, -329.0 / 214, -1974.0 / 3959,
        -851781.0 / 878810, 1648619.0 / 878810, 36519.0 / 878810,
        705.0 / 12673, -2585.0 / 12673, 705.0 / 667,
    };

    static readonly double[] LinP3ToXyz =
    {
        608311.0 / 1250200, 189793.0 / 714400, 198249.0 / 1000160,
        35783.0 / 156275, 247089.0 / 357200, 198249.0 / 2500400,
        0, 32229.0 / 714400, 5220557.0 / 5000800,
    };

    static readonly double[] XyzToLinP3 =
    {
        446124.0 / 178915, -333277.0 / 357830, -72051.0 / 178915,
        -14852.0 / 17905, 63121.0 / 35810, 423.0 / 17905,
        11844.0 / 330415, -50337.0 / 660830, 316169.0 / 330415,
    };

    static readonly double[] LinProphotoToXyzD50 =
    {
        0.79776664490064230, 0.13518129740053308, 0.03134773412839220,
        0.28807482881940130, 0.71183523424187300, 0.00008993693872564,
        0, 0, 0.82510460251046020,
    };

    static readonly double[] XyzD50ToLinProphoto =
    {
        1.34578688164715830, -0.25557208737979464, -0.05110186497554526,
        -0.54463070512490190, 1.50824774284514680, 0.02052744743642139,
        0, 0, 1.21196754563894520,
    };

    static readonly double[] LinA98ToXyz =
    {
        573536.0 / 994567, 263643.0 / 1420810, 187206.0 / 994567,
        591459.0 / 1989134, 6239551.0 / 9945670, 374412.0 / 4972835,
        53769.0 / 1989134, 351524.0 / 4972835, 4929758.0 / 4972835,
    };

    static readonly double[] XyzToLinA98 =
    {
        1829569.0 / 896150, -506331.0 / 896150, -308931.0 / 896150,
        -851781.0 / 878810, 1648619.0 / 878810, 36519.0 / 878810,
        16779.0 / 1248040, -147721.0 / 1248040, 1266979.0 / 1248040,
    };

    static readonly double[] Lin2020ToXyz =
    {
        63426534.0 / 99577255, 20160776.0 / 139408157, 47086771.0 / 278816314,
        26158966.0 / 99577255, 472592308.0 / 697040785, 8267143.0 / 139408157,
        0, 19567812.0 / 697040785, 295819943.0 / 278816314,
    };

    static readonly double[] XyzToLin2020 =
    {
        30757411.0 / 17917100, -6372589.0 / 17917100, -4539589.0 / 17917100,
        -19765991.0 / 29648200, 47925759.0 / 29648200, 467509.0 / 29648200,
        792561.0 / 44930125, -1921689.0 / 44930125, 42328811.0 / 44930125,
    };

    static readonly double[] D65ToD50 =
    {
        1.0479297925449969, 0.022946870601609652, -0.05019226628920524,
        0.02962780877005599, 0.9904344267538799, -0.017073799063418826,
        -0.009243040646204504, 0.015055191490298152, 0.7518742814281371,
    };

    static readonly double[] D50ToD65 =
    {
        0.955473421488075, -0.02309845494876471, 0.06325924320057072,
        -0.0283697093338637, 1.0099953980813041, 0.021041441191917323,
        0.012314014864481998, -0.020507649298898964, 1.330365926242124,
    };

    static readonly double[] XyzToLms =
    {
        0.8190224379967030, 0.3619062600528904, -0.1288737815209879,
        0.0329836539323885, 0.9292868615863434, 0.0361446663506424,
        0.0481771893596242, 0.2642395317527308, 0.6335478284694309,
    };

    static readonly double[] LmsToOklab =
    {
        0.2104542683093140, 0.7936177747023054, -0.0040720430116193,
        1.9779985324311684, -2.4285922420485799, 0.4505937096174110,
        0.0259040424655478, 0.7827717124575296, -0.8086757549230774,
    };

    static readonly double[] LmsToXyz =
    {
        1.2268798758459243, -0.5578149944602171, 0.2813910456659647,
        -0.0405757452148008, 1.1122868032803170, -0.0717110580655164,
        -0.0763729366746601, -0.4214933324022432, 1.5869240198367816,
    };

    static readonly double[] OklabToLms =
    {
        1.0000000000000000, 0.3963377773761749, 0.2158037573099136,
        1.0000000000000000, -0.1055613458156586, -0.0638541728258133,
        1.0000000000000000, -0.0894841775298119, -1.2914855480194092,
    };

    static readonly double D50X = 0.3457 / 0.3585, D50Z = (1.0 - 0.3457 - 0.3585) / 0.3585;
    const double LabEpsilon = 216.0 / 24389, LabKappa = 24389.0 / 27;

    static void Mul(double[] m, ref double a, ref double b, ref double c)
    {
        double x = m[0] * a + m[1] * b + m[2] * c;
        double y = m[3] * a + m[4] * b + m[5] * c;
        double z = m[6] * a + m[7] * b + m[8] * c;
        a = x;
        b = y;
        c = z;
    }

    // ---------------- 传递函数 ----------------

    static double SrgbToLinear(double v)
    {
        double a = Math.Abs(v);
        return a <= 0.04045 ? v / 12.92 : Math.Sign(v) * Math.Pow((a + 0.055) / 1.055, 2.4);
    }

    static double LinearToSrgb(double v)
    {
        double a = Math.Abs(v);
        return a > 0.0031308 ? Math.Sign(v) * (1.055 * Math.Pow(a, 1 / 2.4) - 0.055) : 12.92 * v;
    }

    static double A98ToLinear(double v) => Math.Sign(v) * Math.Pow(Math.Abs(v), 563.0 / 256);
    static double LinearToA98(double v) => Math.Sign(v) * Math.Pow(Math.Abs(v), 256.0 / 563);

    static double ProphotoToLinear(double v)
    {
        double a = Math.Abs(v);
        return a <= 16.0 / 512 ? v / 16 : Math.Sign(v) * Math.Pow(a, 1.8);
    }

    static double LinearToProphoto(double v)
    {
        double a = Math.Abs(v);
        return a >= 1.0 / 512 ? Math.Sign(v) * Math.Pow(a, 1 / 1.8) : 16 * v;
    }

    const double Rec2020Alpha = 1.09929682680944, Rec2020Beta = 0.018053968510807;

    static double Rec2020ToLinear(double v)
    {
        double a = Math.Abs(v);
        return a < Rec2020Beta * 4.5 ? v / 4.5 : Math.Sign(v) * Math.Pow((a + Rec2020Alpha - 1) / Rec2020Alpha, 1 / 0.45);
    }

    static double LinearToRec2020(double v)
    {
        double a = Math.Abs(v);
        return a > Rec2020Beta ? Math.Sign(v) * (Rec2020Alpha * Math.Pow(a, 0.45) - (Rec2020Alpha - 1)) : 4.5 * v;
    }

    static double Cbrt(double v) => v < 0 ? -Math.Pow(-v, 1.0 / 3) : Math.Pow(v, 1.0 / 3);

    // ---------------- 空间 ↔ XYZ-D65 ----------------

    /// <summary>把颜色的三个分量换算到 XYZ-D65（缺失分量按 0）。</summary>
    internal static void ToXyzD65(CssColorSpace space, double c0, double c1, double c2, out double x, out double y, out double z)
    {
        switch (space)
        {
            case CssColorSpace.Srgb:
            case CssColorSpace.Hsl:
            case CssColorSpace.Hwb:
                if (space == CssColorSpace.Hsl) HslToSrgb(c0, c1, c2, out c0, out c1, out c2);
                else if (space == CssColorSpace.Hwb) HwbToSrgb(c0, c1, c2, out c0, out c1, out c2);
                c0 = SrgbToLinear(c0); c1 = SrgbToLinear(c1); c2 = SrgbToLinear(c2);
                Mul(LinSrgbToXyz, ref c0, ref c1, ref c2);
                break;
            case CssColorSpace.SrgbLinear:
                Mul(LinSrgbToXyz, ref c0, ref c1, ref c2);
                break;
            case CssColorSpace.DisplayP3:
                c0 = SrgbToLinear(c0); c1 = SrgbToLinear(c1); c2 = SrgbToLinear(c2);
                Mul(LinP3ToXyz, ref c0, ref c1, ref c2);
                break;
            case CssColorSpace.A98Rgb:
                c0 = A98ToLinear(c0); c1 = A98ToLinear(c1); c2 = A98ToLinear(c2);
                Mul(LinA98ToXyz, ref c0, ref c1, ref c2);
                break;
            case CssColorSpace.ProphotoRgb:
                c0 = ProphotoToLinear(c0); c1 = ProphotoToLinear(c1); c2 = ProphotoToLinear(c2);
                Mul(LinProphotoToXyzD50, ref c0, ref c1, ref c2);
                Mul(D50ToD65, ref c0, ref c1, ref c2);
                break;
            case CssColorSpace.Rec2020:
                c0 = Rec2020ToLinear(c0); c1 = Rec2020ToLinear(c1); c2 = Rec2020ToLinear(c2);
                Mul(Lin2020ToXyz, ref c0, ref c1, ref c2);
                break;
            case CssColorSpace.XyzD50:
                Mul(D50ToD65, ref c0, ref c1, ref c2);
                break;
            case CssColorSpace.XyzD65:
                break;
            case CssColorSpace.Lab:
            case CssColorSpace.Lch:
                if (space == CssColorSpace.Lch) PolarToRect(c0, c1, c2, out c0, out c1, out c2);
                LabToXyzD50(c0, c1, c2, out c0, out c1, out c2);
                Mul(D50ToD65, ref c0, ref c1, ref c2);
                break;
            case CssColorSpace.Oklab:
            case CssColorSpace.Oklch:
                if (space == CssColorSpace.Oklch) PolarToRect(c0, c1, c2, out c0, out c1, out c2);
                Mul(OklabToLms, ref c0, ref c1, ref c2);
                c0 = c0 * c0 * c0; c1 = c1 * c1 * c1; c2 = c2 * c2 * c2;
                Mul(LmsToXyz, ref c0, ref c1, ref c2);
                break;
        }
        x = c0;
        y = c1;
        z = c2;
    }

    /// <summary>把 XYZ-D65 换算到目标空间。极坐标空间中色度极小（无彩色）时色相记为 0，由调用方标为缺失。</summary>
    internal static void FromXyzD65(CssColorSpace space, double x, double y, double z, out double c0, out double c1, out double c2)
    {
        switch (space)
        {
            case CssColorSpace.Srgb:
            case CssColorSpace.Hsl:
            case CssColorSpace.Hwb:
                Mul(XyzToLinSrgb, ref x, ref y, ref z);
                x = LinearToSrgb(x); y = LinearToSrgb(y); z = LinearToSrgb(z);
                if (space == CssColorSpace.Hsl) { SrgbToHsl(x, y, z, out x, out y, out z); }
                else if (space == CssColorSpace.Hwb) { SrgbToHwb(x, y, z, out x, out y, out z); }
                break;
            case CssColorSpace.SrgbLinear:
                Mul(XyzToLinSrgb, ref x, ref y, ref z);
                break;
            case CssColorSpace.DisplayP3:
                Mul(XyzToLinP3, ref x, ref y, ref z);
                x = LinearToSrgb(x); y = LinearToSrgb(y); z = LinearToSrgb(z);
                break;
            case CssColorSpace.A98Rgb:
                Mul(XyzToLinA98, ref x, ref y, ref z);
                x = LinearToA98(x); y = LinearToA98(y); z = LinearToA98(z);
                break;
            case CssColorSpace.ProphotoRgb:
                Mul(D65ToD50, ref x, ref y, ref z);
                Mul(XyzD50ToLinProphoto, ref x, ref y, ref z);
                x = LinearToProphoto(x); y = LinearToProphoto(y); z = LinearToProphoto(z);
                break;
            case CssColorSpace.Rec2020:
                Mul(XyzToLin2020, ref x, ref y, ref z);
                x = LinearToRec2020(x); y = LinearToRec2020(y); z = LinearToRec2020(z);
                break;
            case CssColorSpace.XyzD50:
                Mul(D65ToD50, ref x, ref y, ref z);
                break;
            case CssColorSpace.XyzD65:
                break;
            case CssColorSpace.Lab:
            case CssColorSpace.Lch:
                Mul(D65ToD50, ref x, ref y, ref z);
                XyzD50ToLab(x, y, z, out x, out y, out z);
                if (space == CssColorSpace.Lch) RectToPolar(x, y, z, out x, out y, out z);
                break;
            case CssColorSpace.Oklab:
            case CssColorSpace.Oklch:
                Mul(XyzToLms, ref x, ref y, ref z);
                x = Cbrt(x); y = Cbrt(y); z = Cbrt(z);
                Mul(LmsToOklab, ref x, ref y, ref z);
                if (space == CssColorSpace.Oklch) RectToPolar(x, y, z, out x, out y, out z);
                break;
        }
        c0 = x;
        c1 = y;
        c2 = z;
    }

    static void LabToXyzD50(double l, double a, double b, out double x, out double y, out double z)
    {
        double f1 = (l + 16) / 116;
        double f0 = a / 500 + f1;
        double f2 = f1 - b / 200;
        double x3 = f0 * f0 * f0, z3 = f2 * f2 * f2;
        x = (x3 > LabEpsilon ? x3 : (116 * f0 - 16) / LabKappa) * D50X;
        y = l > LabKappa * LabEpsilon ? Math.Pow((l + 16) / 116, 3) : l / LabKappa;
        z = (z3 > LabEpsilon ? z3 : (116 * f2 - 16) / LabKappa) * D50Z;
    }

    static void XyzD50ToLab(double x, double y, double z, out double l, out double a, out double b)
    {
        double F(double v) => v > LabEpsilon ? Cbrt(v) : (LabKappa * v + 16) / 116;
        double f0 = F(x / D50X), f1 = F(y), f2 = F(z / D50Z);
        l = 116 * f1 - 16;
        a = 500 * (f0 - f1);
        b = 200 * (f1 - f2);
    }

    static void PolarToRect(double l, double c, double h, out double l2, out double a, out double b)
    {
        double rad = h * Math.PI / 180;
        l2 = l;
        a = c * Math.Cos(rad);
        b = c * Math.Sin(rad);
    }

    static void RectToPolar(double l, double a, double b, out double l2, out double c, out double h)
    {
        l2 = l;
        c = Math.Sqrt(a * a + b * b);
        h = Math.Atan2(b, a) * 180 / Math.PI;
        if (h < 0) h += 360;
    }

    /// <summary>CSS Color 4 §7.1 的 hslToRgb（S、L 为 0–100）。</summary>
    internal static void HslToSrgb(double h, double s, double l, out double r, out double g, out double b)
    {
        h %= 360;
        if (h < 0) h += 360;
        s /= 100;
        l /= 100;
        double a = s * Math.Min(l, 1 - l);
        double F(double n)
        {
            double k = (n + h / 30) % 12;
            return l - a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1));
        }
        r = F(0);
        g = F(8);
        b = F(4);
    }

    /// <summary>CSS Color 4 的 rgbToHsl（结果 S、L 为 0–100；无彩色时色相为 0）。</summary>
    static void SrgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        h = 0;
        s = 0;
        l = (min + max) / 2;
        double d = max - min;
        if (d != 0)
        {
            s = l == 0 || l == 1 ? 0 : (max - l) / Math.Min(l, 1 - l);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        // 负饱和度（广色域输入）按规范翻转色相
        if (s < 0)
        {
            h += 180;
            s = Math.Abs(s);
        }
        if (h >= 360) h -= 360;
        s *= 100;
        l *= 100;
    }

    static void HwbToSrgb(double h, double w, double bl, out double r, out double g, out double b)
    {
        w /= 100;
        bl /= 100;
        if (w + bl >= 1)
        {
            double gray = w / (w + bl);
            r = g = b = gray;
            return;
        }
        HslToSrgb(h, 100, 50, out r, out g, out b);
        double k = 1 - w - bl;
        r = r * k + w;
        g = g * k + w;
        b = b * k + w;
    }

    static void SrgbToHwb(double r, double g, double b, out double h, out double w, out double bl)
    {
        SrgbToHsl(r, g, b, out h, out _, out _);
        w = Math.Min(r, Math.Min(g, b)) * 100;
        bl = (1 - Math.Max(r, Math.Max(g, b))) * 100;
    }

    // ---------------- 公共 API ----------------

    /// <summary>在颜色空间之间换算（不做色域映射）。</summary>
    public static void Convert(CssColorSpace from, double c0, double c1, double c2, CssColorSpace to, out double r0, out double r1, out double r2)
    {
        if (from == to)
        {
            r0 = c0;
            r1 = c1;
            r2 = c2;
            return;
        }
        // 同一家族内直接换算（sRGB/HSL/HWB、Lab/LCH、OkLab/OkLCh），不经 XYZ 往返：
        // 往返的浮点误差会让白色在 HSL 中得到非零饱和度（色相不再「无效」），使 color-mix(in hsl, white, …) 偏离浏览器。
        int family = Family(from);
        if (family != 0 && family == Family(to))
        {
            ConvertWithinFamily(from, c0, c1, c2, to, out r0, out r1, out r2);
            return;
        }
        ToXyzD65(from, c0, c1, c2, out var x, out var y, out var z);
        FromXyzD65(to, x, y, z, out r0, out r1, out r2);
    }

    static int Family(CssColorSpace s) => s switch
    {
        CssColorSpace.Srgb or CssColorSpace.Hsl or CssColorSpace.Hwb => 1,
        CssColorSpace.Lab or CssColorSpace.Lch => 2,
        CssColorSpace.Oklab or CssColorSpace.Oklch => 3,
        _ => 0,
    };

    static void ConvertWithinFamily(CssColorSpace from, double c0, double c1, double c2, CssColorSpace to, out double r0, out double r1, out double r2)
    {
        // 先到家族的「直角」形式（sRGB / Lab / OkLab），再到目标
        switch (from)
        {
            case CssColorSpace.Hsl: HslToSrgb(c0, c1, c2, out c0, out c1, out c2); break;
            case CssColorSpace.Hwb: HwbToSrgb(c0, c1, c2, out c0, out c1, out c2); break;
            case CssColorSpace.Lch:
            case CssColorSpace.Oklch: PolarToRect(c0, c1, c2, out c0, out c1, out c2); break;
        }
        switch (to)
        {
            case CssColorSpace.Hsl: SrgbToHsl(c0, c1, c2, out c0, out c1, out c2); break;
            case CssColorSpace.Hwb: SrgbToHwb(c0, c1, c2, out c0, out c1, out c2); break;
            case CssColorSpace.Lch:
            case CssColorSpace.Oklch: RectToPolar(c0, c1, c2, out c0, out c1, out c2); break;
        }
        r0 = c0;
        r1 = c1;
        r2 = c2;
    }

    /// <summary>换算为 sRGB 并逐分量裁剪到 [0, 1]（浏览器的渲染行为）。</summary>
    public static Color ToSrgbClipped(CssColorSpace space, double c0, double c1, double c2, double alpha)
    {
        Convert(space, c0, c1, c2, CssColorSpace.Srgb, out var r, out var g, out var b);
        return new Color(Clip01(r), Clip01(g), Clip01(b), Clip01(alpha));
    }

    static float Clip01(double v) => double.IsNaN(v) ? 0f : (float)(v < 0 ? 0 : v > 1 ? 1 : v);

    /// <summary>
    /// CSS Color 4 §13.2「CSS 色域映射算法」映射到 sRGB：在 OkLCh 中保持亮度与色相、二分降低色度，
    /// 直到裁剪结果与当前颜色的 ΔEOK 小于 0.02。亮度 ≥ 1 得到白色，≤ 0 得到黑色。
    /// </summary>
    public static Color GamutMapToSrgb(CssColorSpace space, double c0, double c1, double c2, double alpha)
    {
        Convert(space, c0, c1, c2, CssColorSpace.Srgb, out var r, out var g, out var b);
        float a = Clip01(alpha);
        if (InUnit(r) && InUnit(g) && InUnit(b)) return new Color((float)r, (float)g, (float)b, a);
        Convert(space, c0, c1, c2, CssColorSpace.Oklch, out var l, out var c, out var h);
        if (l >= 1) return new Color(1, 1, 1, a);
        if (l <= 0) return new Color(0, 0, 0, a);
        const double Jnd = 0.02, Epsilon = 0.0001;
        var clipped = ClipOklch(l, c, h);
        if (DeltaEOk(clipped, l, c, h) < Jnd) return ToColor(clipped, a);
        double min = 0, max = c;
        bool minInGamut = true;
        var current = clipped;
        int guard = 0;
        while (max - min > Epsilon && guard++ < 64)
        {
            double chroma = (min + max) / 2;
            Convert(CssColorSpace.Oklch, l, chroma, h, CssColorSpace.Srgb, out r, out g, out b);
            if (minInGamut && InUnit(r) && InUnit(g) && InUnit(b))
            {
                min = chroma;
                continue;
            }
            current = ClipOklch(l, chroma, h);
            double e = DeltaEOk(current, l, chroma, h);
            if (e < Jnd)
            {
                if (Jnd - e < Epsilon) return ToColor(current, a);
                minInGamut = false;
                min = chroma;
            }
            else max = chroma;
        }
        return ToColor(current, a);

        static bool InUnit(double v) => v >= -1e-9 && v <= 1 + 1e-9;
        static (double R, double G, double B) ClipOklch(double l, double c, double h)
        {
            Convert(CssColorSpace.Oklch, l, c, h, CssColorSpace.Srgb, out var r, out var g, out var b);
            return (Math.Max(0, Math.Min(1, r)), Math.Max(0, Math.Min(1, g)), Math.Max(0, Math.Min(1, b)));
        }
        static double DeltaEOk((double R, double G, double B) rgb, double l, double c, double h)
        {
            Convert(CssColorSpace.Srgb, rgb.R, rgb.G, rgb.B, CssColorSpace.Oklab, out var l1, out var a1, out var b1);
            Convert(CssColorSpace.Oklch, l, c, h, CssColorSpace.Oklab, out var l2, out var a2, out var b2);
            return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
        }
        static Color ToColor((double R, double G, double B) rgb, float a) => new((float)rgb.R, (float)rgb.G, (float)rgb.B, a);
    }

    // ---------------- 内部：WideColor 的换算与插值 ----------------

    static bool IsPolar(CssColorSpace s) => s is CssColorSpace.Lch or CssColorSpace.Oklch or CssColorSpace.Hsl or CssColorSpace.Hwb;

    /// <summary>色相分量下标；非极坐标空间为 -1。</summary>
    static int HueIndex(CssColorSpace s) => s switch
    {
        CssColorSpace.Lch or CssColorSpace.Oklch => 2,
        CssColorSpace.Hsl or CssColorSpace.Hwb => 0,
        _ => -1,
    };

    /// <summary>
    /// 分量的「类别」（CSS Color 4 §12.2「相似分量」），用于在换算时携带缺失分量：
    /// 1 = 红/X，2 = 绿/Y，3 = 蓝/Z，4 = 亮度，5 = 彩度（C/S），6 = 色相，7 = 对立色 a，8 = 对立色 b；0 = 无对应。
    /// </summary>
    static int Category(CssColorSpace s, int i)
    {
        switch (s)
        {
            case CssColorSpace.Srgb:
            case CssColorSpace.SrgbLinear:
            case CssColorSpace.DisplayP3:
            case CssColorSpace.A98Rgb:
            case CssColorSpace.ProphotoRgb:
            case CssColorSpace.Rec2020:
            case CssColorSpace.XyzD50:
            case CssColorSpace.XyzD65:
                return i + 1;
            case CssColorSpace.Lab:
            case CssColorSpace.Oklab:
                return i == 0 ? 4 : i == 1 ? 7 : 8;
            case CssColorSpace.Lch:
            case CssColorSpace.Oklch:
                return i == 0 ? 4 : i == 1 ? 5 : 6;
            case CssColorSpace.Hsl:
                return i == 0 ? 6 : i == 1 ? 5 : 4;
            default: // hwb
                return i == 0 ? 6 : 0;
        }
    }

    /// <summary>
    /// 换算到目标空间：缺失分量按 0 参与换算，相似分量继续标为缺失；换算后极坐标空间中无彩色的色相视为缺失（powerless）。
    /// 已在目标空间中的颜色不做换算，也<b>不</b>把无效色相标为缺失（CSS Color 4 §4.4：只在换算时转为缺失；
    /// Chrome 中 <c>color-mix(in oklch, oklch(0.5 0 30), oklch(0.7 0.2 120))</c> 的色相为 75）。
    /// </summary>
    internal static WideColor ConvertWide(in WideColor c, CssColorSpace to)
    {
        if (c.Space == to) return c;
        Convert(c.Space, c.IsMissing(0) ? 0 : c.C0, c.IsMissing(1) ? 0 : c.C1, c.IsMissing(2) ? 0 : c.C2, to, out var r0, out var r1, out var r2);
        byte missing = (byte)(c.Missing & 8);
        for (int i = 0; i < 3; i++)
        {
            if (!c.IsMissing(i)) continue;
            int cat = Category(c.Space, i);
            if (cat == 0) continue;
            for (int j = 0; j < 3; j++)
                if (Category(to, j) == cat) missing |= (byte)(1 << j);
        }
        return MarkPowerless(new WideColor(to, r0, r1, r2, c.Alpha, missing));
    }

    static WideColor MarkPowerless(in WideColor c)
    {
        int hi = HueIndex(c.Space);
        if (hi < 0 || c.IsMissing(hi)) return c;
        bool powerless = c.Space switch
        {
            CssColorSpace.Lch => !c.IsMissing(1) && Math.Abs(c.C1) < 0.0015,
            CssColorSpace.Oklch => !c.IsMissing(1) && Math.Abs(c.C1) < 0.000004,
            CssColorSpace.Hsl => !c.IsMissing(1) && Math.Abs(c.C1) < 1e-5,
            _ => !c.IsMissing(1) && !c.IsMissing(2) && c.C1 + c.C2 >= 100 - 1e-5,
        };
        return powerless ? new WideColor(c.Space, c.C0, c.C1, c.C2, c.Alpha, (byte)(c.Missing | (1 << hi))) : c;
    }

    /// <summary>得到用于渲染的 sRGB（缺失分量按 0，逐分量裁剪）。</summary>
    internal static Color ToSrgb(in WideColor c)
        => ToSrgbClipped(c.Space, c.IsMissing(0) ? 0 : c.C0, c.IsMissing(1) ? 0 : c.C1, c.IsMissing(2) ? 0 : c.C2, c.IsMissing(3) ? 0 : c.Alpha);

    /// <summary>
    /// CSS Color 4 §12 的插值：换算到插值空间、缺失分量互补、色相修正、预乘 alpha 后线性插值。
    /// <paramref name="t"/> 为第二个颜色的权重（0–1）。
    /// </summary>
    internal static WideColor Interpolate(in WideColor a0, in WideColor b0, double t, CssColorSpace space, CssHueInterpolation hue)
    {
        var a = ConvertWide(a0, space);
        var b = ConvertWide(b0, space);
        Span<double> ca = stackalloc double[4];
        Span<double> cb = stackalloc double[4];
        byte missing = 0;
        for (int i = 0; i < 4; i++)
        {
            bool ma = a.IsMissing(i), mb = b.IsMissing(i);
            double va = a[i], vb = b[i];
            if (ma && mb) { missing |= (byte)(1 << i); va = vb = 0; }
            else if (ma) va = vb;
            else if (mb) vb = va;
            ca[i] = va;
            cb[i] = vb;
        }
        int hi = HueIndex(space);
        if (hi >= 0 && (missing & (1 << hi)) == 0)
        {
            double h1 = Normalize(ca[hi]), h2 = Normalize(cb[hi]);
            double d = h2 - h1;
            switch (hue)
            {
                case CssHueInterpolation.Shorter:
                    if (d > 180) h1 += 360;
                    else if (d < -180) h2 += 360;
                    break;
                case CssHueInterpolation.Longer:
                    if (d > 0 && d < 180) h1 += 360;
                    else if (d > -180 && d <= 0) h2 += 360;
                    break;
                case CssHueInterpolation.Increasing:
                    if (d < 0) h2 += 360;
                    break;
                case CssHueInterpolation.Decreasing:
                    if (d > 0) h1 += 360;
                    break;
            }
            ca[hi] = h1;
            cb[hi] = h2;
        }
        // 预乘 alpha（色相不预乘）
        double alphaA = ca[3], alphaB = cb[3];
        for (int i = 0; i < 3; i++)
        {
            if (i == hi) continue;
            ca[i] *= alphaA;
            cb[i] *= alphaB;
        }
        double alpha = alphaA + (alphaB - alphaA) * t;
        Span<double> r = stackalloc double[3];
        for (int i = 0; i < 3; i++)
        {
            double v = ca[i] + (cb[i] - ca[i]) * t;
            if (i != hi && alpha != 0) v /= alpha;
            if (i == hi) v = Normalize(v);
            r[i] = v;
        }
        return new WideColor(space, r[0], r[1], r[2], alpha, missing);
    }

    static double Normalize(double h)
    {
        if (double.IsNaN(h) || double.IsInfinity(h)) return 0;
        h %= 360;
        return h < 0 ? h + 360 : h;
    }
}
