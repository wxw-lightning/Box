using System.Collections.Generic;
using System.Diagnostics;

namespace Sokoban.Editor
{
    /// <summary>
    /// 关卡真实可解性求解器（编辑器作者辅助）。用 BFS 搜索状态空间，判断是否存在把每个箱子
    /// 推到「同属性目标」的着法序列——超出数量配平校验，给出有解/无解/无法判定三态。
    ///
    /// ⚠ 强耦合：本类的单步模拟逐段镜像运行时 <c>Assets/Scripts/Runtime/ECS/GameSystems.cs</c> 的
    /// <c>MovementSystem.OnUpdate</c> + <c>ResolveArrivals</c>（气动爆发 / 衍射推列 / 湮灭锁定 / 过关判定）。
    /// 运行时逻辑绑定 ECS/EntityManager 无法直接复用，故此处为并行纯数据实现。
    /// **任何机制规则改动都必须同步本文件**，否则求解结果会与实际游戏不一致。
    /// 初始 Triggered/Locked 取值镜像 <c>LevelSpawnSystem</c>（出生即在匹配目标的箱子直接视为已触发/锁定）。
    /// </summary>
    public static class LevelSolver
    {
        public enum Solvability { Solvable, Unsolvable, Unknown }

        public readonly struct SolveResult
        {
            public readonly Solvability Status;
            public readonly int Moves;          // 仅 Solvable 时有效：最少着法数
            public readonly int StatesExplored; // 去重后访问过的状态数（诊断用）

            public SolveResult(Solvability status, int moves, int statesExplored)
            {
                Status = status;
                Moves = moves;
                StatesExplored = statesExplored;
            }
        }

        // 静态盘面格子标志位（与 GridFlags 同义，本类自持以免引入 ECS 依赖）。
        private const byte FWall = 1 << 0;
        private const byte FTargetA = 1 << 1;
        private const byte FTargetB = 1 << 2;
        private const byte FTargetC = 1 << 3;

        // 气动爆发的四邻处理顺序，镜像 GameSystems.Dirs。
        private static readonly (int dx, int dy)[] Dirs =
            { (1, 0), (-1, 0), (0, 1), (0, -1) };

        // 玩家移动尝试方向（BFS 扩展，顺序不影响正确性）。
        private static readonly (int dx, int dy)[] MoveDirs =
            { (0, -1), (0, 1), (-1, 0), (1, 0) };

        /// <summary>
        /// 判断关卡是否可解。<paramref name="maxStates"/>/<paramref name="maxSeconds"/> 为搜索预算，
        /// 触顶则返回 <see cref="Solvability.Unknown"/>。调用方应先做数量/玩家配平预检。
        /// </summary>
        public static SolveResult Analyze(LevelAsset lvl, int maxStates, double maxSeconds)
        {
            var board = Board.From(lvl);
            if (board == null || board.BoxCount == 0 || board.Player < 0)
                return new SolveResult(Solvability.Unknown, 0, 0);

            var start = board.InitialState();
            if (board.IsWin(start))
                return new SolveResult(Solvability.Solvable, 0, 1);

            var visited = new HashSet<string> { board.Key(start) };
            var queue = new Queue<(State s, int depth)>();
            queue.Enqueue((start, 0));

            var sw = Stopwatch.StartNew();
            int sinceCheck = 0;

            while (queue.Count > 0)
            {
                // 预算检查（不必每次扩展都读时钟）。
                if (visited.Count >= maxStates)
                    return new SolveResult(Solvability.Unknown, 0, visited.Count);
                if (++sinceCheck >= 2048)
                {
                    sinceCheck = 0;
                    if (sw.Elapsed.TotalSeconds > maxSeconds)
                        return new SolveResult(Solvability.Unknown, 0, visited.Count);
                }

                var (cur, depth) = queue.Dequeue();
                foreach (var (dx, dy) in MoveDirs)
                {
                    var next = cur.Clone();
                    if (!board.ApplyMove(next, dx, dy))
                        continue; // 撞墙/推不动：玩家未移动，丢弃

                    string key = board.Key(next);
                    if (!visited.Add(key))
                        continue;

                    if (board.IsWin(next))
                        return new SolveResult(Solvability.Solvable, depth + 1, visited.Count);

                    queue.Enqueue((next, depth + 1));
                }
            }

            return new SolveResult(Solvability.Unsolvable, 0, visited.Count);
        }

        // ---------- 可变搜索状态：玩家格 + 每箱(格,标志) ----------
        // boxFlag 语义随 kind 而定：kind0 气动/kind2 衍射 = Triggered；kind1 湮灭 = Locked。

        private sealed class State
        {
            public int Player;
            public int[] BoxCell;
            public bool[] BoxFlag;

            public State Clone() => new State
            {
                Player = Player,
                BoxCell = (int[])BoxCell.Clone(),
                BoxFlag = (bool[])BoxFlag.Clone(),
            };
        }

