using System;

namespace MC2IL.Core.Extensions;

internal static class ArrayExtensions
{
    public static bool Contains<T>(this T[] array, T item)
    {
        return Array.IndexOf(array, item) >= 0;
    }
}
