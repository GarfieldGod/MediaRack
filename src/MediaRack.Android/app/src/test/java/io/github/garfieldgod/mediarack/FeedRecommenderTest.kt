package io.github.garfieldgod.mediarack

import io.github.garfieldgod.mediarack.ui.feed.FeedRecommender
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 信息流客户端侧纯逻辑：
 * - 本地序列：播过的只占 1/10 名额、均匀分散、30 分钟冷却、小库保护、确定性
 * - 「全部」混入：守恒（一条不多不少）、占比、确定性、空侧退化
 */
class FeedRecommenderTest {

    private data class Item(val id: Int, val playedAt: Long)

    private val now = System.currentTimeMillis()

    // ── shuffleLocal ──────────────────────────────────────

    @Test
    fun `本地缓存 - 前 100 条里播过的只占十分之一`() {
        // 60 条播过（1~60 小时前）+ 540 条没播过。名额按全序总量算，
        // 所以要在"截断"上看比例——整份列出来时必然是池子的原始配比
        val seen = (1..60).map { Item(it, now - it * 3_600_000L) }
        val unseen = (1001..1540).map { Item(it, 0L) }
        val out = FeedRecommender.shuffleLocal(seen + unseen, seed = 42L) { it.playedAt }

        assertEquals(600, out.size)
        assertEquals(10, out.take(100).count { it.playedAt > 0L })
    }

    @Test
    fun `本地缓存 - 播过的均匀分散不聚堆`() {
        val seen = (1..60).map { Item(it, now - it * 3_600_000L) }
        val unseen = (1001..1540).map { Item(it, 0L) }
        val out = FeedRecommender.shuffleLocal(seen + unseen, seed = 42L) { it.playedAt }

        // 分层取位保证前 100 条里任意 20 条窗口至少有 1 条播过的（不会前半页全新、后半页全旧）
        for (start in 0..80) {
            assertTrue(
                "第 $start 条起 20 条全是没播过的",
                out.subList(start, start + 20).any { it.playedAt > 0L },
            )
        }
    }

    @Test
    fun `本地缓存 - 播过的位置随 seed 变化不固定`() {
        val items = (1..600).map { Item(it, if (it % 2 == 0) now - it * 60_000L else 0L) }
        val slotsA = FeedRecommender.shuffleLocal(items, seed = 1L) { it.playedAt }
            .take(100).mapIndexedNotNull { i, it -> if (it.playedAt > 0L) i else null }
        val slotsB = FeedRecommender.shuffleLocal(items, seed = 2L) { it.playedAt }
            .take(100).mapIndexedNotNull { i, it -> if (it.playedAt > 0L) i else null }

        // 若还留着"每第 10 条固定一个重温槽"的实现，两个 seed 的位置会完全一样
        assertTrue("播过的位置与 seed 无关，像是固定槽位", slotsA != slotsB)
    }

    @Test
    fun `本地缓存 - 未看耗尽时剩余槽位给播过的`() {
        val unseen = (1..5).map { Item(it, 0L) }
        val seen = (101..140).map { Item(it, now - it * 60_000L) }
        val out = FeedRecommender.shuffleLocal(unseen + seen, seed = 3L) { it.playedAt }

        assertEquals(45, out.size) // 一条不少
        // 5 条没播过的会先被用掉，之后只能全给播过的
        assertEquals(5, out.take(10).count { it.playedAt == 0L })
    }

    @Test
    fun `本地缓存 - 同seed同序不同seed不同序`() {
        val items = (1..60).map { Item(it, if (it % 2 == 0) 0L else now - it * 60_000L) }
        val a1 = FeedRecommender.shuffleLocal(items, seed = 7L) { it.playedAt }
        val a2 = FeedRecommender.shuffleLocal(items, seed = 7L) { it.playedAt }
        val b = FeedRecommender.shuffleLocal(items, seed = 8L) { it.playedAt }
        assertEquals(a1.map { it.id }, a2.map { it.id })
        // 60 条规模下不同 seed 完全同序的概率可忽略
        assertTrue(a1.map { it.id } != b.map { it.id })
    }

    @Test
    fun `本地缓存 - 全是已看时仍是随机序`() {
        val seen = (1..20).map { Item(it, now - it * 60_000L) }
        val out = FeedRecommender.shuffleLocal(seen, seed = 1L) { it.playedAt }
        // 一条不少
        assertEquals(seen.map { it.id }.sorted(), out.map { it.id }.sorted())
        // 不再按"最久没播"升序
        assertTrue(out.map { it.id } != seen.sortedBy { it.playedAt }.map { it.id })
    }

    @Test
    fun `本地缓存 - 刚播过的进冷却期不再推荐`() {
        val old = (1..40).map { Item(it, now - it * 3_600_000L) }
        val recent = (201..205).map { Item(it, now - 60_000L) } // 5 条刚播过
        val out = FeedRecommender.shuffleLocal(old + recent, seed = 3L) { it.playedAt }
        // 排除后剩 40 ≥ MIN_POOL，刚播过的全部出局
        assertTrue(out.none { it.id in 201..205 })
    }

    @Test
    fun `本地缓存 - 小库不启用冷却避免清空`() {
        val old = (1..4).map { Item(it, now - it * 3_600_000L) }
        val recent = (201..208).map { Item(it, now - 60_000L) } // 8 条刚播过，排除后只剩 4 条
        val out = FeedRecommender.shuffleLocal(old + recent, seed = 3L) { it.playedAt }
        assertEquals(12, out.size) // 排除会让池子太小 → 全部保留
    }

    @Test
    fun `本地缓存 - 输出守恒`() {
        // 全部时间戳都在冷却窗之外（>30 分钟），冷却不启用才能验证守恒
        val items = (1..55).map { Item(it, if (it % 3 == 0) 0L else now - it * 3_600_000L) }
        val out = FeedRecommender.shuffleLocal(items, seed = 9L) { it.playedAt }
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
