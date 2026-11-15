using System;
using System.Collections.Generic;

namespace DollFrame
{
    /// <summary>
    /// Pure helpers for handling the "Vanilla (PMC)" selection option.
    /// Pure C# (no Unity dependencies) so it can be verified in Tests/DollFrameVerify.
    /// </summary>
    public static class VanillaSelection
    {
        public const string VanillaPackageName = "Vanilla (PMC)";

        public static bool IsVanilla(string name)
        {
            return string.IsNullOrWhiteSpace(name) ||
                   string.Equals(name, VanillaPackageName, StringComparison.OrdinalIgnoreCase);
        }

        public static string[] PrependVanilla(IEnumerable<string> packages)
        {
            var list = new List<string> { VanillaPackageName };
            if (packages != null)
            {
                foreach (var p in packages)
                {
                    if (!string.IsNullOrWhiteSpace(p) && !IsVanilla(p))
                        list.Add(p);
                }
            }
            return list.ToArray();
        }
    }
}
