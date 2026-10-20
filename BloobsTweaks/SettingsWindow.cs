using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BloobsTweaks
{
    // The mod's settings window, built from the game's own UI pieces so it looks like one of its skill windows:
    // the SkillUI frame, title banner, exit button, basic buttons as tabs down the left, the on/off switch
    // sprites from the time-bank panel, the game's slider art and its LiberationSans font, on a canvas scaled
    // the same way as the game's (1920x1080 reference, match 0.5). Built the first time it's opened, once the
    // game scene - and so its sprites - are loaded.
    internal class SettingsWindow : MonoBehaviour
    {
        private static readonly float[] SpeedPresets = { 1f, 2f, 5f, 10f, 20f, 50f, 100f, 200f };

        private static readonly Color Yellow = new Color32(0xF4, 0xFF, 0x0B, 0xFF);
        private static readonly Color NoteColor = new Color32(0xD8, 0xD8, 0xD8, 0xFF);
        private static readonly Color TabActive = new Color32(0x3C, 0xF8, 0x19, 0xFF);

        private GameObject _canvas;
        private RectTransform _panel;
        private readonly List<(Button Tab, GameObject Page)> _tabs = new List<(Button, GameObject)>();
        private readonly List<Action> _refreshers = new List<Action>();
        private float _nextRefresh;
        private bool _open;

        private Theme _theme;

        private class Theme
        {
            public TMP_FontAsset Font;
            public Sprite Frame, Title, Exit, Button, On, Off, SliderBack, SliderHandle;
        }

        private void Update()
        {
            if (Plugin.MenuKey.Value.IsDown())
                SetOpen(!_open);
            else if (_open && Input.GetKeyDown(KeyCode.Escape))
                SetOpen(false);

            if (_open && Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + 0.25f;
                foreach (var refresh in _refreshers)
                {
                    try { refresh(); }
                    catch (Exception e) { Plugin.Log.LogWarning($"Settings refresh failed: {e.Message}"); }
                }
            }
        }

        private void OnDestroy()
        {
            if (_canvas != null)
                Destroy(_canvas);
        }

        private void SetOpen(bool open)
        {
            if (open && _canvas == null && !TryBuild())
                return;
            _open = open;
            if (_canvas != null)
                _canvas.SetActive(open);
            _nextRefresh = 0f;
        }

        // ---------------------------------------------------------------------------------------------------
        // Building

        private bool TryBuild()
        {
            if (Scene.Find<InventoryUI>() == null)
            {
                Plugin.Log.LogInfo("Settings window: load a save first");
                return false;
            }
            try
            {
                _theme = LoadTheme();
                Build();
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Building the settings window failed: {e}");
                if (_canvas != null)
                    Destroy(_canvas);
                _canvas = null;
                _tabs.Clear();
                _refreshers.Clear();
                return false;
            }
        }

        private static Theme LoadTheme()
        {
            var sprites = new Dictionary<string, Sprite>();
            foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                if (s != null && !sprites.ContainsKey(s.name))
                    sprites[s.name] = s;
            Sprite Get(string name) => sprites.TryGetValue(name, out var s) ? s : null;

            var offline = Resources.FindObjectsOfTypeAll<OfflineProgression>().FirstOrDefault(o => o.gameObject.scene.IsValid());
            var font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f.name == "LiberationSans SDF")
                       ?? TMP_Settings.defaultFontAsset;

            return new Theme
            {
                Font = font,
                Frame = Get("SkillUI") ?? Get("Frame_bg_big"),
                Title = Get("Title_frame_0m"),
                Exit = Get("1_exit"),
                Button = Get("1_basic_button"),
                On = offline != null && offline.autoStopEnabledSprite != null ? offline.autoStopEnabledSprite : Get("2_options_on"),
                Off = offline != null ? offline.autoStopDisabledSprite : null,
                SliderBack = Get("Slidearea"),
                SliderHandle = Get("Handle"),
            };
        }

        private void Build()
        {
            _canvas = new GameObject("BloobsTweaksSettings");
            DontDestroyOnLoad(_canvas);
            var canvas = _canvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500;
            var scaler = _canvas.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _canvas.AddComponent<GraphicRaycaster>();

            // Window, sized and laid out like the game's skill windows.
            _panel = Rect("Window", _canvas.transform);
            _panel.sizeDelta = new Vector2(710f, 744f);
            _panel.anchoredPosition = new Vector2(-265f, 0f);
            Img(_panel.gameObject, _theme.Frame, _theme.Frame != null ? Color.white : new Color(0.15f, 0.12f, 0.2f, 0.95f));
            _panel.gameObject.AddComponent<DragWindow>();

            var title = Rect("Title", _panel);
            Place(title, new Vector2(0.5f, 0.5f), new Vector2(265f, 40f), new Vector2(6f, 333f));
            if (_theme.Title != null)
                Img(title.gameObject, _theme.Title, Color.white);
            var titleText = Text(title, Plugin.Name, 32f, Color.white, TextAlignmentOptions.Center);
            Stretch(titleText.rectTransform);

            var close = Rect("Close", _panel);
            Place(close, new Vector2(1f, 1f), new Vector2(56f, 56f), new Vector2(-58f, -55f));
            var closeImg = Img(close.gameObject, _theme.Exit, _theme.Exit != null ? Color.white : new Color(0.7f, 0.1f, 0.1f));
            var closeButton = close.gameObject.AddComponent<Button>();
            closeButton.targetGraphic = closeImg;
            Tint(closeButton, new Color32(0xFD, 0x0B, 0x0B, 0xFF));
            closeButton.onClick.AddListener(() => SetOpen(false));
            if (_theme.Exit == null)
                Stretch(Text(close, "X", 30f, Color.white, TextAlignmentOptions.Center).rectTransform);

            var footer = Text(_panel, $"{Plugin.MenuKey.Value} toggles this window\nv{Plugin.Version}", 18f, NoteColor, TextAlignmentOptions.Center);
            Place(footer.rectTransform, new Vector2(0f, 0f), new Vector2(150f, 50f), new Vector2(87f, 45f));

            AddPage("Bank", BuildBank);
            AddPage("Items", BuildItems);
            AddPage("Gear", BuildGear);
            AddPage("Combat", BuildCombat);
            AddPage("Challenges", BuildChallenges);
            AddPage("Speed", BuildSpeed);
            AddPage("Launch", BuildLaunch);
            ShowTab(0);
        }

        private void AddPage(string name, Action<RectTransform> fill)
        {
            var index = _tabs.Count;

            var tab = Rect("Tab " + name, _panel);
            Place(tab, new Vector2(0f, 0.5f), new Vector2(116f, 54f), new Vector2(87f, 247f - index * 70f));
            var tabButton = MakeButton(tab, name, 22f);
            tabButton.onClick.AddListener(() => ShowTab(index));

            // Content area matches the skill windows' tab panels: scrolling text on the right of the tabs.
            var page = Rect("Page " + name, _panel);
            page.anchorMin = Vector2.zero;
            page.anchorMax = Vector2.one;
            page.offsetMin = new Vector2(168f, 23f);
            page.offsetMax = new Vector2(-33f, -98f);
            var content = ScrollArea(page);
            fill(content);

            _tabs.Add((tabButton, page.gameObject));
        }

        private void ShowTab(int index)
        {
            for (var i = 0; i < _tabs.Count; i++)
            {
                _tabs[i].Page.SetActive(i == index);
                var colors = _tabs[i].Tab.colors;
                colors.normalColor = i == index ? TabActive : Color.white;
                _tabs[i].Tab.colors = colors;
            }
        }

        // ---------------------------------------------------------------------------------------------------
        // Pages

        private void BuildBank(RectTransform c)
        {
            Heading(c, "Bank");
            ToggleRow(c, Plugin.OverflowEnabled, "Bank items that don't fit in a full inventory");
            ToggleRow(c, Plugin.OverflowMessages, "Show a note when items are banked");
            ToggleRow(c, Plugin.OverflowMuteBankSound, "Mute the bank sound for these deposits");
            ToggleRow(c, Plugin.CraftFromBank, "Craft and burn logs with materials from the bank");
            ToggleRow(c, Plugin.HomesteadFromBank, "Homesteading costs and ranch traps use the bank");
            ToggleRow(c, Plugin.HomesteadBankAnyTier, "Seeds and ranch eggs use the bank before homestead tier 7");
            ToggleRow(c, Plugin.BaitFromBank, "Fishing uses bait from the bank");
            ToggleRow(c, Plugin.KeysFromBank, "Chests use keys and lockpicks from the bank");
            ToggleRow(c, Plugin.EatFromBank, "Auto-eat uses food from the bank when you run out");
            ToggleRow(c, Plugin.UnlimitedAutoManage, "No limit on the auto-sell / auto-bank list");
            Note(c, "Selling speed is unchanged. Turning this off makes the game drop list entries beyond its normal limit.");
            ToggleRow(c, Plugin.LootShowTotals, "Show bag and bank totals when picking up items");
        }

        private void BuildItems(RectTransform c)
        {
            Heading(c, "Golden resources");
            ToggleRow(c, Plugin.AutoGoldenEnabled, "Auto-collect Golden Resource rewards");
            var claimed = Note(c, "");
            _refreshers.Add(() => claimed.text = $"Trees, ores, bushes, fishing spots and thieving chests. Claimed this session: <color=yellow>{AutoGolden.Claimed}</color>");

            Heading(c, "Resources");
            ToggleRow(c, Plugin.InfiniteResources, "Trees, rocks, bushes and fishing spots never run out");
            Note(c, "No rewards are lost: they all come per 100 damage. Thieving chests still run out, since they only roll golden when they respawn.");

            Heading(c, "Item info");
            ToggleRow(c, Plugin.ItemTooltips, "Show what items are used in, made with, obtained from and spent on");
            ToggleRow(c, Plugin.ItemCtrlClick, "Ctrl+click an item to open its skill window");
            ToggleRow(c, Plugin.RecipeLinksEnabled, "Click recipe ingredients to jump to where they're made");
            ToggleRow(c, Plugin.RecipeAmountColors, "Colour recipe ingredients green or red by what you have");
            ToggleRow(c, Plugin.KeyWalksToChest, "Click a chest key to walk to its chest and open it");
        }

        private void BuildGear(RectTransform c)
        {
            Heading(c, "Tools");
            ToggleRow(c, Plugin.BestToolsFromBank, "Use better tools from the bank when gathering");
            Note(c, "Axe, pickaxe, rod and lens. The game already picks the best one you carry; this adds the bank.");

            Heading(c, "Run speed");
            ToggleRow(c, Plugin.FastestBoots, "Wear my fastest boots outside combat");
            Note(c, "Boots are the only gear with run speed, and it speeds up all walking. Checked every 10 seconds and when a dexterity course starts.");

            Heading(c, "Combat gear");
            ToggleRow(c, Plugin.BestGearOnCombat, "Equip the best gear for my combat style when a fight starts");
            Note(c, "Picks from inventory and bank for the active equipment set only. Replaced pieces go to the bank.");
            var row = Rect("Equip now", c);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 54f;
            var button = Rect("Button", row);
            Place(button, new Vector2(0f, 0.5f), new Vector2(260f, 54f), new Vector2(130f, 0f));
            MakeButton(button, "Equip best gear now", 22f).onClick.AddListener(() =>
            {
                try { BestGear.EquipBestForCombat(true); }
                catch (Exception e) { Plugin.Log.LogWarning($"Equip best failed: {e.Message}"); }
            });
        }

        private void BuildCombat(RectTransform c)
        {
            Heading(c, "Combat");
            ToggleRow(c, Plugin.AutoRefightEnabled, "Go back and refight after dying");
            var status = Note(c, "");
            _refreshers.Add(() => status.text = string.IsNullOrEmpty(AutoRefight.Status)
                ? $"Stops after <color=yellow>{Plugin.AutoRefightMaxDeaths.Value}</color> deaths in a row without a kill."
                : AutoRefight.Status);
        }

        private void BuildChallenges(RectTransform c)
        {
            Heading(c, "Challenges");
            ToggleRow(c, Plugin.UntrackedChallengesEnabled, "Count progress toward untracked challenges");
            var status = Note(c, "");
            _refreshers.Add(() =>
            {
                var level = ChallengeAutoProgressUpgrade.Instance != null ? ChallengeAutoProgressUpgrade.Instance.AutoProgressLevel : 0;
                status.text = UntrackedChallenges.Unlocked
                    ? $"Active: covers tiers 1-{level + 1} (Auto Progress level {level}) and repeatables. Progress added this session: <color=yellow>{UntrackedChallenges.Applied}</color>"
                    : "Inactive: needs Auto Navigate + Start and Auto Progress switched on.";
            });
        }

        private static readonly (int Slot, string Label)[] LaunchSlots =
            { (-1, "Last played"), (0, "Save 1"), (1, "Save 2"), (2, "Save 3"), (3, "Turbo") };

        private void BuildLaunch(RectTransform c)
        {
            Heading(c, "Launch");
            ToggleRow(c, Plugin.AutoLoadEnabled, "Load a save automatically when the game starts");
            Note(c, "Only on the first trip to the menu after launching. Never starts a new game. Hold Shift while the menu appears to stay on it.");

            Body(c, "Which save:");
            var grid = Rect("Slots", c);
            var layout = grid.gameObject.AddComponent<GridLayoutGroup>();
            layout.cellSize = new Vector2(140f, 44f);
            layout.spacing = new Vector2(10f, 10f);
            layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            layout.constraintCount = 3;
            foreach (var (slot, label) in LaunchSlots)
            {
                var value = slot;
                var button = MakeButton(Rect(label, grid), label, 20f);
                button.onClick.AddListener(() => Plugin.AutoLoadSlot.Value = value);
                _refreshers.Add(() =>
                {
                    var colors = button.colors;
                    colors.normalColor = Plugin.AutoLoadSlot.Value == value ? TabActive : Color.white;
                    button.colors = colors;
                });
            }

            var last = Note(c, "");
            _refreshers.Add(() =>
            {
                var s = Plugin.AutoLoadLastSlot.Value;
                last.text = s < 0 ? "Last played: none recorded yet." : $"Last played: <color=yellow>{(s == 3 ? "Turbo" : $"Save {s + 1}")}</color>";
            });
        }

        private void BuildSpeed(RectTransform c)
        {
            Heading(c, "Game speed");
            var readout = Body(c, "");
            var actual = Body(c, "");
            var behind = Note(c, "Frames can't keep up with this speed. Raising the game's FPS limit helps; each skill does at most one action per frame.");

            var slider = MakeSlider(c);
            slider.onValueChanged.AddListener(v =>
            {
                var snapped = v < 10f ? Mathf.Round(v * 4f) / 4f : Mathf.Round(v);
                if (!Mathf.Approximately(snapped, Plugin.SpeedMultiplier.Value))
                    Plugin.SpeedMultiplier.Value = snapped;
            });

            var presets = Rect("Presets", c);
            var grid = presets.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(100f, 44f);
            grid.spacing = new Vector2(10f, 10f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 4;
            var presetButtons = new List<(float, GameObject)>();
            foreach (var preset in SpeedPresets)
            {
                var value = preset;
                var b = Rect($"{preset}x", presets);
                MakeButton(b, $"{preset:0.#}x", 22f).onClick.AddListener(() => Plugin.SpeedMultiplier.Value = value);
                presetButtons.Add((preset, b.gameObject));
            }

            var note = Note(c, "");

            Heading(c, "Smoothing");
            ToggleRow(c, Plugin.SmoothHighSpeed, "Smooth camera, animations and popups at high speed");
            ToggleRow(c, Plugin.SkillCatchUp, "Let skills keep up with high speeds");
            Note(c, "Gathering, thieving, crafting, firemaking, combat, tracking, devotion and courses otherwise manage one action per frame.");
            var fpsHint = Note(c, "");

            _refreshers.Add(() =>
            {
                // Each frame covers (speed / fps) seconds of game time, so the fps cap matters most for smoothness.
                var cap = Application.targetFrameRate;
                var capped = cap > 0 && cap <= 30 && QualitySettings.vSyncCount == 0;
                fpsHint.text = capped
                    ? "The game is capped at <color=yellow>30 fps</color>. Set FPS to 60, VSync or Uncapped in the game's Settings for smoother high speeds."
                    : "Only active while the game runs faster than normal.";

                var max = Mathf.Max(1f, Plugin.SpeedMax.Value);
                var ours = Mathf.Clamp(Plugin.SpeedMultiplier.Value, 1f, max);
                slider.maxValue = max;
                slider.SetValueWithoutNotify(ours);
                foreach (var (value, go) in presetButtons)
                    go.SetActive(value <= max);

                readout.text = $"Mod <color=yellow>{ours:0.##}x</color>   Game <color=yellow>{GameSpeed.GameMultiplier:0.##}x</color>   Requested <color=yellow>{GameSpeed.Requested:0.##}x</color>";
                var lagging = GameSpeed.Actual < GameSpeed.Requested * 0.9f;
                actual.text = $"Actually running at <color={(lagging ? "#FFB347" : "#9CFF9C")}>{GameSpeed.Actual:0.#}x</color>  ({GameSpeed.Fps:0} fps)";
                behind.gameObject.SetActive(lagging);
                note.text = $"Stacks with the game's own 2x / 3x / turbo speed. Banked time still drains in real time. The slider tops out at {max:0}x (MaxMultiplier in the config).";
            });
        }

        // ---------------------------------------------------------------------------------------------------
        // Widgets

        private void ToggleRow(RectTransform parent, ConfigEntry<bool> entry, string label)
        {
            var row = Rect("Toggle", parent);
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 12f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;

            var text = Text(row, label, 24f, Color.white, TextAlignmentOptions.Left);
            text.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

            var sw = Rect("Switch", row);
            var le = sw.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = le.minWidth = 55f;
            le.preferredHeight = le.minHeight = 55f;
            var img = Img(sw.gameObject, entry.Value ? _theme.On : _theme.Off, Color.white);
            var button = sw.gameObject.AddComponent<Button>();
            button.targetGraphic = img;
            Tint(button, new Color32(0x3C, 0xF8, 0x19, 0xFF));
            button.onClick.AddListener(() => entry.Value = !entry.Value);

            // Without the game's off sprite, show state by colour instead.
            _refreshers.Add(() =>
            {
                if (_theme.Off != null)
                    img.sprite = entry.Value ? _theme.On : _theme.Off;
                else
                    img.color = entry.Value ? Color.white : new Color(0.45f, 0.45f, 0.45f);
            });
        }

        private Slider MakeSlider(RectTransform parent)
        {
            var root = Rect("Slider", parent);
            var le = root.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = le.minHeight = 40f;

            var back = Rect("Background", root);
            back.anchorMin = new Vector2(0f, 0.25f);
            back.anchorMax = new Vector2(1f, 0.75f);
            back.offsetMin = back.offsetMax = Vector2.zero;
            Img(back.gameObject, _theme.SliderBack, _theme.SliderBack != null ? Color.white : new Color(0.2f, 0.2f, 0.2f));

            var fillArea = Rect("Fill Area", root);
            fillArea.anchorMin = new Vector2(0f, 0.35f);
            fillArea.anchorMax = new Vector2(1f, 0.65f);
            fillArea.offsetMin = new Vector2(8f, 0f);
            fillArea.offsetMax = new Vector2(-8f, 0f);
            var fill = Rect("Fill", fillArea);
            fill.offsetMin = fill.offsetMax = Vector2.zero;
            Img(fill.gameObject, null, new Color32(0xDE, 0xCB, 0x50, 0xFF));

            var handleArea = Rect("Handle Slide Area", root);
            handleArea.anchorMin = Vector2.zero;
            handleArea.anchorMax = Vector2.one;
            handleArea.offsetMin = new Vector2(10f, 0f);
            handleArea.offsetMax = new Vector2(-10f, 0f);
            var handle = Rect("Handle", handleArea);
            handle.sizeDelta = new Vector2(37f, 0f);
            var handleImg = Img(handle.gameObject, _theme.SliderHandle, Color.white);

            var slider = root.gameObject.AddComponent<Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handleImg;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 1f;
            slider.maxValue = 50f;
            return slider;
        }

        private Button MakeButton(RectTransform rect, string label, float size)
        {
            var img = Img(rect.gameObject, _theme.Button, _theme.Button != null ? new Color32(0xF8, 0xF4, 0xF4, 0xFF) : new Color(0.3f, 0.25f, 0.4f));
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = img;
            Tint(button, new Color32(0x25, 0xFD, 0x08, 0xFF));
            Stretch(Text(rect, label, size, Color.white, TextAlignmentOptions.Center).rectTransform);
            return button;
        }

        private static void Tint(Button button, Color highlight)
        {
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = highlight;
            colors.selectedColor = Color.white;
            colors.pressedColor = new Color32(0xC8, 0xC8, 0xC8, 0xFF);
            button.colors = colors;
        }

        private void Heading(RectTransform parent, string text) => Text(parent, text, 30f, Yellow, TextAlignmentOptions.Left);

        private TextMeshProUGUI Body(RectTransform parent, string text) => Text(parent, text, 24f, Color.white, TextAlignmentOptions.Left);

        private TextMeshProUGUI Note(RectTransform parent, string text) => Text(parent, text, 20f, NoteColor, TextAlignmentOptions.Left);

        private TextMeshProUGUI Text(Transform parent, string text, float size, Color color, TextAlignmentOptions align)
        {
            var rect = Rect("Text", parent);
            var tmp = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (_theme?.Font != null)
                tmp.font = _theme.Font;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.enableWordWrapping = true;
            tmp.richText = true;
            tmp.raycastTarget = false;
            tmp.text = text;
            return tmp;
        }

        // A vertical scrolling list filling the page; returns the content to add rows to.
        private static RectTransform ScrollArea(RectTransform page)
        {
            var scroll = page.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            var view = Rect("View", page);
            Stretch(view);
            Img(view.gameObject, null, new Color(1f, 1f, 1f, 0.004f));
            view.gameObject.AddComponent<RectMask2D>();

            var content = Rect("Content", view);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = content.offsetMax = Vector2.zero;
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 16, 16);
            layout.spacing = 14f;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = view;
            scroll.content = content;
            return content;
        }

        private static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void Place(RectTransform rect, Vector2 anchor, Vector2 size, Vector2 position)
        {
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        private static Image Img(GameObject go, Sprite sprite, Color color)
        {
            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = true;
            return img;
        }

        // Drag the window by any part of it that isn't a control, as with the game's DraggableUI windows.
        private class DragWindow : MonoBehaviour, IDragHandler
        {
            public void OnDrag(PointerEventData e)
            {
                var canvas = GetComponentInParent<Canvas>();
                ((RectTransform)transform).anchoredPosition += e.delta / (canvas != null ? canvas.scaleFactor : 1f);
            }
        }
    }
}