        // ---------- 静态盘面 + 单步模拟（镜像 GameSystems） ----------

        private sealed class Board
        {
            private int _w, _h;
            private byte[] _flags;       // 每格地形/目标位，索引 row*W+col
            private byte[] _kind;        // 每箱属性 0/1/2（不入状态）
            private int[] _initialBox;   // 每箱初始格
            private bool[] _initialFlag; // 每箱初始 Triggered/Locked
            private int _player;
            private int[] _occ;          // 复用占用表：格 -> 箱下标，-1 空

            public int BoxCount => _kind?.Length ?? 0;
            public int Player => _player;

            public static Board From(LevelAsset lvl)
            {
                if (lvl == null || lvl.cells == null) return null;
                int w = lvl.Width, h = lvl.Height;
                if (w <= 0 || h <= 0 || (long)w * h >= 32768) return null; // key 编码假定格数 < 32768

                var b = new Board { _w = w, _h = h };
                b._flags = new byte[w * h];
                b._occ = new int[w * h];

                var boxCells = new List<int>();
                var boxKinds = new List<byte>();
                var boxFlags = new List<bool>();
                int player = -1;

                for (int row = 0; row < h; row++)
                {
                    for (int col = 0; col < w; col++)
                    {
                        var c = lvl.cells[col, row];
                        int idx = row * w + col;

                        byte f = 0;
                        if (c.IsWall()) f |= FWall;
                        if (c.IsTargetA()) f |= FTargetA;
                        if (c.IsTargetB()) f |= FTargetB;
                        if (c.IsTargetC()) f |= FTargetC;
                        b._flags[idx] = f;

                        if (c.HasBox())
                        {
                            byte k = c.BoxKindOf();
                            boxCells.Add(idx);
                            boxKinds.Add(k);
                            // 出生即在匹配目标 → 直接已触发/锁定（镜像 LevelSpawnSystem）。
                            bool born = k == 1 ? c.IsTargetB() : k == 2 ? c.IsTargetC() : c.IsTargetA();
                            boxFlags.Add(born);
                        }
                        if (c.HasPlayer())
                            player = player < 0 ? idx : player; // 仅取第一个（防御）
                    }
                }

                b._kind = boxKinds.ToArray();
                b._initialBox = boxCells.ToArray();
                b._initialFlag = boxFlags.ToArray();
                b._player = player;
                return b;
            }

            public State InitialState() => new State
            {
                Player = _player,
                BoxCell = (int[])_initialBox.Clone(),
                BoxFlag = (bool[])_initialFlag.Clone(),
            };

            // 越界视为墙；否则查 Wall 位。idx<0 表示越界。
            private bool IsWallIdx(int idx) => idx < 0 || (_flags[idx] & FWall) != 0;

            // 从 idx 沿 (dx,dy) 走一格，越界返回 -1。
            private int Step(int idx, int dx, int dy)
            {
                int col = idx % _w + dx;
                int row = idx / _w + dy;
                if (col < 0 || col >= _w || row < 0 || row >= _h) return -1;
                return row * _w + col;
            }

            private bool IsLocked(State s, int b) => _kind[b] == 1 && s.BoxFlag[b];

            private bool HasTarget(int idx, byte mask) => idx >= 0 && (_flags[idx] & mask) != 0;

            private bool IsAeroArrival(State s, int b) =>
                _kind[b] == 0 && !s.BoxFlag[b] && HasTarget(s.BoxCell[b], FTargetA);

            private bool IsSpectroArrival(State s, int b) =>
                _kind[b] == 2 && !s.BoxFlag[b] && HasTarget(s.BoxCell[b], FTargetC);

            /// <summary>对 <paramref name="s"/> 原地施加一次玩家移动；返回玩家是否实际移动（状态是否改变）。</summary>
            public bool ApplyMove(State s, int dx, int dy)
            {
                int next = Step(s.Player, dx, dy);
                if (IsWallIdx(next))
                    return false;

                BuildOcc(s);
                try
                {
                    int pushed = -1;
                    int boxAtNext = _occ[next];
                    if (boxAtNext >= 0)
                    {
                        int beyond = Step(next, dx, dy);
                        if (IsLocked(s, boxAtNext) || IsWallIdx(beyond) || _occ[beyond] >= 0)
                            return false; // 锁定箱 / 前方墙 / 前方有箱：整步失败

                        MoveBox(s, boxAtNext, next, beyond);
                        pushed = boxAtNext;
                    }

                    s.Player = next;
                    ResolveArrivals(s, pushed, dx, dy, next);
                    return true;
                }
                finally
                {
                    ClearOcc(s);
                }
            }

