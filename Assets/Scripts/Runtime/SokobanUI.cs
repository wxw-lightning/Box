using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace Sokoban
{
    /// <summary>
    /// 运行时构建 uGUI：顶部关卡/步数信息，过关面板，重玩/切关按钮。
    /// 每帧读 ECS <see cref="GameState"/> 单例刷新；按钮通过写 <see cref="ControlRequest"/> 单例发指令。
    /// </summary>
    public class SokobanUI : MonoBehaviour
    {
        private Text _infoText;
        private Text _hintText;
        private GameObject _winPanel;
        private Font _font;

        private EntityManager _em;
        private EntityQuery _stateQuery;
        private EntityQuery _ctrlQuery;
        private EntityQuery _resQuery;
        private bool _worldReady;

        // 缓存上次显示的状态，避免每帧重建信息字符串（GC）/重复刷新面板。
        private int _lastLevelIndex = -1;
        private int _lastLevelCount = -1;
        private int _lastMoves = -1;
        private bool _lastWon;

        private void Start()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            BuildUI();
        }

        private bool EnsureWorld()
        {
            if (_worldReady) return true;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return false;
            _em = world.EntityManager;
            _stateQuery = _em.CreateEntityQuery(typeof(GameState));
            _ctrlQuery = _em.CreateEntityQuery(typeof(ControlRequest));
            _resQuery = _em.CreateEntityQuery(typeof(RenderResources));
            _worldReady = true;
            return true;
        }

        private void Update()
        {
            if (!EnsureWorld() || _stateQuery.IsEmptyIgnoreFilter)
                return;

            var state = _stateQuery.GetSingleton<GameState>();

            bool levelChanged = state.LevelIndex != _lastLevelIndex;

            if (levelChanged ||
                state.LevelCount != _lastLevelCount ||
                state.Moves != _lastMoves)
            {
                _lastLevelIndex = state.LevelIndex;
                _lastLevelCount = state.LevelCount;
                _lastMoves = state.Moves;
                _infoText.text = $"关卡 {state.LevelIndex + 1}/{state.LevelCount}    步数 {state.Moves}\n" +
                                 "方向键/WASD 移动   U 撤销   R 重玩   N 下一关 P 前一关";
            }

            // 提示文字仅随关卡切换变化，不必每帧重建。
            if (levelChanged)
                _hintText.text = GetLevelHint(state.LevelIndex);

            if (state.Won != _lastWon)
            {
                _lastWon = state.Won;
                _winPanel.SetActive(state.Won);
            }
        }

        /// <summary>从托管单例 <see cref="RenderResources"/> 的关卡数据库取该关教学提示（纯文本）。</summary>
        private string GetLevelHint(int levelIndex)
        {
            if (_resQuery.IsEmptyIgnoreFilter) return "";
            var res = _em.GetComponentObject<RenderResources>(_resQuery.GetSingletonEntity());
            var level = res?.Db != null ? res.Db.Get(levelIndex) : null;
            return level != null ? level.hint ?? "" : "";
        }

        // ---------- 按钮 → 写 ControlRequest ----------

        private void SendReset() => Mutate((ref ControlRequest c) => c.Reset = true);
        private void SendNext() => Mutate((ref ControlRequest c) => c.LevelDelta = +1);
        private void SendPrev() => Mutate((ref ControlRequest c) => c.LevelDelta = -1);

        private delegate void CtrlMutator(ref ControlRequest c);

        private void Mutate(CtrlMutator mutator)
        {
            if (!EnsureWorld() || _ctrlQuery.IsEmptyIgnoreFilter) return;
            var e = _ctrlQuery.GetSingletonEntity();
            var c = _em.GetComponentData<ControlRequest>(e);
            mutator(ref c);
            _em.SetComponentData(e, c);
        }

        // ---------- 运行时搭建 UI ----------

        private void BuildUI()
        {
            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

            _infoText = CreateText(canvasGo.transform, "Info", 22, TextAnchor.UpperLeft, Color.white);
            var infoRt = _infoText.rectTransform;
            infoRt.anchorMin = new Vector2(0f, 1f);
            infoRt.anchorMax = new Vector2(1f, 1f);
            infoRt.pivot = new Vector2(0f, 1f);
            infoRt.anchoredPosition = new Vector2(12f, -8f);
            infoRt.sizeDelta = new Vector2(-24f, 60f);

            // 教学提示：信息栏下方，淡黄色多行自动换行。空串则不占视觉空间。
            _hintText = CreateText(canvasGo.transform, "Hint", 20, TextAnchor.UpperLeft,
                new Color(1f, 0.92f, 0.55f));
            _hintText.horizontalOverflow = HorizontalWrapMode.Wrap;
            var hintRt = _hintText.rectTransform;
            hintRt.anchorMin = new Vector2(0f, 1f);
            hintRt.anchorMax = new Vector2(1f, 1f);
            hintRt.pivot = new Vector2(0f, 1f);
            hintRt.anchoredPosition = new Vector2(12f, -72f);
            hintRt.sizeDelta = new Vector2(-24f, 120f);

            // 底部按钮行。
            CreateButton(canvasGo.transform, "重玩 (R)", new Vector2(-120f, 30f), SendReset);
            CreateButton(canvasGo.transform, "上一关 (P)", new Vector2(0f, 30f), SendPrev);
            CreateButton(canvasGo.transform, "下一关 (N)", new Vector2(120f, 30f), SendNext);

            // 过关面板。
            _winPanel = new GameObject("WinPanel", typeof(Image));
            _winPanel.transform.SetParent(canvasGo.transform, false);
            var img = _winPanel.GetComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.55f);
            Stretch(img.rectTransform);

            var winText = CreateText(_winPanel.transform, "WinText", 48, TextAnchor.MiddleCenter, Color.white);
            winText.text = "过关！\nLevel Clear\n\n按 N 下一关 / R 重玩";
            Stretch(winText.rectTransform);

            _winPanel.SetActive(false);
        }

        private Text CreateText(Transform parent, string name, int size, TextAnchor anchor, Color color)
        {
            var go = new GameObject(name, typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = _font;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = color;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        private void CreateButton(Transform parent, string label, Vector2 bottomCenterOffset, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(label, typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.2f, 0.22f, 0.28f, 0.9f);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(110f, 36f);
            rt.anchoredPosition = bottomCenterOffset;
            go.GetComponent<Button>().onClick.AddListener(onClick);

            var txt = CreateText(go.transform, "Label", 18, TextAnchor.MiddleCenter, Color.white);
            txt.text = label;
            Stretch(txt.rectTransform);
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
