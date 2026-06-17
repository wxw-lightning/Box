using Unity.Entities;
using UnityEngine;
using Sirenix.OdinInspector;

namespace Sokoban
{
    /// <summary>
    /// 场景桥接：把编辑器拖入的关卡数据库、网格、URP 材质注入 ECS 世界，
    /// 创建游戏单例实体并触发首个关卡生成。固定俯视相机也在此布置。
    /// 用法：场景里建空物体挂本组件，拖入 LevelDatabase 与五类 URP/Lit 材质即可。
    /// </summary>
    public class SokobanBootstrap : MonoBehaviour
    {
        [Required] public LevelDatabase database;

        [Title("渲染资源")]
        [Tooltip("留空则运行时取内置 Cube 网格")]
        public Mesh cubeMesh;
        [Required] public Material floorMaterial;
        [Required] public Material wallMaterial;
        [Required] public Material targetMaterial;
        [Required] public Material boxMaterial;
        [Required] public Material playerMaterial;

        [Title("机制图标 (可空，未指定则该类箱子不显示图标)")]
        [Tooltip("留空则运行时取内置 Quad 网格")]
        public Mesh quadMesh;
        public Material iconAeroMaterial;    // 气动箱图标
        public Material iconHavocMaterial;   // 湮灭箱图标
        public Material iconSpectroMaterial; // 衍射箱图标

        [Title("玩法")]
        [MinValue(0)] public int startLevel = 0;
        [MinValue(0f)] public float moveDuration = 0.12f;

        private EntityManager _em;
        private EntityQuery _stateQuery;
        private Camera _cam;
        private bool _ready;
        private int _lastWidth = -1;
        private int _lastHeight = -1;

        private void Start()
        {
            if (database == null || database.Count == 0)
            {
                Debug.LogError("[Sokoban] 未指定 LevelDatabase 或其为空。");
                enabled = false;
                return;
            }

            var mesh = cubeMesh != null ? cubeMesh : Resources.GetBuiltinResource<Mesh>("Cube.fbx");

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                Debug.LogError("[Sokoban] 默认 ECS World 不可用，Bootstrap 已中止。");
                enabled = false;
                return;
            }
            var em = world.EntityManager;

            var singleton = em.CreateEntity();
            // 开局进入选关界面：先不生成关卡，等玩家在菜单里选定关卡再触发生成。
            em.AddComponentData(singleton, new GameState
            {
                LevelIndex = Mathf.Clamp(startLevel, 0, database.Count - 1),
                LevelCount = database.Count, // 供 UI 立即按关卡数生成选关按钮
                InLevelSelect = true,
            });
            em.AddComponentData(singleton, new MoveCommand());
            em.AddComponentData(singleton, new ControlRequest { SelectLevel = -1 });
            em.AddComponentData(singleton, new RespawnRequest { Value = false }); // 选关后再触发生成
            em.AddBuffer<GridCell>(singleton);
            em.AddBuffer<UndoStep>(singleton);
            em.AddBuffer<UndoEntry>(singleton);
            var quad = quadMesh != null ? quadMesh : Resources.GetBuiltinResource<Mesh>("Quad.fbx");

            em.AddComponentObject(singleton, new RenderResources
            {
                CubeMesh = mesh,
                Floor = floorMaterial,
                Wall = wallMaterial,
                Target = targetMaterial,
                BoxMat = boxMaterial,
                PlayerMat = playerMaterial,
                QuadMesh = quad,
                IconAero = iconAeroMaterial,
                IconHavoc = iconHavocMaterial,
                IconSpectro = iconSpectroMaterial,
                Db = database,
                MoveDuration = moveDuration,
            });

            _em = world.EntityManager;
            _stateQuery = _em.CreateEntityQuery(typeof(GameState));
            _ready = true;

            var startLevelData = database.Get(Mathf.Clamp(startLevel, 0, database.Count - 1));
            if (startLevelData != null)
                RecenterCamera(startLevelData.Width, startLevelData.Height);
        }

        // 每帧检查关卡尺寸变化（N/P 切到不同大小的关卡时）并重新对中相机。
        private void Update()
        {
            if (!_ready || _stateQuery.IsEmptyIgnoreFilter)
                return;

            var state = _stateQuery.GetSingleton<GameState>();
            if (state.Width > 0 && (state.Width != _lastWidth || state.Height != _lastHeight))
                RecenterCamera(state.Width, state.Height);
        }

        private void RecenterCamera(int width, int height)
        {
            _lastWidth = width;
            _lastHeight = height;

            if (_cam == null)
                _cam = Camera.main;
            if (_cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                _cam = go.AddComponent<Camera>();
            }

            float centerX = (width - 1) * 0.5f;
            float centerZ = -(height - 1) * 0.5f;
            _cam.transform.position = new Vector3(centerX, 12f, centerZ);
            _cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            _cam.orthographic = true;
            _cam.orthographicSize = Mathf.Max(width, height) * 0.5f + 1f;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.12f, 0.13f, 0.16f);
        }
    }
}
