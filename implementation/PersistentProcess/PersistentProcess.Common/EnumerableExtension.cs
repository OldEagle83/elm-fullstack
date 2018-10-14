using System;
using System.Collections.Generic;
using System.Linq;

namespace Kalmit
{
    static public class EnumerableExtension
    {
        static public IEnumerable<T> WhereHasValue<T>(this IEnumerable<T?> orig) where T : struct =>
            orig?.Where(i => i.HasValue).Select(i => i.Value);

        static public IEnumerable<T> WhereNotNull<T>(this IEnumerable<T> orig) where T : class =>
            orig?.Where(i => i != null);

        static public IEnumerable<T> EmptyIfNull<T>(this IEnumerable<T> orig) =>
            orig ?? Array.Empty<T>();
    }
}