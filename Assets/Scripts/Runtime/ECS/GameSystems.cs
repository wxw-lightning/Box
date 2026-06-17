using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Transforms;
using Unity.Rendering;
using UnityEngine;

namespace Sokoban
{
    /// <summary>读传统输入 → 写 MoveCommand / ControlRequest。主线程（SystemBase 非 Burst）。</summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class SokobanInputSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<GameState>();
            RequireForUpdate<MoveCommand>();
        }

        protected override void OnUpdate()
        {
            var state = SystemAPI.GetSingleton<GameState>();
            if (state.InLevelSelect)
                return; // 选关界面屏蔽所有游戏内按键，选关由 UI 按钮驱动

            var ctrl = SystemAPI.GetSingletonRW<ControlRequest>();
            if (Input.GetKeyDown(KeyCode.R)) ctrl.ValueRW.Reset = true;
            if (Input.GetKeyDown(KeyCode.U)) ctrl.ValueRW.Undo = true;
            if (Input.GetKeyDown(KeyCode.N)) ctrl.ValueRW.LevelDelta = +1;
            if (Input.GetKeyDown(KeyCode.P)) ctrl.ValueRW.LevelDelta = -1;

            if (state.Won || state.Animating)
                return;

            int2 dir = default;
            bool has = false;
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) { dir = new int2(0, -1); has = true; }
            else if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) { dir = new int2(0, 1); has = true; }
            else if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) { dir = new int2(-1, 0); has = true; }
            else if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) { dir = new int2(1, 0); has = true; }

            if (has)
            {
                var cmd = SystemAPI.GetSingletonRW<MoveCommand>();
                cmd.ValueRW.Dir = dir;
                cmd.ValueRW.HasValue = true;
            }
        }
    }

    /// <summary>
    /// 推箱核心规则（移植自 v1）：撞墙不动；箱子前方为墙或另一箱子则整步失败（不能一次推两个）。
    /// 成功则更新逻辑格、压撤销栈、给玩家/箱子加平滑动画。
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class MovementSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<GameState>();

        protected override void OnUpdate()
        {
            var cmdRef = SystemAPI.GetSingletonRW<MoveCommand>();
            if (!cmdRef.ValueRO.HasValue)
                return;
            cmdRef.ValueRW.HasValue = false;
            int2 dir = cmdRef.ValueRO.Dir;

            // 按值读取（下方 Animate 是结构性变更，会让 RefRW 句柄失效；状态写入推迟到末尾重新获取）。
            var state = SystemAPI.GetSingleton<GameState>();
            if (state.Won || state.Animating)
                return;
            int W = state.Width;
            int H = state.Height;

            if (!SystemAPI.TryGetSingletonEntity<Player>(out var playerEntity))
                return;

            var singleton = SystemAPI.GetSingletonEntity<GameState>();
            var grid = EntityManager.GetBuffer<GridCell>(singleton);

            int2 playerPos = EntityManager.GetComponentData<GridPosition>(playerEntity).Value;
            int2 next = playerPos + dir;
            if (IsWall(grid, W, H, next))
                return;

            // 箱子位置查询表。
            var boxMap = new NativeHashMap<int2, Entity>(16, Allocator.Temp);
            foreach (var (gp, ent) in SystemAPI.Query<RefRO<GridPosition>>().WithAll<Box>().WithEntityAccess())
                boxMap.TryAdd(gp.ValueRO.Value, ent);

            // 静态层快照：后续 Animate(加 MoveAnimation) 是结构性变更，会让 grid/undo 缓冲句柄失效；
            // 故墙/目标位先拷进 NativeArray，撤销明细先攒进 NativeList，全部结构性变更完成后再落盘。
            var flags = new NativeArray<byte>(W * H, Allocator.Temp);
            for (int i = 0; i < flags.Length; i++) flags[i] = grid[i].Flags;

            float duration = SystemAPI.ManagedAPI.TryGetSingleton<RenderResources>(out var res) ? res.MoveDuration : 0.12f;
            var stepEntries = new NativeList<UndoEntry>(Allocator.Temp);

            Entity pushedBox = Entity.Null;
            int2 pushedDir = default;
            if (boxMap.TryGetValue(next, out var boxEntity))
            {
                int2 beyond = next + dir;
                if (IsLocked(boxEntity) || IsWall(flags, W, H, beyond) || boxMap.ContainsKey(beyond))
                {
                    flags.Dispose();
                    stepEntries.Dispose();
                    boxMap.Dispose();
                    return; // 箱子被锁定(湮灭墙)或前方被挡，不能推
                }

                MoveBox(boxEntity, next, beyond, boxMap, stepEntries, duration);
                pushedBox = boxEntity;
                pushedDir = dir;
            }
            EntityManager.SetComponentData(playerEntity, new GridPosition { Value = next });
            Animate(playerEntity, next, duration);

            // 元素「首次到达」结算（含连锁）：初始到达就是被玩家推动的那个箱子，方向=推动方向。
            ResolveArrivals(boxMap, flags, W, H, next, duration, stepEntries, pushedBox, pushedDir);

            // 结构性变更已全部完成，重新取撤销缓冲落盘（先明细后步，二者 LIFO 配套弹出）。
            var undoEntries = EntityManager.GetBuffer<UndoEntry>(singleton);
            for (int i = 0; i < stepEntries.Length; i++) undoEntries.Add(stepEntries[i]);
            var undoSteps = EntityManager.GetBuffer<UndoStep>(singleton);
            undoSteps.Add(new UndoStep { Player = playerPos, BoxCount = stepEntries.Length });

            flags.Dispose();
            stepEntries.Dispose();
            boxMap.Dispose();

            // 结构性变更已完成，重新获取单例 RW 再写状态。
            var stateRW = SystemAPI.GetSingletonRW<GameState>();
            stateRW.ValueRW.Moves += 1;
            stateRW.ValueRW.Animating = true;
        }

        private static readonly int2[] Dirs = { new int2(1, 0), new int2(-1, 0), new int2(0, 1), new int2(0, -1) };

        // 一次待结算的「到达」：箱子 + 其到达方向（衍射按此方向推；气动忽略方向）。
        private struct Arrival { public Entity Box; public int2 Dir; }

        /// <summary>
        /// 元素「首次到达匹配目标」结算（FIFO，含跨元素连锁）：气动→四邻各向外推一格；衍射→沿到达方向整条射线各推一格；
        /// 湮灭→在移动处即时锁定（见 <see cref="OnBoxArrived"/>）。被推动的箱子再次结算；Triggered/Locked 保证每箱仅一次、必然终止。
        /// </summary>
        private void ResolveArrivals(NativeHashMap<int2, Entity> boxMap, NativeArray<byte> flags,
            int W, int H, int2 playerCell, float duration, NativeList<UndoEntry> stepEntries, Entity seed, int2 seedDir)
        {
            var queue = new NativeQueue<Arrival>(Allocator.Temp);
            if (seed != Entity.Null)
                OnBoxArrived(seed, seedDir, flags, W, H, stepEntries, queue);

            while (queue.TryDequeue(out var arr))
            {
                if (IsAeroArrival(arr.Box, flags, W, H))
                    AeroBurst(arr.Box, boxMap, flags, W, H, playerCell, duration, stepEntries, queue);
                else if (IsSpectroArrival(arr.Box, flags, W, H))
                    SpectroBeam(arr.Box, arr.Dir, boxMap, flags, W, H, playerCell, duration, stepEntries, queue);
            }
            queue.Dispose();
        }

        // 气动爆发：四邻箱子各向外一格；被推走者再结算（连锁）。受阻(墙/箱/玩家/锁定箱)则该箱不动。
        private void AeroBurst(Entity center, NativeHashMap<int2, Entity> boxMap, NativeArray<byte> flags,
            int W, int H, int2 playerCell, float duration, NativeList<UndoEntry> stepEntries, NativeQueue<Arrival> queue)
        {
            int2 c = EntityManager.GetComponentData<GridPosition>(center).Value;
            EntityManager.SetComponentData(center, new AeroState { Triggered = true });
            stepEntries.Add(new UndoEntry { Box = center, From = c, RevertArrival = true });

            foreach (var d in Dirs)
            {
                int2 nb = c + d;
                if (!boxMap.TryGetValue(nb, out var b)) continue;
                if (IsLocked(b)) continue;
                int2 dest = nb + d;
                if (IsWall(flags, W, H, dest) || boxMap.ContainsKey(dest) || math.all(dest == playerCell)) continue;
                MoveBox(b, nb, dest, boxMap, stepEntries, duration);
                OnBoxArrived(b, d, flags, W, H, stepEntries, queue);
            }
        }

        // 衍射推动：沿到达方向射线上的所有箱子各推进一格。远→近处理，整列连推不自撞；遇墙/被占/锁定箱则该箱不动。
        private void SpectroBeam(Entity center, int2 dir, NativeHashMap<int2, Entity> boxMap, NativeArray<byte> flags,
            int W, int H, int2 playerCell, float duration, NativeList<UndoEntry> stepEntries, NativeQueue<Arrival> queue)
        {
            int2 c = EntityManager.GetComponentData<GridPosition>(center).Value;
            EntityManager.SetComponentData(center, new SpectroState { Triggered = true });
            stepEntries.Add(new UndoEntry { Box = center, From = c, RevertArrival = true });
            if (math.all(dir == int2.zero)) return;

            var line = new NativeList<int2>(Allocator.Temp);
            for (int2 p = c + dir; p.x >= 0 && p.x < W && p.y >= 0 && p.y < H; p += dir)
                if (boxMap.ContainsKey(p)) line.Add(p);

            for (int i = line.Length - 1; i >= 0; i--)
            {
                int2 from = line[i];
                if (!boxMap.TryGetValue(from, out var b)) continue;
                if (IsLocked(b)) continue; // 锁定湮灭箱=墙
                int2 dest = from + dir;
                if (IsWall(flags, W, H, dest) || boxMap.ContainsKey(dest) || math.all(dest == playerCell)) continue;
                MoveBox(b, from, dest, boxMap, stepEntries, duration);
                OnBoxArrived(b, dir, flags, W, H, stepEntries, queue);
            }
            line.Dispose();
        }

        // 移动一个箱子：记撤销、更新位置查询表、设逻辑位置、播放动画。
        private void MoveBox(Entity b, int2 from, int2 to, NativeHashMap<int2, Entity> boxMap,
            NativeList<UndoEntry> stepEntries, float duration)
        {
            stepEntries.Add(new UndoEntry { Box = b, From = from, RevertArrival = false });
            boxMap.Remove(from);
            boxMap.Add(to, b);
            EntityManager.SetComponentData(b, new GridPosition { Value = to });
            Animate(b, to, duration);
        }

        // 箱子到达新格后的「首次到达」结算：湮灭即时锁定；气动/衍射入队待调度（含连锁）。
        private void OnBoxArrived(Entity b, int2 dir, NativeArray<byte> flags, int W, int H,
            NativeList<UndoEntry> stepEntries, NativeQueue<Arrival> queue)
        {
            TryLockHavoc(b, flags, W, H, stepEntries);
            if (IsAeroArrival(b, flags, W, H) || IsSpectroArrival(b, flags, W, H))
                queue.Enqueue(new Arrival { Box = b, Dir = dir });
        }

        // 气动箱(持 AeroState)、未触发、且当前格带气动(A)目标位 → 视为「首次到达」。
        private bool IsAeroArrival(Entity e, NativeArray<byte> flags, int W, int H)
        {
            if (e == Entity.Null || !EntityManager.HasComponent<AeroState>(e)) return false;
            if (EntityManager.GetComponentData<AeroState>(e).Triggered) return false;
            int2 p = EntityManager.GetComponentData<GridPosition>(e).Value;
            if (p.x < 0 || p.x >= W || p.y < 0 || p.y >= H) return false;
            return (flags[p.y * W + p.x] & GridFlags.TargetA) != 0;
        }

        // 衍射箱(持 SpectroState)、未触发、且当前格带衍射(C)目标位 → 视为「首次到达」。
        private bool IsSpectroArrival(Entity e, NativeArray<byte> flags, int W, int H)
        {
            if (e == Entity.Null || !EntityManager.HasComponent<SpectroState>(e)) return false;
            if (EntityManager.GetComponentData<SpectroState>(e).Triggered) return false;
            int2 p = EntityManager.GetComponentData<GridPosition>(e).Value;
            if (p.x < 0 || p.x >= W || p.y < 0 || p.y >= H) return false;
            return (flags[p.y * W + p.x] & GridFlags.TargetC) != 0;
        }

        // 湮灭箱已锁定 → 等同墙：玩家与气动爆发都推不动它。
        private bool IsLocked(Entity e) =>
            EntityManager.HasComponent<HavocState>(e) && EntityManager.GetComponentData<HavocState>(e).Locked;

        // 湮灭箱(持 HavocState)未锁定、且当前格带湮灭(B)目标位 → 锁定为不可推动，并记一条可撤销翻转。
        private void TryLockHavoc(Entity e, NativeArray<byte> flags, int W, int H, NativeList<UndoEntry> stepEntries)
        {
            if (!EntityManager.HasComponent<HavocState>(e)) return;
            if (EntityManager.GetComponentData<HavocState>(e).Locked) return;
            int2 p = EntityManager.GetComponentData<GridPosition>(e).Value;
            if (p.x < 0 || p.x >= W || p.y < 0 || p.y >= H) return;
            if ((flags[p.y * W + p.x] & GridFlags.TargetB) == 0) return;
            EntityManager.SetComponentData(e, new HavocState { Locked = true });
            stepEntries.Add(new UndoEntry { Box = e, From = p, RevertArrival = true });
        }

        // 幂等：一步内同一箱可能被玩家推、再被爆发推；重复调用只更新终点，保留原始 From/Elapsed，避免重复 AddComponent。
        private void Animate(Entity e, int2 toCell, float duration)
        {
            var lt = EntityManager.GetComponentData<LocalTransform>(e);
            float3 to = LevelSpawnSystem.CellToWorld(toCell, lt.Position.y);
            if (EntityManager.HasComponent<MoveAnimation>(e))
            {
                var anim = EntityManager.GetComponentData<MoveAnimation>(e);
                anim.To = to;
                EntityManager.SetComponentData(e, anim);
            }
            else
            {
                EntityManager.AddComponentData(e, new MoveAnimation { From = lt.Position, To = to, Duration = duration, Elapsed = 0f });
            }
        }

        public static bool IsWall(DynamicBuffer<GridCell> grid, int w, int h, int2 c)
        {
            if (c.x < 0 || c.x >= w || c.y < 0 || c.y >= h) return true;
            return (grid[c.y * w + c.x].Flags & GridFlags.Wall) != 0;
        }

        public static bool IsWall(NativeArray<byte> flags, int w, int h, int2 c)
        {
            if (c.x < 0 || c.x >= w || c.y < 0 || c.y >= h) return true;
            return (flags[c.y * w + c.x] & GridFlags.Wall) != 0;
        }
    }

    /// <summary>插值移动动画；全部结束后清 Animating 标志，放行下一步输入。</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MovementSystem))]
    public partial class MoveAnimationSystem : SystemBase
    {
        private EntityQuery _animQuery;

        protected override void OnCreate()
        {
            RequireForUpdate<GameState>();
            _animQuery = GetEntityQuery(ComponentType.ReadOnly<MoveAnimation>());
        }

        protected override void OnUpdate()
        {
            // 没有动画在跑：跳过 ECB 分配与遍历；仅在状态需要时把 Animating 落为 false
            // （撤销时 ControlSystem.Snap 会直接移除 MoveAnimation，故这里仍需负责清标志）。
            if (_animQuery.IsEmptyIgnoreFilter)
            {
                var st = SystemAPI.GetSingletonRW<GameState>();
                if (st.ValueRO.Animating)
                    st.ValueRW.Animating = false;
                return;
            }

            float dt = SystemAPI.Time.DeltaTime;
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            int active = 0;

            foreach (var (lt, anim, e) in
                     SystemAPI.Query<RefRW<LocalTransform>, RefRW<MoveAnimation>>().WithEntityAccess())
            {
                anim.ValueRW.Elapsed += dt;
                float u = anim.ValueRO.Duration <= 0f ? 1f : math.saturate(anim.ValueRO.Elapsed / anim.ValueRO.Duration);
                float s = u * u * (3f - 2f * u); // smoothstep
                lt.ValueRW.Position = math.lerp(anim.ValueRO.From, anim.ValueRO.To, s);

                if (u >= 1f)
                {
                    lt.ValueRW.Position = anim.ValueRO.To;
                    ecb.RemoveComponent<MoveAnimation>(e);
                }
                else active++;
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();

            SystemAPI.GetSingletonRW<GameState>().ValueRW.Animating = active > 0;
        }
    }

    /// <summary>所有箱子都在目标格 → 过关。逻辑位置即时判定（不必等动画）。</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MovementSystem))]
    public partial class WinSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<GameState>();

        protected override void OnUpdate()
        {
            var stateRef = SystemAPI.GetSingletonRW<GameState>();
            if (stateRef.ValueRO.Won || stateRef.ValueRO.Animating)
                return; // 等动画落定再判定，避免过关提示比箱子早 ~0.12s 弹出
            int W = stateRef.ValueRO.Width;
            var singleton = SystemAPI.GetSingletonEntity<GameState>();
            var grid = EntityManager.GetBuffer<GridCell>(singleton);

            bool all = true;
            int count = 0;
            foreach (var (gp, kind) in SystemAPI.Query<RefRO<GridPosition>, RefRO<BoxKind>>().WithAll<Box>())
            {
                count++;
                int2 p = gp.ValueRO.Value;
                byte k = kind.ValueRO.Value;
                byte need = k == 2 ? GridFlags.TargetC : k == 1 ? GridFlags.TargetB : GridFlags.TargetA;
                if ((grid[p.y * W + p.x].Flags & need) == 0) { all = false; break; }
            }
            if (count > 0 && all)
                stateRef.ValueRW.Won = true;
        }
    }

    /// <summary>箱子在目标格→绿，否则棕（每实体 URP _BaseColor 覆盖）。</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MovementSystem))]
    public partial class BoxColorSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<GameState>();

        protected override void OnUpdate()
        {
            int W = SystemAPI.GetSingleton<GameState>().Width;
            var singleton = SystemAPI.GetSingletonEntity<GameState>();
            var grid = EntityManager.GetBuffer<GridCell>(singleton);

            // 每属性各自的基础色与「已满足」提亮色（绿仍绿、紫仍紫）。
            float4 baseA = ToFloat4(CellType.Box.ToColor());
            float4 onA = ToFloat4(CellType.BoxOnTarget.ToColor());
            float4 baseB = ToFloat4(CellType.BoxB.ToColor());
            float4 onB = ToFloat4(CellType.BoxBOnTarget.ToColor());
            float4 baseC = ToFloat4(CellType.BoxC.ToColor());
            float4 onC = ToFloat4(CellType.BoxCOnTarget.ToColor());

            foreach (var (gp, kind, col) in
                     SystemAPI.Query<RefRO<GridPosition>, RefRO<BoxKind>, RefRW<URPMaterialPropertyBaseColor>>().WithAll<Box>())
            {
                int2 p = gp.ValueRO.Value;
                byte k = kind.ValueRO.Value;
                byte need = k == 2 ? GridFlags.TargetC : k == 1 ? GridFlags.TargetB : GridFlags.TargetA;
                bool onMatch = (grid[p.y * W + p.x].Flags & need) != 0;
                float4 baseCol = k == 2 ? baseC : k == 1 ? baseB : baseA;
                float4 onCol = k == 2 ? onC : k == 1 ? onB : onA;
                col.ValueRW.Value = onMatch ? onCol : baseCol;
            }
        }

        private static float4 ToFloat4(Color c) => new float4(c.r, c.g, c.b, c.a);
    }

    /// <summary>处理撤销/重置/切关请求。</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(MovementSystem))]
    public partial class ControlSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<GameState>();

        protected override void OnUpdate()
        {
            var ctrl = SystemAPI.GetSingletonRW<ControlRequest>();
            var stateRef = SystemAPI.GetSingletonRW<GameState>();

            // 从菜单选定某关：加载该关并进入游戏。
            if (ctrl.ValueRO.SelectLevel >= 0)
            {
                int count = math.max(1, stateRef.ValueRO.LevelCount);
                stateRef.ValueRW.LevelIndex = math.clamp(ctrl.ValueRO.SelectLevel, 0, count - 1);
                stateRef.ValueRW.InLevelSelect = false;
                ctrl.ValueRW.SelectLevel = -1;
                RequestRespawn();
                return;
            }

            // 返回选关界面：清空当前关卡实体，进入菜单态（不生成新关卡）。
            if (ctrl.ValueRO.OpenMenu)
            {
                ctrl.ValueRW.OpenMenu = false;
                EnterLevelSelect();
                return;
            }

            // 处于选关界面时，忽略其余游戏内请求（防御性，正常已被输入系统屏蔽）。
            if (stateRef.ValueRO.InLevelSelect)
            {
                ctrl.ValueRW.Reset = false;
                ctrl.ValueRW.Undo = false;
                ctrl.ValueRW.LevelDelta = 0;
                return;
            }

            if (ctrl.ValueRO.Reset)
            {
                ctrl.ValueRW.Reset = false;
                RequestRespawn();
                return;
            }

            if (ctrl.ValueRO.LevelDelta != 0)
            {
                int delta = ctrl.ValueRO.LevelDelta;
                ctrl.ValueRW.LevelDelta = 0;
                int count = math.max(1, stateRef.ValueRO.LevelCount);
                stateRef.ValueRW.LevelIndex = math.clamp(stateRef.ValueRO.LevelIndex + delta, 0, count - 1);
                RequestRespawn();
                return;
            }

            if (ctrl.ValueRO.Undo)
            {
                ctrl.ValueRW.Undo = false;
                DoUndo();
            }
        }

        private void RequestRespawn() => SystemAPI.GetSingletonRW<RespawnRequest>().ValueRW.Value = true;

        // 进入选关界面：销毁当前关卡的全部可视实体并清状态，菜单覆盖层由 UI 显示。
        private void EnterLevelSelect()
        {
            // 销毁实体是结构性变更，会让此前取得的 RefRW 句柄失效，故状态写入推迟到销毁之后重新获取。
            var oldQuery = SystemAPI.QueryBuilder().WithAll<LevelElement>().Build();
            EntityManager.DestroyEntity(oldQuery);

            var singleton = SystemAPI.GetSingletonEntity<GameState>();
            EntityManager.GetBuffer<UndoStep>(singleton).Clear();
            EntityManager.GetBuffer<UndoEntry>(singleton).Clear();

            var stateRW = SystemAPI.GetSingletonRW<GameState>();
            stateRW.ValueRW.InLevelSelect = true;
            stateRW.ValueRW.Won = false;
            stateRW.ValueRW.Animating = false;
            stateRW.ValueRW.Moves = 0;
        }

        private void DoUndo()
        {
            var singleton = SystemAPI.GetSingletonEntity<GameState>();
            var steps = EntityManager.GetBuffer<UndoStep>(singleton);
            if (steps.Length == 0)
                return;

            UndoStep step = steps[steps.Length - 1];
            steps.RemoveAt(steps.Length - 1);
            int movesBefore = SystemAPI.GetSingleton<GameState>().Moves;

            // 先把本步明细（缓冲末尾 BoxCount 条）弹入临时表，再做 Snap——
            // Snap 移除 MoveAnimation 是结构性变更，会让 UndoEntry 缓冲句柄失效。
            var entries = EntityManager.GetBuffer<UndoEntry>(singleton);
            var toRestore = new NativeList<UndoEntry>(Allocator.Temp);
            int n = math.min(step.BoxCount, entries.Length);
            for (int i = 0; i < n; i++)
            {
                toRestore.Add(entries[entries.Length - 1]);
                entries.RemoveAt(entries.Length - 1);
            }

            // 按 LIFO 还原：同一箱在本步若移动多次，最后落定的是其步初位置；RevertArrival 同时清气动触发/湮灭锁定标志。
            foreach (var en in toRestore)
            {
                if (!EntityManager.Exists(en.Box)) continue;
                EntityManager.SetComponentData(en.Box, new GridPosition { Value = en.From });
                Snap(en.Box, en.From);
                if (en.RevertArrival)
                {
                    if (EntityManager.HasComponent<AeroState>(en.Box))
                        EntityManager.SetComponentData(en.Box, new AeroState { Triggered = false });
                    if (EntityManager.HasComponent<HavocState>(en.Box))
                        EntityManager.SetComponentData(en.Box, new HavocState { Locked = false });
                    if (EntityManager.HasComponent<SpectroState>(en.Box))
                        EntityManager.SetComponentData(en.Box, new SpectroState { Triggered = false });
                }
            }
            toRestore.Dispose();

            // Snap 内部可能移除 MoveAnimation（结构性变更），故状态写入推迟到最后重新获取 RW。
            if (SystemAPI.TryGetSingletonEntity<Player>(out var playerEntity))
            {
                EntityManager.SetComponentData(playerEntity, new GridPosition { Value = step.Player });
                Snap(playerEntity, step.Player);
            }

            var stateRW = SystemAPI.GetSingletonRW<GameState>();
            stateRW.ValueRW.Moves = math.max(0, movesBefore - 1);
            stateRW.ValueRW.Won = false;
        }

        // 撤销瞬时生效：直接对齐 transform 并移除可能在进行的动画。
        private void Snap(Entity e, int2 cell)
        {
            var lt = EntityManager.GetComponentData<LocalTransform>(e);
            lt.Position = LevelSpawnSystem.CellToWorld(cell, lt.Position.y);
            EntityManager.SetComponentData(e, lt);
            if (EntityManager.HasComponent<MoveAnimation>(e))
                EntityManager.RemoveComponent<MoveAnimation>(e);
        }
    }
}