            // 到达结算 FIFO（含跨元素连锁），镜像 ResolveArrivals。
            private void ResolveArrivals(State s, int seed, int seedDx, int seedDy, int playerCell)
            {
                var queue = new Queue<(int box, int dx, int dy)>();
                if (seed >= 0)
                    OnBoxArrived(s, seed, seedDx, seedDy, queue);

                while (queue.Count > 0)
                {
                    var (box, dx, dy) = queue.Dequeue();
                    if (IsAeroArrival(s, box))
                        AeroBurst(s, box, playerCell, queue);
                    else if (IsSpectroArrival(s, box))
                        SpectroBeam(s, box, dx, dy, playerCell, queue);
                }
            }

            private void OnBoxArrived(State s, int b, int dx, int dy, Queue<(int, int, int)> queue)
            {
                TryLockHavoc(s, b);
                if (IsAeroArrival(s, b) || IsSpectroArrival(s, b))
                    queue.Enqueue((b, dx, dy));
            }

            private void TryLockHavoc(State s, int b)
            {
                if (_kind[b] != 1 || s.BoxFlag[b]) return;
                if (HasTarget(s.BoxCell[b], FTargetB))
                    s.BoxFlag[b] = true; // 锁定，此后等同墙
            }

            // 气动爆发：四邻箱各向外一格；受阻(墙/箱/玩家/锁定箱)则该箱不动。
            private void AeroBurst(State s, int center, int playerCell, Queue<(int, int, int)> queue)
            {
                int c = s.BoxCell[center];
                s.BoxFlag[center] = true; // Triggered

                foreach (var (dx, dy) in Dirs)
                {
                    int nb = Step(c, dx, dy);
                    if (nb < 0) continue;
                    int b = _occ[nb];
                    if (b < 0 || IsLocked(s, b)) continue;
                    int dest = Step(nb, dx, dy);
                    if (dest < 0 || IsWallIdx(dest) || _occ[dest] >= 0 || dest == playerCell) continue;
                    MoveBox(s, b, nb, dest);
                    OnBoxArrived(s, b, dx, dy, queue);
                }
            }

            // 衍射推动：沿到达方向射线上所有箱，远→近各推一格。
            private void SpectroBeam(State s, int center, int dx, int dy, int playerCell, Queue<(int, int, int)> queue)
            {
                int c = s.BoxCell[center];
                s.BoxFlag[center] = true; // Triggered
                if (dx == 0 && dy == 0) return;

                var line = new List<int>();
                for (int p = Step(c, dx, dy); p >= 0; p = Step(p, dx, dy))
                    if (_occ[p] >= 0) line.Add(p);

                for (int i = line.Count - 1; i >= 0; i--)
                {
                    int from = line[i];
                    int b = _occ[from];
                    if (b < 0 || IsLocked(s, b)) continue;
                    int dest = Step(from, dx, dy);
                    if (dest < 0 || IsWallIdx(dest) || _occ[dest] >= 0 || dest == playerCell) continue;
                    MoveBox(s, b, from, dest);
                    OnBoxArrived(s, b, dx, dy, queue);
                }
            }

            private void MoveBox(State s, int b, int from, int to)
            {
                _occ[from] = -1;
                _occ[to] = b;
                s.BoxCell[b] = to;
            }

            private void BuildOcc(State s)
            {
                for (int i = 0; i < _occ.Length; i++) _occ[i] = -1;
                for (int b = 0; b < s.BoxCell.Length; b++) _occ[s.BoxCell[b]] = b;
            }

            private void ClearOcc(State s)
            {
                for (int b = 0; b < s.BoxCell.Length; b++) _occ[s.BoxCell[b]] = -1;
            }

            /// <summary>每箱在匹配属性目标上即过关（箱数 &gt; 0 由调用方保证）。</summary>
            public bool IsWin(State s)
            {
                for (int b = 0; b < _kind.Length; b++)
                {
                    byte need = _kind[b] == 2 ? FTargetC : _kind[b] == 1 ? FTargetB : FTargetA;
                    if (!HasTarget(s.BoxCell[b], need)) return false;
                }
                return true;
            }

            /// <summary>去重键：玩家格 + 同属性箱按 (格,标志) 规范排序合并对称态。</summary>
            public string Key(State s)
            {
                int n = _kind.Length;
                var packed = new int[n]; // (kind<<17) | (cell<<1) | flag
                for (int b = 0; b < n; b++)
                    packed[b] = (_kind[b] << 17) | (s.BoxCell[b] << 1) | (s.BoxFlag[b] ? 1 : 0);
                System.Array.Sort(packed);

                var chars = new char[1 + 2 * n];
                chars[0] = (char)s.Player;
                for (int b = 0; b < n; b++)
                {
                    chars[1 + 2 * b] = (char)(packed[b] >> 17);       // kind
                    chars[2 + 2 * b] = (char)(packed[b] & 0x1FFFF);   // (cell<<1)|flag, < 65536
                }
                return new string(chars);
            }
        }
    }
}
