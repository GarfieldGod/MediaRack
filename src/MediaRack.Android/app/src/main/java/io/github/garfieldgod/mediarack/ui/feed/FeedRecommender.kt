package io.github.garfieldgod.mediarack.ui.feed

/**
 * 信息流推荐（客户端侧的两块）：
 *
 * - [recommendLocal]：「本地缓存」视图的推荐序列。本地条目没有跨设备播放计数，
 *   用 marks 的 `played_at` 做代理——未播过的（0）均匀洗牌优先，播过的按"最久没播"
 *   排序进重温槽，效果与"低播优先"几乎等价。
 * - [mix]：「全部」视图的混入层。服务端只对远程内容出推荐序列，本地条目由这里
 *   按 `本地数 : 远程数` 的占比随机穿插进去，两边都不会被淹没。
 *
 * 两个函数都是**确定性**的：同 seed + 同输入 = 同输出。信息流的列表在一次会话里
 * 会被反复重算（buildFeed 两段式先本地后远程、切过滤都从 allCache 重投影），
 * 确定性保证这些重算不会让序列抖动。
 *
 * 「单个服务器」视图不需要这里做事：远程拉取时服务端已按 recommend 排好。
 * 「收藏夹连播」（FeedFilter.Liked）是固定队列，明确不走推荐。
 */
internal object FeedRecommender {

    /** 每 N 个槽位 1 个重温槽（与服务器端 recommend 的周期一致）。 */
    const val REPLAY_EVERY = 6

    /** 刚播过的冷却窗口：30 分钟内播过的不再推（本地侧同样规则）。 */
    private const val RECENT_MS = 30 * 60 * 1000L

    /** 小库保护：排除刚播过之后剩不足这个数，就不排除（否则小库会被清空一页）。 */
    private const val MIN_POOL = 10

    /**
     * 本地推荐序列。
     *
     * @param playedAt 条目的"上次播放时间"（0 = 这台设备上从没播过）
     */
    fun <T> recommendLocal(items: List<T>, seed: Long, playedAt: (T) -> Long): List<T> {
        if (items.size <= 1) return items

        // 冷却：刚播过的先挪出去；挪完后剩太少（小库）就全保留
        val cutoff = System.currentTimeMillis() - RECENT_MS
        val kept = items.filter { playedAt(it) <= cutoff }
        val pool = if (kept.size >= MIN_POOL) kept else items

        val unseen = pool.filter { playedAt(it) <= 0 }
        val seen = pool.filter { playedAt(it) > 0 }.sortedBy { playedAt(it) }

        val rnd = java.util.Random(seed)
        val unseenShuffled = unseen.shuffled(rnd)

        // 槽位合并：每 6 槽 1 个重温槽从 seen 取（最久没播优先），其余从未看洗牌序列取；
        // 未看见底后剩余的已看按序补齐
        val out = ArrayList<T>(pool.size)
        var ui = 0
        var si = 0
        while (out.size < pool.size) {
            val replaySlot = out.size % REPLAY_EVERY == REPLAY_EVERY - 1
            if (replaySlot && si < seen.size) {
                out.add(seen[si++])
            } else if (ui < unseenShuffled.size) {
                out.add(unseenShuffled[ui++])
            } else if (si < seen.size) {
                out.add(seen[si++])
            } else {
                break // 防御：两个池都空了（不该发生，pool 恰好是两池之和）
            }
        }
        return out
    }

    /**
     * 「全部」视图：把本地条目按占比随机穿插进服务端推荐序列。
     * 占比 = 本地数 / 总数——本地 300 / 远程 6000 就约每 20 条插 1 条，
     * 而不是死板的固定间隔；插入位置由 seed 决定，会话内稳定。
     */
    fun <T> mix(remote: List<T>, local: List<T>, seed: Long): List<T> {
        if (local.isEmpty()) return remote
        if (remote.isEmpty()) return local

        val total = remote.size + local.size
        val rnd = java.util.Random(seed)
        // 从全槽位里随机抽 local.size 个给本地条目（洗牌后取前 k 个，拒绝式抽样在
        // 本地占比接近 1 时会退化，洗牌法任何占比都稳定）
        val slots = ArrayList<Int>(total)
        for (i in 0 until total) slots.add(i)
        slots.shuffle(rnd)
        val localSlots = slots.subList(0, local.size).toHashSet()

        val out = ArrayList<T>(total)
        var li = 0
        var ri = 0
        for (i in 0 until total) {
            if (i in localSlots) out.add(local[li++]) else out.add(remote[ri++])
        }
        return out
    }
}
