using System.Collections.Generic;

namespace SCANsat
{
    // GetValueOrDefault is absent from the legacy framework reference assemblies
    // used by the local compiler. Equivalent to the upstream call, including null.
    internal static class SCANbuildCompatibility
    {
        internal static TValue GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key)
        {
            TValue value;
            return dictionary.TryGetValue(key, out value) ? value : default(TValue);
        }
    }
}
