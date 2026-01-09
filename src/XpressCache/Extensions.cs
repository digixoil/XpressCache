using System;
using System.Collections.Generic;
using System.Text;

namespace XpressCache;

/// <summary>
/// Internal extension methods for validation and assertions.
/// </summary>
internal static class Extensions
{
    #region Assertions
    public static T AssertNotNull<T>(this T o, string paramName, string message = null) where T : class
    {
        if (o == null) throw new ArgumentNullException(paramName: paramName, message: message);

        return o;
    }

    public static IEnumerable<T> AssertNotEmpty<T>(this IEnumerable<T> o, string paramName, string message = null) where T : class
    {
        if ((o?.Any() ?? false) == false) throw new ArgumentNullException(paramName: paramName, message: message);

        return o;
    }

    public static Guid AssertNotNullOrEmpty(this Guid? g, string paramName, string message = null)
    {
        if ((g ?? Guid.Empty) == Guid.Empty) throw new ArgumentNullException(paramName: paramName, message: message);

        return (Guid)g;
    }

    public static bool AssertTrue(this bool? b, string paramName, string message = null)
    {
        if (b != true) throw new ArgumentNullException(paramName: paramName, message: message);
        return true;
    }

    public static bool AssertTrue(this bool b, string paramName, string message = null)
    {
        if (b != true) throw new ArgumentNullException(paramName: paramName, message: message);
        return true;
    }

    public static bool AssertFalse(this bool? b, string paramName, string message = null)
    {
        if (b != false) throw new ArgumentNullException(paramName: paramName, message: message);
        return false;
    }

    public static bool AssertFalse(this bool b, string paramName, string message = null)
    {
        if (b != false) throw new ArgumentNullException(paramName: paramName, message: message);
        return false;
    }

    public static List<T> AssertNotEmpty<T>(this List<T> o, string paramName, string message = null) where T : class
    {
        if ((o?.Count ?? 0) == 0) throw new ArgumentNullException(paramName: paramName, message: message);

        return o;
    }

    public static T[] AssertNotEmpty<T>(this T[] o, string paramName, string message = null) where T : class
    {
        if ((o?.Length ?? 0) == 0) throw new ArgumentNullException(paramName: paramName, message: message);

        return o;
    }

    public static string AssertNotWhiteSpace(this string s, string paramName, string message = null)
    {
        if (string.IsNullOrWhiteSpace(s)) throw new ArgumentException(message: message, paramName: paramName);

        return s;
    }

    public static string AssertNotEmpty(this string s, string paramName, string message = null)
    {
        if (string.IsNullOrEmpty(s)) throw new ArgumentException(message: message, paramName: paramName);

        return s;
    }
    #endregion
}
