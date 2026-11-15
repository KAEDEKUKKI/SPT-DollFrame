using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DollFrame
{
    /// <summary>
    /// Declarative capsule collider definition for humanoid body parts.
    /// </summary>
    public class CapsuleProfile
    {
        public string BoneName = string.Empty;
        public float Radius = 0.05f;
        public float Height = 0.1f;
        public float[] Offset = new float[3];

        public CapsuleProfile() { }

        public CapsuleProfile(string boneName, float radius, float height, float[] offset = null)
        {
            BoneName = boneName ?? string.Empty;
            Radius = radius;
            Height = height;
            Offset = offset ?? new float[3];
        }
    }

    /// <summary>
    /// Declarative spring chain definition for secondary physics (hair, skirt, breast, accessories).
    /// </summary>
    public class SpringChainProfile
    {
        public string RootBone = string.Empty;
        public string[] ChildBones = Array.Empty<string>();
        public float Stiffness = 0.5f;
        public float Drag = 0.4f;
        public float Radius = 0.02f;
        public string Category = "Hair";
        public float[] Gravity = new float[3] { 0f, -9.8f, 0f };

        public SpringChainProfile() { }

        public SpringChainProfile(string rootBone, string[] childBones, float stiffness = 0.5f, float drag = 0.4f, float radius = 0.02f, string category = "Hair")
        {
            RootBone = rootBone ?? string.Empty;
            ChildBones = childBones ?? Array.Empty<string>();
            Stiffness = stiffness;
            Drag = drag;
            Radius = radius;
            Category = category ?? "Hair";
            Gravity = new float[3] { 0f, -9.8f, 0f };
        }
    }

    /// <summary>
    /// Anatomical and secondary physics keywords for automatic profile classification.
    /// </summary>
    public static class ModelProfileKeywords
    {
        public static readonly string[] FrontHairExclude = { "前髪", "bang", "maegami", "front_hair", "fronthair", "瀏海" };
        public static readonly string[] Hair = { "髪", "hair", "Hair", "ツインテ", "ポニテ", "後髪", "横髪", "アホ毛" };
        public static readonly string[] Skirt = { "スカート", "skirt", "Skirt", "裾", "腰布", "マント", "コート", "ケープ", "cape", "cloak", "coat", "frill", "フリル" };
        public static readonly string[] Breast = { "胸", "breast", "Breast", "おっぱい" };
        public static readonly string[] Accessory = { "リボン", "ribbon", "アクセ", "acc", "Acc", "紐", "羽", "tail", "尾", "尻尾", "袖", "sleeve", "衣", "服", "ベルト", "belt", "帯", "strap", "垂", "布", "ネクタイ", "tie", "マフラー", "ストール", "scarf", "pouch", "ear", "耳", "飄" };

        public static string CategorizeSpring(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (MatchesAny(name, FrontHairExclude)) return null;
            if (MatchesAny(name, Hair)) return "Hair";
            if (MatchesAny(name, Skirt)) return "Skirt";
            if (MatchesAny(name, Breast)) return "Breast";
            if (MatchesAny(name, Accessory)) return "Accessory";
            return null;
        }

        private static bool MatchesAny(string s, string[] keys)
        {
            foreach (var k in keys)
            {
                if (s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Declarative model profile containing measured anatomical metrics, capsule colliders, and spring physics chains.
    /// Provides JSON serialization/deserialization for external tuning and overrides.
    /// </summary>
    public class ModelProfile
    {
        public float EyeHeight;
        public float ArmReach;
        public float ShoulderWidth;
        public CapsuleProfile[] Colliders = Array.Empty<CapsuleProfile>();
        public SpringChainProfile[] Springs = Array.Empty<SpringChainProfile>();

        public ModelProfile() { }

        public ModelProfile(float eyeHeight, float armReach, float shoulderWidth, CapsuleProfile[] colliders, SpringChainProfile[] springs)
        {
            EyeHeight = eyeHeight;
            ArmReach = armReach;
            ShoulderWidth = shoulderWidth;
            Colliders = colliders ?? Array.Empty<CapsuleProfile>();
            Springs = springs ?? Array.Empty<SpringChainProfile>();
        }

        public string ToJson(bool pretty = true)
        {
            var sb = new StringBuilder();
            string ind = pretty ? "  " : "";
            string nl = pretty ? "\n" : "";

            sb.Append("{" + nl);
            sb.Append($"{ind}\"EyeHeight\": {EyeHeight.ToString("R", CultureInfo.InvariantCulture)}," + nl);
            sb.Append($"{ind}\"ArmReach\": {ArmReach.ToString("R", CultureInfo.InvariantCulture)}," + nl);
            sb.Append($"{ind}\"ShoulderWidth\": {ShoulderWidth.ToString("R", CultureInfo.InvariantCulture)}," + nl);

            // Colliders
            sb.Append($"{ind}\"Colliders\": [");
            if (Colliders != null && Colliders.Length > 0)
            {
                sb.Append(nl);
                for (int i = 0; i < Colliders.Length; i++)
                {
                    var c = Colliders[i];
                    string cInd = pretty ? "    " : "";
                    var ox = (c.Offset != null && c.Offset.Length > 0) ? c.Offset[0] : 0f;
                    var oy = (c.Offset != null && c.Offset.Length > 1) ? c.Offset[1] : 0f;
                    var oz = (c.Offset != null && c.Offset.Length > 2) ? c.Offset[2] : 0f;
                    sb.Append($"{cInd}{{\"BoneName\": \"{Escape(c.BoneName)}\", \"Radius\": {c.Radius.ToString("R", CultureInfo.InvariantCulture)}, \"Height\": {c.Height.ToString("R", CultureInfo.InvariantCulture)}, \"Offset\": [{ox.ToString("R", CultureInfo.InvariantCulture)}, {oy.ToString("R", CultureInfo.InvariantCulture)}, {oz.ToString("R", CultureInfo.InvariantCulture)}]}}");
                    if (i < Colliders.Length - 1) sb.Append(",");
                    sb.Append(nl);
                }
                sb.Append(ind);
            }
            sb.Append("]," + nl);

            // Springs
            sb.Append($"{ind}\"Springs\": [");
            if (Springs != null && Springs.Length > 0)
            {
                sb.Append(nl);
                for (int i = 0; i < Springs.Length; i++)
                {
                    var s = Springs[i];
                    string sInd = pretty ? "    " : "";
                    var gx = (s.Gravity != null && s.Gravity.Length > 0) ? s.Gravity[0] : 0f;
                    var gy = (s.Gravity != null && s.Gravity.Length > 1) ? s.Gravity[1] : -9.8f;
                    var gz = (s.Gravity != null && s.Gravity.Length > 2) ? s.Gravity[2] : 0f;
                    sb.Append($"{sInd}{{\"RootBone\": \"{Escape(s.RootBone)}\", \"Category\": \"{Escape(s.Category ?? "Hair")}\", \"Stiffness\": {s.Stiffness.ToString("R", CultureInfo.InvariantCulture)}, \"Drag\": {s.Drag.ToString("R", CultureInfo.InvariantCulture)}, \"Radius\": {s.Radius.ToString("R", CultureInfo.InvariantCulture)}, \"Gravity\": [{gx.ToString("R", CultureInfo.InvariantCulture)}, {gy.ToString("R", CultureInfo.InvariantCulture)}, {gz.ToString("R", CultureInfo.InvariantCulture)}], \"ChildBones\": [");
                    if (s.ChildBones != null && s.ChildBones.Length > 0)
                    {
                        for (int b = 0; b < s.ChildBones.Length; b++)
                        {
                            sb.Append($"\"{Escape(s.ChildBones[b])}\"");
                            if (b < s.ChildBones.Length - 1) sb.Append(", ");
                        }
                    }
                    sb.Append("]}");
                    if (i < Springs.Length - 1) sb.Append(",");
                    sb.Append(nl);
                }
                sb.Append(ind);
            }
            sb.Append("]" + nl);
            sb.Append("}");
            return sb.ToString();
        }

        public static ModelProfile FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON input is empty", nameof(json));

            var profile = new ModelProfile();

            var mEye = Regex.Match(json, @"""EyeHeight""\s*:\s*([-+0-9.eE]+)");
            if (mEye.Success) profile.EyeHeight = float.Parse(mEye.Groups[1].Value, CultureInfo.InvariantCulture);

            var mArm = Regex.Match(json, @"""ArmReach""\s*:\s*([-+0-9.eE]+)");
            if (mArm.Success) profile.ArmReach = float.Parse(mArm.Groups[1].Value, CultureInfo.InvariantCulture);

            var mShoulder = Regex.Match(json, @"""ShoulderWidth""\s*:\s*([-+0-9.eE]+)");
            if (mShoulder.Success) profile.ShoulderWidth = float.Parse(mShoulder.Groups[1].Value, CultureInfo.InvariantCulture);

            // Colliders array
            var collidersList = new List<CapsuleProfile>();
            var collidersContent = ExtractArrayContent(json, "Colliders");
            if (!string.IsNullOrEmpty(collidersContent))
            {
                var colObjects = ExtractObjects(collidersContent);
                foreach (var objText in colObjects)
                {
                    var c = new CapsuleProfile();
                    var mBone = Regex.Match(objText, @"""BoneName""\s*:\s*""([^""]*)""");
                    if (mBone.Success) c.BoneName = Unescape(mBone.Groups[1].Value);

                    var mRad = Regex.Match(objText, @"""Radius""\s*:\s*([-+0-9.eE]+)");
                    if (mRad.Success) c.Radius = float.Parse(mRad.Groups[1].Value, CultureInfo.InvariantCulture);

                    var mHeight = Regex.Match(objText, @"""Height""\s*:\s*([-+0-9.eE]+)");
                    if (mHeight.Success) c.Height = float.Parse(mHeight.Groups[1].Value, CultureInfo.InvariantCulture);

                    var mOffset = Regex.Match(objText, @"""Offset""\s*:\s*\[\s*([-+0-9.eE]+)\s*,\s*([-+0-9.eE]+)\s*,\s*([-+0-9.eE]+)\s*\]");
                    if (mOffset.Success)
                    {
                        c.Offset = new float[3]
                        {
                            float.Parse(mOffset.Groups[1].Value, CultureInfo.InvariantCulture),
                            float.Parse(mOffset.Groups[2].Value, CultureInfo.InvariantCulture),
                            float.Parse(mOffset.Groups[3].Value, CultureInfo.InvariantCulture)
                        };
                    }
                    collidersList.Add(c);
                }
            }
            profile.Colliders = collidersList.ToArray();

            // Springs array
            var springsList = new List<SpringChainProfile>();
            var springsContent = ExtractArrayContent(json, "Springs");
            if (!string.IsNullOrEmpty(springsContent))
            {
                var spObjects = ExtractObjects(springsContent);
                foreach (var objText in spObjects)
                {
                    var s = new SpringChainProfile();
                    var mRoot = Regex.Match(objText, @"""RootBone""\s*:\s*""([^""]*)""");
                    if (mRoot.Success) s.RootBone = Unescape(mRoot.Groups[1].Value);

                    var mCat = Regex.Match(objText, @"""Category""\s*:\s*""([^""]*)""");
                    if (mCat.Success) s.Category = Unescape(mCat.Groups[1].Value);

                    var mStiff = Regex.Match(objText, @"""Stiffness""\s*:\s*([-+0-9.eE]+)");
                    if (mStiff.Success) s.Stiffness = float.Parse(mStiff.Groups[1].Value, CultureInfo.InvariantCulture);

                    var mDrag = Regex.Match(objText, @"""Drag""\s*:\s*([-+0-9.eE]+)");
                    if (mDrag.Success) s.Drag = float.Parse(mDrag.Groups[1].Value, CultureInfo.InvariantCulture);

                    var mRad = Regex.Match(objText, @"""Radius""\s*:\s*([-+0-9.eE]+)");
                    if (mRad.Success) s.Radius = float.Parse(mRad.Groups[1].Value, CultureInfo.InvariantCulture);

                    var mGrav = Regex.Match(objText, @"""Gravity""\s*:\s*\[\s*([-+0-9.eE]+)\s*,\s*([-+0-9.eE]+)\s*,\s*([-+0-9.eE]+)\s*\]");
                    if (mGrav.Success)
                    {
                        s.Gravity = new float[3]
                        {
                            float.Parse(mGrav.Groups[1].Value, CultureInfo.InvariantCulture),
                            float.Parse(mGrav.Groups[2].Value, CultureInfo.InvariantCulture),
                            float.Parse(mGrav.Groups[3].Value, CultureInfo.InvariantCulture)
                        };
                    }

                    var childContent = ExtractArrayContent(objText, "ChildBones");
                    if (!string.IsNullOrEmpty(childContent))
                    {
                        var childList = new List<string>();
                        foreach (Match cbm in Regex.Matches(childContent, @"""([^""]*)"""))
                        {
                            childList.Add(Unescape(cbm.Groups[1].Value));
                        }
                        s.ChildBones = childList.ToArray();
                    }
                    springsList.Add(s);
                }
            }
            profile.Springs = springsList.ToArray();

            return profile;
        }

        private static string ExtractArrayContent(string json, string arrayName)
        {
            int keyIdx = json.IndexOf($"\"{arrayName}\"", StringComparison.Ordinal);
            if (keyIdx < 0) return null;
            int startBracket = json.IndexOf('[', keyIdx);
            if (startBracket < 0) return null;

            int depth = 0;
            bool inString = false;
            bool escape = false;

            for (int i = startBracket; i < json.Length; i++)
            {
                char ch = json[i];
                if (escape) { escape = false; continue; }
                if (ch == '\\') { escape = true; continue; }
                if (ch == '"') { inString = !inString; continue; }
                if (!inString)
                {
                    if (ch == '[') depth++;
                    else if (ch == ']')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            return json.Substring(startBracket + 1, i - startBracket - 1);
                        }
                    }
                }
            }
            return null;
        }

        private static List<string> ExtractObjects(string content)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(content)) return list;

            int start = -1;
            int depth = 0;
            bool inString = false;
            bool escape = false;

            for (int i = 0; i < content.Length; i++)
            {
                char ch = content[i];
                if (escape) { escape = false; continue; }
                if (ch == '\\') { escape = true; continue; }
                if (ch == '"') { inString = !inString; continue; }
                if (!inString)
                {
                    if (ch == '{')
                    {
                        if (depth == 0) start = i;
                        depth++;
                    }
                    else if (ch == '}')
                    {
                        depth--;
                        if (depth == 0 && start >= 0)
                        {
                            list.Add(content.Substring(start + 1, i - start - 1));
                            start = -1;
                        }
                    }
                }
            }
            return list;
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r");
        }
    }
}
