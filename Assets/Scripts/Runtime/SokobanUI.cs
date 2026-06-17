using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace Sokoban
{
    /// <summary>
    /// 运行时构建 uGUI：开局的<b>选关界面</b>（按关卡数量生成关卡按钮），以及游戏内 HUD
    /// （顶部关卡/步数信息、过关面板、重玩/切关/返回选关按钮）。
    /// 每帧读 ECS <see cref="GameState"/> 单例刷新并在「选关 ↔ 游戏」两态间切换；
    /// 按钮通过写 <see cref="ControlRequest"/> 单例发指令（选关写 SelectLevel，返回菜单写 OpenMenu）。
    /// </summary>
    public class SokobanUI : MonoBehaviour
    {
        private Text _infoText;
        private Text _hintText;
        private GameObject _winPanel;
        private GameObject _hudRoot;    // 游戏内 HUD 容器
        private GameObject _menuRoot;   // 选关界面容器（全屏覆盖层）
        private Transform _levelGrid;   // 选关按钮的父节点（GridLayoutGroup）
        private Font _font;

        private EntityManager _em;
        private EntityQuery _stateQuery;
        private EntityQuery _ctrlQuery;
        private EntityQuery _resQuery;
        private bool _worldReady;
        private bool _menuBuilt;        // 选关按钮已按关卡数量生成

        // 缓存上次显示的状态，避免每帧重建信息字符串（GC）/重复刷新面板。
        private int _lastLevelIndex = -1;
        private int _lastLevelCount = -1;
        private int _lastMoves = -1;
        private bool _lastWon;
        private int _lastInLevelSelect = -1; // -1=未应用，0/1=上次的选关态

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

            if (!_menuBuilt)
                TryBuildLevelButtons();

            var state = _stateQuery.GetSingleton<GameState>();

            // 选关 ↔ 游戏 两态切换（仅在变化时切换激活，避免每帧 SetActive）。
            int inMenu = state.InLevelSelect ? 1 : 0;
            if (inMenu != _lastInLevelSelect)
            {
                _lastInLevelSelect = inMenu;
                _menuRoot.SetActive(inMenu == 1);
                _hudRoot.SetActive(inMenu == 0);
                if (inMenu == 1)
                    _winPanel.SetActive(false);
            }

            if (state.InLevelSelect)
                return; // 菜单态：HUD 隐藏，无需刷新信息/提示/过关面板

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
            var db = GetDatabase();
            var level = db != null ? db.Get(levelIndex) : null;
            return level != null ? level.hint ?? "" : "";
        }

        private LevelDatabase GetDatabase()
        {
            if (_resQuery.IsEmptyIgnoreFilter) return null;
            var res = _em.GetComponentObject<RenderResources>(_resQuery.GetSingletonEntity());
            return res?.Db;
        }

        // ---------- 按钮 → 写 ControlRequest ----------

        private void SendReset() => Mutate((ref ControlRequest c) => c.Reset = true);
        private void SendNext() => Mutate((ref ControlRequest c) => c.LevelDelta = +1);
        private void SendPrev() => Mutate((ref ControlRequest c) => c.LevelDelta = -1);
        private void SendOpenMenu() => Mutate((ref ControlRequest c) => c.OpenMenu = true);
        private void SendSelectLevel(int index) => Mutate((ref ControlRequest c) => c.SelectLevel = index);

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

        // uGUI 的按钮点击依赖场景里存在 EventSystem；本项目 UI 全运行时搭建，故缺省时在此补建。
        // 项目用旧版 Input（GameSystems 里的 UnityEngine.Input），对应 StandaloneInputModule。
        private void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            go.transform.SetParent(transform, false);
        }

        private void BuildUI()
        {
            EnsureEventSystem();

            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

            BuildLevelSelect(canvasGo.transform);
            BuildHud(canvasGo.transform);

            // 过关面板（HUD 之上的独立覆盖层）。
            _winPanel = new GameObject("WinPanel", typeof(Image));
            _winPanel.transform.SetParent(canvasGo.transform, false);
            var img = _winPanel.GetComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.55f);
            Stretch(img.rectTransform);

            var winText = CreateText(_winPanel.transform, "WinText", 48, TextAnchor.MiddleCenter, Color.white);
            winText.text = "过关！\nLevel Clear\n\n按 N 下一关 / R 重玩 / 选关返回";
            Stretch(winText.rectTransform);
            _winPanel.SetActive(false);

            // 开局先显示选关界面。
            _menuRoot.SetActive(true);
            _hudRoot.SetActive(false);
        }

        // 选关界面：全屏不透明背景 + 标题 + 关卡按钮网格（按钮在世界就绪后按关卡数量生成）。
        private void BuildLevelSelect(Transform parent)
        {
            _menuRoot = new GameObject("LevelSelect", typeof(Image));
            _menuRoot.transform.SetParent(parent, false);
            _menuRoot.GetComponent<Image>().color = new Color(0.10f, 0.11f, 0.14f, 1f);
            Stretch((RectTransform)_menuRoot.transform);

            var title = CreateText(_menuRoot.transform, "Title", 40, TextAnchor.UpperCenter, Color.white);
            title.text = "选择关卡";
            var titleRt = title.rectTransform;
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -40f);
            titleRt.sizeDelta = new Vector2(0f, 60f);

            var subtitle = CreateText(_menuRoot.transform, "Subtitle", 20, TextAnchor.UpperCenter,
                new Color(0.7f, 0.72f, 0.78f));
            subtitle.text = "点击关卡开始游戏";
            var subRt = subtitle.rectTransform;
            subRt.anchorMin = new Vector2(0f, 1f);
            subRt.anchorMax = new Vector2(1f, 1f);
            subRt.pivot = new Vector2(0.5f, 1f);
            subRt.anchoredPosition = new Vector2(0f, -104f);
            subRt.sizeDelta = new Vector2(0f, 30f);

            var gridGo = new GameObject("LevelGrid", typeof(RectTransform), typeof(GridLayoutGroup));
            gridGo.transform.SetParent(_menuRoot.transform, false);
            var gridRt = (RectTransform)gridGo.transform;
            gridRt.anchorMin = new Vector2(0.06f, 0.08f);
            gridRt.anchorMax = new Vector2(0.94f, 0.74f);
            gridRt.offsetMin = Vector2.zero;
            gridRt.offsetMax = Vector2.zero;

            var grid = gridGo.GetComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(150f, 52f);
            grid.spacing = new Vector2(14f, 14f);
            grid.childAlignment = TextAnchor.MiddleCenter;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 5;
            _levelGrid = gridGo.transform;
        }

        // 游戏内 HUD：信息栏、教学提示、底部按钮行（重玩/上一关/下一关/选关）。
        private void BuildHud(Transform parent)
        {
            _hudRoot = new GameObject("HUD", typeof(RectTransform));
            _hudRoot.transform.SetParent(parent, false);
            Stretch((RectTransform)_hudRoot.transform);
            var hud = _hudRoot.transform;

            _infoText = CreateText(hud, "Info", 22, TextAnchor.UpperLeft, Color.white);
            var infoRt = _infoText.rectTransform;
            infoRt.anchorMin = new Vector2(0f, 1f);
            infoRt.anchorMax = new Vector2(1f, 1f);
            infoRt.pivot = new Vector2(0f, 1f);
            infoRt.anchoredPosition = new Vector2(12f, -8f);
            infoRt.sizeDelta = new Vector2(-24f, 60f);

            // 教学提示：信息栏下方，淡黄色多行自动换行。空串则不占视觉空间。
            _hintText = CreateText(hud, "Hint", 20, TextAnchor.UpperLeft,
                new Color(1f, 0.92f, 0.55f));
            _hintText.horizontalOverflow = HorizontalWrapMode.Wrap;
            var hintRt = _hintText.rectTransform;
            hintRt.anchorMin = new Vector2(0f, 1f);
            hintRt.anchorMax = new Vector2(1f, 1f);
            hintRt.pivot = new Vector2(0f, 1f);
            hintRt.anchoredPosition = new Vector2(12f, -72f);
            hintRt.sizeDelta = new Vector2(-24f, 120f);

            // 底部按钮行（4 颗，中心间距 120）。
            CreateButton(hud, "重玩 (R)", new Vector2(-180f, 30f), SendReset);
            CreateButton(hud, "上一关 (P)", new Vector2(-60f, 30f), SendPrev);
            CreateButton(hud, "下一关 (N)", new Vector2(60f, 30f), SendNext);
            CreateButton(hud, "选关", new Vector2(180f, 30f), SendOpenMenu);
        }

        // 世界就绪、关卡数据库可读后，按关卡数量生成关卡按钮（仅一次）。
        private void TryBuildLevelButtons()
        {
            var db = GetDatabase();
            if (db == null) return; // 世界/资源尚未就绪，下一帧再试

            for (int i = 0; i < db.Count; i++)
            {
                int index = i; // 闭包捕获本地副本
                var level = db.Get(i);
                string name = level != null ? level.levelName : null;
                string label = string.IsNullOrWhiteSpace(name) || name == "新关卡"
                    ? $"关卡 {i + 1}"
                    : $"{i + 1}. {name}";
                CreateGridButton(_levelGrid, label, () => SendSelectLevel(index));
            }
            _menuBuilt = true;
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

        // 选关网格按钮：尺寸/位置由 GridLayoutGroup 接管，这里不手动设锚点。
        private void CreateGridButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(label, typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.22f, 0.34f, 0.5f, 0.95f);
            go.GetComponent<Button>().onClick.AddListener(onClick);

            var txt = CreateText(go.transform, "Label", 20, TextAnchor.MiddleCenter, Color.white);
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
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
