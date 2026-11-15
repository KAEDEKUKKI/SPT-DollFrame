using System;

namespace DollFrame
{
    /// <summary>
    /// 【Visibility 次要連動 2C：特殊建模自帶配件隱藏】
    /// 過濾 PMX 特殊建模自帶的武器、槍套、彈匣包、背包等配件材質（HideModelAccessories）。
    /// 供 PmxAssets（全身網格建構）與 ArmsMesh（第一人稱手臂子網格提取）共用單一判準。
    /// </summary>
    internal static class AccessoryFilter
    {
#if !DOLLFRAME_PMX_PARSER_ONLY
        private static string _memoCsv;
        private static string[] _memoParsed;

        internal static string[] FromConfig()
        {
            if (Plugin.HideModelAccessories == null || !Plugin.HideModelAccessories.Value)
            {
                _memoCsv = null;
                _memoParsed = null;
                return null;
            }
            var csv = Plugin.HideModelAccessoriesList != null ? Plugin.HideModelAccessoriesList.Value : null;
            if (_memoParsed != null && csv == _memoCsv) return _memoParsed;
            _memoCsv = csv;
            _memoParsed = Parse(true, csv);
            return _memoParsed;
        }
#endif

        internal static string[] Parse(bool enabled, string csv)
        {
            if (!enabled || string.IsNullOrWhiteSpace(csv)) return null;
            var parts = csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
            return parts;
        }

        /// <summary>
        /// 判定指定 PMX 材質是否屬於模型自帶配件（比對材質中英名稱與貼圖路徑子字串，不分大小寫）。
        /// </summary>
        internal static bool IsAccessory(PmxParser.PmxModel pmx, int matIdx, string[] patterns)
        {
            if (pmx == null || pmx.Mats == null || matIdx < 0 || matIdx >= pmx.Mats.Length) return false;
            if (patterns == null || patterns.Length == 0) return false;
            var mat = pmx.Mats[matIdx];
            string texPath = mat.TexIdx >= 0 && pmx.TexPaths != null && mat.TexIdx < pmx.TexPaths.Length
                ? pmx.TexPaths[mat.TexIdx]
                : null;
            foreach (var p in patterns)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (mat.Name != null && mat.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (mat.NameEn != null && mat.NameEn.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (texPath != null && texPath.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }
}
