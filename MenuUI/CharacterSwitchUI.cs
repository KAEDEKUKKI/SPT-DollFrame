using System;
using System.Collections.Generic;
using System.Reflection;
using EFT.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace DollFrame
{
    /// <summary>
    /// 主選單「全般（Overall）」畫面的角色切換 UI：＜＞＋角色名按鈕列。
    /// 放置於人物預覽正上方空白處（不遮蔽右側數據統計列表）。
    /// 切換時寫回 Plugin.Character，並同步通知預覽與在場玩家（如藏身處）。
    /// </summary>
    [HarmonyPatch]
    internal static class CharacterSwitchUI
    {
        private static bool _typeFailLogged;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var t = AccessTools.TypeByName("EFT.UI.OverallScreen") ?? AccessTools.TypeByName("OverallScreen");
            if (t == null)
            {
                if (!_typeFailLogged)
                {
                    _typeFailLogged = true;
                    Plugin.Log.LogWarning("[DollFrame] OverallScreen type not found; menu switch UI disabled (PageUp hotkey unaffected)");
                }
                yield break;
            }
            var seen = new HashSet<MethodBase>();
            foreach (var m in t.GetMethods(AccessTools.all))
                if ((m.Name == "Show" || m.Name == "Close") && seen.Add(m))
                    yield return m;
        }

        [HarmonyPostfix]
        internal static void PostShowOrClose(object __instance, MethodBase __originalMethod)
        {
            try
            {
                var screen = __instance as Component;
                if (screen == null) return;
                if (__originalMethod.Name != "Show") { SwitchMenuDriver.Close(screen.gameObject); return; }
                if (!Plugin.ReplaceBody.Value) return;
                SwitchMenuDriver.Ensure(screen.gameObject);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[DollFrame] switch UI patch failed: {e.Message}");
            }
        }
    }

    internal sealed class SwitchMenuDriver : MonoBehaviour
    {
        private GameObject _rowB;
        private TMP_Text _rowBLabel;
        private string[] _names;
        private string _lastApplied = string.Empty;
        private float _lastPoll;
        private Sprite _white;
        private RectTransform _targetRT;

        private Sprite WhiteSprite =>
            _white != null ? _white : _white = Sprite.Create(Texture2D.whiteTexture,
                new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 100f);

        internal static void Ensure(GameObject screen)
        {
            if (screen == null) return;
            var d = screen.GetComponent<SwitchMenuDriver>();
            if (d == null) d = screen.AddComponent<SwitchMenuDriver>();
            d.Rebuild();
        }

        internal static void Close(GameObject screen)
        {
            var d = screen != null ? screen.GetComponent<SwitchMenuDriver>() : null;
            if (d == null) return;
            d.Teardown();
        }

        private void Rebuild()
        {
            try
            {
                var names = Plugin.MenuPackageNames();
                var current = Plugin.Character != null ? Plugin.Character.Value : null;
                if (_rowB != null && _names != null && names.Length == _names.Length && current == _lastApplied)
                    return; // 同畫面重複 Show 且名單、值皆未變：不重建
                Teardown();
                Build(names, current);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[DollFrame] switch UI build failed: {e.Message}");
            }
        }

        private void Teardown()
        {
            if (_rowB != null) Destroy(_rowB);
            _rowB = null; _rowBLabel = null; _targetRT = null;
        }

        private void OnDestroy()
        {
            Teardown();
            if (_white != null) { Destroy(_white); _white = null; }
        }

        private void Build(string[] names, string current)
        {
            _names = names;
            BuildButtonsRow(current);
            _lastApplied = current ?? string.Empty;
        }

        private void BuildButtonsRow(string current)
        {
            var screenRT = transform as RectTransform;
            if (screenRT == null) return;

            var screenTmp = GetComponentInChildren<TMP_Text>(true);
            TMP_FontAsset tmpFont = screenTmp != null ? screenTmp.font : null;

            _targetRT = ResolveTargetRectTransform();

            var row = new GameObject("DollFrame_SwitchRow", typeof(RectTransform));
            Transform parentT = _targetRT != null ? _targetRT : screenRT;
            row.transform.SetParent(parentT, false);
            var rt = (RectTransform)row.transform;
            rt.sizeDelta = new Vector2(280f, 32f);

            var bgGo = new GameObject("bg", typeof(Image));
            bgGo.transform.SetParent(row.transform, false);
            var bgImg = bgGo.GetComponent<Image>();
            bgImg.sprite = WhiteSprite;
            bgImg.color = new Color(0.04f, 0.05f, 0.07f, 0.85f);
            Stretch(bgGo.GetComponent<RectTransform>());

            var labelGo = new GameObject("label", typeof(TextMeshProUGUI));
            labelGo.transform.SetParent(row.transform, false);
            _rowBLabel = labelGo.GetComponent<TextMeshProUGUI>();
            if (tmpFont != null) _rowBLabel.font = tmpFont;
            _rowBLabel.fontSize = 16f;
            _rowBLabel.color = new Color(0.85f, 0.85f, 0.85f, 1f);
            _rowBLabel.alignment = TextAlignmentOptions.Center;
            Stretch(_rowBLabel.rectTransform);

            float btnSize = 26f;
            MakeButton(row.transform, "dollframeprev", "＜", tmpFont, 0f, btnSize, () => Cycle(-1));
            MakeButton(row.transform, "dollframenext", "＞", tmpFont, 1f, btnSize, () => Cycle(1));

            _rowB = row;
            UpdatePosition();
            RefreshRowBLabel();
        }

        private void UpdatePosition()
        {
            if (_rowB == null) return;
            var rt = (RectTransform)_rowB.transform;

            if (_targetRT == null)
            {
                _targetRT = ResolveTargetRectTransform();
                if (_targetRT != null && _rowB.transform.parent != _targetRT)
                    _rowB.transform.SetParent(_targetRT, false);
            }

            if (_targetRT != null && _rowB.transform.parent == _targetRT)
            {
                // 直接錨定在人物預覽頂端正中央，永不被其他面板覆蓋
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, -15f);
            }
            else
            {
                var screenRT = transform as RectTransform;
                if (screenRT != null)
                {
                    rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
                    float posX = screenRT.rect.xMin + Mathf.Min(300f, screenRT.rect.width * 0.25f);
                    rt.anchoredPosition = new Vector2(posX, -45f);
                }
            }
            var parent = _rowB.transform.parent;
            if (parent != null && _rowB.transform.GetSiblingIndex() != parent.childCount - 1)
                _rowB.transform.SetAsLastSibling();
        }

        private RectTransform ResolveTargetRectTransform()
        {
            var pmv = GetComponentInChildren<PlayerModelView>(true);
            if (pmv != null)
            {
                var rt = pmv.transform as RectTransform ?? pmv.GetComponentInParent<RectTransform>();
                if (rt != null && rt != transform) return rt;
            }
            var pmw = GetComponentInChildren<InventoryPlayerModelWithStatsWindow>(true);
            if (pmw != null)
            {
                var rt = pmw.transform as RectTransform ?? pmw.GetComponentInParent<RectTransform>();
                if (rt != null && rt != transform) return rt;
            }
            return null;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private void MakeButton(Transform parentRow, string name, string glyph, TMP_FontAsset font, float anchorX, float size, Action onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parentRow, false);
            var rt = (RectTransform)go.transform;
            bool left = anchorX < 0.5f;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(left ? 0f : 1f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);
            rt.anchoredPosition = new Vector2(left ? 6f : -6f, 0f);
            var img = go.GetComponent<Image>();
            img.sprite = WhiteSprite;
            img.color = new Color(0.9f, 0.9f, 0.95f, 0.14f);
            var txtGo = new GameObject("t", typeof(TextMeshProUGUI));
            txtGo.transform.SetParent(go.transform, false);
            var tmp = txtGo.GetComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.fontSize = size * 0.6f;
            tmp.color = new Color(0.9f, 0.9f, 0.95f, 0.95f);
            tmp.alignment = TextAlignmentOptions.Center;
            Stretch((RectTransform)txtGo.transform);
            tmp.text = glyph;
            go.GetComponent<Button>().onClick.AddListener(new UnityAction(onClick));
        }

        private void Update()
        {
            if (Time.unscaledTime - _lastPoll < 0.25f) return;
            _lastPoll = Time.unscaledTime;
            try
            {
                UpdatePosition();
                if (_rowBLabel != null && Plugin.Character != null && Plugin.Character.Value != _lastApplied)
                    RefreshRowBLabel();
            }
            catch { }
        }

        private void Cycle(int delta)
        {
            if (_names == null || _names.Length == 0) return;
            var idx = Array.IndexOf(_names, Plugin.Character != null ? Plugin.Character.Value : null);
            SetCharacter(_names[(idx + delta + _names.Length) % _names.Length]);
        }

        private void SetCharacter(string name)
        {
            if (Plugin.Character == null || Plugin.Character.Value == name) return;
            Plugin.Character.Value = name;
            Plugin.Log.LogInfo($"[DollFrame] menu switch -> '{name}'");
            _lastApplied = name ?? string.Empty;
            RefreshRowBLabel();
            Plugin.NotifyCharacterChanged();
        }

        private void RefreshRowBLabel()
        {
            if (Plugin.Character != null) _lastApplied = Plugin.Character.Value ?? string.Empty;
            if (_rowBLabel != null)
                _rowBLabel.text = $"DollFrame 角色：{(Plugin.Character != null ? Plugin.Character.Value : "-")}";
        }
    }
}
