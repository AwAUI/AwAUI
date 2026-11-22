using System;

namespace AwAUI.Shadcn;

/// <summary>Small numeric helpers: netstandard2.0 has no <c>Math.Clamp</c>.</summary>
internal static class ShadcnMath
{
    internal static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    internal static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;
}
