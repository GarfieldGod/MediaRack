package io.github.garfieldgod.mediarack

import io.github.garfieldgod.mediarack.ui.feed.FeedItem
import io.github.garfieldgod.mediarack.ui.feed.nextPageOutsideSource
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * 跨源翻页跳过不可达源：从当前页往后找第一个别的源的页。
 * 判错就会出现「跳掉一个正常源」或「卡在坏源里一页页重连」。
 */
class CrossSourceSkipTest {

    private fun item(serverId: Long?, key: String) = FeedItem(
        scope = serverId?.let { "srv:$it" } ?: "local:1",
        key = key,
        title = key,
        collectionName = null,
        collectionDir = null,
        sourceId = 0,
        serverId = serverId,
        remoteSourceId = null,
        layout = "single",
        durationMs = 0,
        sizeBytes = 0,
        width = 0,
        height = 0,
        videoUri = null,
        audioUri = null,
    )

    // 本地 2 条 + 服务器 1 的 2 条 + 服务器 2 的 1 条
    private val feed = listOf(
        item(null, "l0"),
        item(null, "l1"),
        item(1L, "a0"),
        item(1L, "a1"),
        item(2L, "b0"),
    )

    @Test
    fun skipsWholeUnreachableSource() {
        // 站在服务器 1 的第一条，服务器 1 不可达 → 应该落到服务器 2 的第一条
        assertEquals(4, nextPageOutsideSource(feed, 2, 1L))
        // 站在服务器 1 的第二条也一样
        assertEquals(4, nextPageOutsideSource(feed, 3, 1L))
    }

    @Test
    fun returnsNullWhenUnreachableSourceIsLast() {
        // 服务器 2 是最后一个源 → null，调用方退回上一个可播条目
        assertNull(nextPageOutsideSource(feed, 4, 2L))
    }

    @Test
    fun skipsEveryItemOfThatServer() {
        // 从本地条目往后找"不是服务器 1"的页 → 要跨过服务器 1 的全部条目
        assertEquals(4, nextPageOutsideSource(feed, 1, 1L))
    }

    @Test
    fun findsImmediateNeighbour() {
        assertEquals(3, nextPageOutsideSource(feed, 2, 2L))
        // 两页不同源：下一跳就是第 1 页
        assertEquals(1, nextPageOutsideSource(listOf(item(1L, "x"), item(2L, "y")), 0, 1L))
    }
}
