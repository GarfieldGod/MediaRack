package io.github.garfieldgod.mediarack

import io.github.garfieldgod.mediarack.ui.feed.FeedRecommender
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 信息流推荐（客户端侧纯逻辑）：
 * - 本地推荐：未看优先、重温槽周期、最久没播优先、30 分钟冷却、小库保护、确定性
 * - 「全部」混入：守恒（一条不多不少）、占比、确定性、空侧退化
 */
class FeedRecommenderTest {

    private data class Item(val id: Int, val playedAt: Long)

    private val now = System.currentTimeMillis()

    // ── recommendLocal ──────────────────────────────────────

    @Test
    fun `本地推荐 - 前6条里5条未看加1条重温且重温取最久没播`() {
        // id 越大播得越久远（1 小时前 ~ 30 小时前）
        val seen = (1..30).map { Item(it, now - it * 3_600_000L) }
        val unseen = (101..150).map { Item(it, 0L) }
        val out = FeedRecommender.recommendLocal(seen + unseen, seed = 42L) { it.playedAt }

        val first6 = out.take(FeedRecommender.REPLAY_EVERY)
        assertEquals(5, first6.count { it.playedAt == 0L })
        val replay = first6.last()
        assertTrue(replay.playedAt > 0L)
        // 重温槽 = "最久没播"的那条（playedAt 最小 = id 30）
        assertEquals(seen.minBy { it.playedAt }.id, replay.id)
    }

    @Test
    fun `本地推荐 - 同seed同序不同seed不同序`() {
        val items = (1..60).map { Item(it, if (it % 2 == 0) 0L else now - it * 60_000L) }
        val a1 = FeedRecommender.recommendLocal(items, seed = 7L) { it.playedAt }
        val a2 = FeedRecommender.recommendLocal(items, seed = 7L) { it.playedAt }
        val b = FeedRecommender.recommendLocal(items, seed = 8L) { it.playedAt }
        assertEquals(a1.map { it.id }, a2.map { it.id })
        // 60 条规模下不同 seed 完全同序的概率可忽略
        assertTrue(a1.map { it.id } != b.map { it.id })
    }

    @Test
    fun `本地推荐 - 全是已看时退化为最久没播升序`() {
        val seen = (1..20).map { Item(it, now - it * 60_000L) }
        val out = FeedRecommender.recommendLocal(seen, seed = 1L) { it.playedAt }
        assertEquals(seen.sortedBy { it.playedAt }.map { it.id }, out.map { it.id })
    }

    @Test
    fun `本地推荐 - 刚播过的进冷却期不再推荐`() {
        val old = (1..40).map { Item(it, now - it * 3_600_000L) }
        val recent = (201..205).map { Item(it, now - 60_000L) } // 5 条刚播过
        val out = FeedRecommender.recommendLocal(old + recent, seed = 3L) { it.playedAt }
        // 排除后剩 40 ≥ MIN_POOL，刚播过的全部出局
        assertTrue(out.none { it.id in 201..205 })
    }

    @Test
    fun `本地推荐 - 小库不启用冷却避免清空`() {
        val old = (1..4).map { Item(it, now - it * 3_600_000L) }
        val recent = (201..208).map { Item(it, now - 60_000L) } // 8 条刚播过，排除后只剩 4 条
        val out = FeedRecommender.recommendLocal(old + recent, seed = 3L) { it.playedAt }
        assertEquals(12, out.size) // 排除会让池子太小 → 全部保留
    }

    @Test
    fun `本地推荐 - 输出守恒`() {
        // 全部时间戳都在冷却窗之外（>30 分钟），冷却不启用才能验证守恒
        val items = (1..55).map { Item(it, if (it % 3 == 0) 0L else now - it * 3_600_000L) }
        val out = FeedRecommender.recommendLocal(items, seed = 9L) { it.playedAt }
        assertEquals(items.map { it.id }.sorted(), out.map { it.id }.sorted())
    }

    // ── mix（全部视图） ─────────────────────────────────────

    @Test
    fun `混入 - 守恒且本地全部出现`() {
        val remote = (1..100).map { Item(it, 0L) }
        val local = (1001..1010).map { Item(it, 0L) }
        val out = FeedRecommender.mix(remote, local, seed = 5L)
        assertEquals(110, out.size)
        assertEquals(local.map { it.id }.toSet(), out.map { it.id }.toSet() intersect local.map { it.id }.toSet())
        assertEquals(items100plusLocal(remote, local), out.map { it.id }.sorted())
    }

    private fun items100plusLocal(remote: List<Item>, local: List<Item>) =
        (remote.map { it.id } + local.map { it.id }).sorted()

    @Test
    fun `混入 - 同seed同序`() {
        val remote = (1..50).map { Item(it, 0L) }
        val local = (1001..1015).map { Item(it, 0L) }
        val a1 = FeedRecommender.mix(remote, local, seed = 11L)
        val a2 = FeedRecommender.mix(remote, local, seed = 11L)
        assertEquals(a1.map { it.id }, a2.map { it.id })
    }

    @Test
    fun `混入 - 空侧退化`() {
        val remote = (1..5).map { Item(it, 0L) }
        val local = (9..10).map { Item(it, 0L) }
        assertEquals(remote.map { it.id }, FeedRecommender.mix(remote, emptyList(), 1L).map { it.id })
        assertEquals(local.map { it.id }, FeedRecommender.mix(emptyList(), local, 1L).map { it.id })
    }
}
