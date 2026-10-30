package io.github.garfieldgod.mediarack.ui.feed

/**
 * 信息流在客户端侧的两块处理：
 *
 * - [shuffleLocal]：「本地缓存」视图的序列。播过的条目只占 [SEEN_SHARE] 的名额，
 *   其余全给没播过的；名额内怎么排、插在哪，都由 seed 决定。
 * - [mix]：「全部」视图的混入层。服务端只对远程内容出序列，本地条目由这里
 *   按 `本地数 : 远程数` 的占比随机穿插进去，两边都不会被淹没。
 *
 * 两个函数都是**确定性**的：同 seed + 同输入 = 同输出。信息流的列表在一次会话里
 * 会被反复重算（buildFeed 两段式先本地后远程、切过滤都从 allCache 重投影），
 * 确定性保证这些重算不会让序列抖动。
 *
 * 「单个服务器」视图不需要这里做事：远程拉取时服务端已按同一个 seed 排好。
 * 「收藏夹连播」（FeedFilter.Liked）是固定队列，明确不走这里。
 */
internal object FeedRecommender {

    /** 刚播过的冷却窗口：30 分钟内播过的不再推（与服务端同一规则）。 */
    private const val RECENT_MS = 30 * 60 * 1000L

    /** 小库保护：排除刚播过之后剩不足这个数，就不排除（否则小库会被清空一页）。 */
    private const val MIN_POOL = 10

    /** 播过内容的配额：每 10 条里约 1 条。服务端 `MediaRepository.SeenShare` 用同一个值。 */
    private const val SEEN_SHARE = 0.1

    /**
     * 本地条目序列：冷却之后按"播过 / 没播过"分两池，各自洗牌，再按名额交错。
     *
     * 名额按池分配，不是给未看加权：加权的话最终比例会随两池大小变化——
     * 未看 60 条、播过 240 条时拿不到 90%，只能拿到约 69%。
     *
     * @param playedAt 条目的"上次播放时间"（0 = 这台设备上从没播过）
     */
    fun <T> shuffleLocal(items: List<T>, seed: Long, playedAt: (T) -> Long): List<T> {
        if (items.size <= 1) return items

        // 冷却：刚播过的先挪出去；挪完后剩太少（小库）就全保留
        val cutoff = System.currentTimeMillis() - RECENT_MS
        val kept = items.filter { playedAt(it) <= cutoff }
        val pool = if (kept.size >= MIN_POOL) kept else items

        val rnd = java.util.Random(seed)
        val unseen = pool.filter { playedAt(it) == 0L }.shuffled(rnd)
        val seen = pool.filter { playedAt(it) != 0L }.shuffled(rnd)
        return interleaveByQuota(unseen, seen, rnd)
    }

    /**
     * 按名额把 [seen] 均匀插进 [unseen]，名额 = 总数 × [SEEN_SHARE]。
     *
     * 位置用分层随机：把序列切成 k 段、每段内随机取一位，所以既均匀分散（不会前半页全是旧片），
     * 又不会形成"每第 10 条固定是旧片"的可预测节拍。
     * 任一侧耗尽时，剩余槽位全部给另一侧——池子小不会让序列变短。
     */
    private fun <T> interleaveByQuota(
        unseen: List<T>,
        seen: List<T>,
        rnd: java.util.Random,
    ): List<T> {
        val total = unseen.size + seen.size
        if (seen.isEmpty() || unseen.isEmpty() || total == 0) return unseen + seen

        val quota = minOf(seen.size, Math.round(total * SEEN_SHARE).toInt())
        val slots = HashSet<Int>(quota)
        for (seg in 0 until quota) {
            // 第 seg 段的区间 [lo, hi)，两段不重叠，取位必然唯一
            val lo = seg * total / quota
            val hi = (seg + 1) * total / quota
            slots.add(lo + rnd.nextInt(hi - lo))
        }

        val out = ArrayList<T>(total)
        var ui = 0
        var si = 0
        for (i in 0 until total) {
            if (si < seen.size && (i in slots || ui >= unseen.size)) out.add(seen[si++])
            else out.add(unseen[ui++])
        }
        return out
    }

    /**
     * 「全部」视图：把本地条目按占比随机穿插进服务端序列。
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
