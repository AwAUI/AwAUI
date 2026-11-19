#if NETSTANDARD2_0 || NETFRAMEWORK
namespace System
{
    /// <summary>netstandard2.0 没有 MathF，这里提供与 .NET Core 同名的单精度数学函数。</summary>
    internal static class MathF
    {
        public const float PI = 3.14159265f;
        public static float Abs(float x) => Math.Abs(x);
        public static float Sqrt(float x) => (float)Math.Sqrt(x);
        public static float Sin(float x) => (float)Math.Sin(x);
        public static float Cos(float x) => (float)Math.Cos(x);
        public static float Tan(float x) => (float)Math.Tan(x);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static float Acos(float x) => (float)Math.Acos(x);
        public static float Floor(float x) => (float)Math.Floor(x);
        public static float Ceiling(float x) => (float)Math.Ceiling(x);
        public static float Round(float x) => (float)Math.Round(x);
        public static float Min(float a, float b) => Math.Min(a, b);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Pow(float x, float y) => (float)Math.Pow(x, y);
        public static float Exp(float x) => (float)Math.Exp(x);
        public static float Log(float x) => (float)Math.Log(x);
        public static float Truncate(float x) => (float)Math.Truncate(x);
    }
}
#endif
