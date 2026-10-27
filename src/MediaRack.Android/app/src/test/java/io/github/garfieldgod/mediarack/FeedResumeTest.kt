package io.github.garfieldgod.mediarack

import io.github.garfieldgod.mediarack.ui.feed.PageCache
import io.github.garfieldgod.mediarack.ui.feed.pickResumePosMs
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder

/**
 * 二级页的离线兜底与跨端续播规则。
 * 这两个规则都在 UI 的关键路径上：判错要么"离线却让点"（播不出还假装能播），
 * 要么"续播点被另一台设备的旧进度压掉"（看了半天回到片头）。
 */
class FeedResumeTest {
    @get:Rule
    val tmp = TemporaryFolder()

    // ── 续播游标择优 ──────────────────────────────────────

    @Test
    fun `没有任何进度_不续播`() {
        assertEquals(0L, pickResumePosMs(0, 0, null, 0, 0, 600_000))
    }

    @Test
    fun `本地进度过半_续播`() {
        assertEquals(120_000L, pickResumePosMs(120_000, 5_000, null, 0, 0, 600_000))
    }

    @Test
    fun `进度太短或接近片尾_不续播`() {
        // ≤30s
        assertEquals(0L, pickResumePosMs(20_000, 5_000, null, 0, 0, 600_000))
        // 剩余 ≤20s
        assertEquals(0L, pickResumePosMs(590_000, 5_000, null, 0, 0, 600_000))
    }

    @Test
    fun `时长为0_不续播`() {
        assertEquals(0L, pickResumePosMs(120_000, 5_000, null, 0, 0, 0))
    }

    @Test
    fun `标过看过_不续播`() {
        assertEquals(0L, pickResumePosMs(120_000, 5_000, 9_000, 300_000, 9_500, 600_000))
    }

    @Test
    fun `服务端更新更晚_以服务端为准`() {
        // 电脑上看到 5 分钟（lastPlayAt 9_500），本地停在 2 分钟（updatedAt 5_000）
        assertEquals(300_000L, pickResumePosMs(120_000, 5_000, null, 300_000, 9_500, 600_000))
    }

    @Test
    fun `服务端更旧_信本地`() {
        assertEquals(120_000L, pickResumePosMs(120_000, 9_500, null, 300_000, 5_000, 600_000))
    }

    @Test
    fun `本机刚上报过_时间戳贴近_不信服务端`() {
        // 本地 updatedAt 与服务端 lastPlayAt 差 <1s：那是自己刚报上去的，位置本来就一样，
        // 但若服务端 completed 归零，这里会把续播点抖回 0——1s 阈值就是防这个
        assertEquals(120_000L, pickResumePosMs(120_000, 9_500, null, 0, 9_900, 600_000))
    }

    @Test
    fun `服务端看完归零_续播点清零`() {
        // 另一台设备看完了：服务端 pos=0 且时间戳更新 → 从头播
        assertEquals(0L, pickResumePosMs(120_000, 5_000, null, 0, 9_500, 600_000))
    }

    // ── 二级页离线快照缓存 ─────────────────────────────────

    @Test
    fun `pageCache_写入读回_按服务器隔离`() {
        val cache = PageCache(tmp.newFolder())
        cache.put(1, PageCache.KEY_COLLECTIONS, """[{"id":"a"}]""")
        cache.put(2, PageCache.KEY_COLLECTIONS, """[{"id":"b"}]""")
        assertEquals("""[{"id":"a"}]""", cache.get(1, PageCache.KEY_COLLECTIONS))
        assertEquals("""[{"id":"b"}]""", cache.get(2, PageCache.KEY_COLLECTIONS))
        assertNull(cache.get(1, PageCache.KEY_FAVORITES))
        assertNull(cache.get(3, PageCache.KEY_COLLECTIONS))
    }

    @Test
    fun `pageCache_不同pageKey互不覆盖`() {
        val cache = PageCache(tmp.newFolder())
        cache.put(1, PageCache.keyItems(collectionId = "dir:bili:/Videos"), "col")
        cache.put(1, PageCache.keyItems(favoriteId = "fav_1"), "fav")
        assertEquals("col", cache.get(1, PageCache.keyItems(collectionId = "dir:bili:/Videos")))
        assertEquals("fav", cache.get(1, PageCache.keyItems(favoriteId = "fav_1")))
    }

    @Test
    fun `pageCache_clear只删该服务器`() {
        val cache = PageCache(tmp.newFolder())
        cache.put(1, PageCache.KEY_COLLECTIONS, "a")
        cache.put(2, PageCache.KEY_COLLECTIONS, "b")
        cache.clear(1)
        assertNull(cache.get(1, PageCache.KEY_COLLECTIONS))
        assertEquals("b", cache.get(2, PageCache.KEY_COLLECTIONS))
    }

    @Test
    fun `pageCache_读不存在的文件返回null`() {
        val cache = PageCache(tmp.newFolder())
        assertNull(cache.get(1, PageCache.KEY_COLLECTIONS))
        // 根目录都不存在也不能炸
        assertTrue(PageCache(java.io.File(tmp.newFolder(), "nope")).get(1, "x") == null)
    }
}
