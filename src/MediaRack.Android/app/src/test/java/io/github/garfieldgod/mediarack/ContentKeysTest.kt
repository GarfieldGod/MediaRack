package io.github.garfieldgod.mediarack

import io.github.garfieldgod.mediarack.core.ContentKeys
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * 跨端对照向量：期望值由 PC 端 `MediaRack.Core/Models/MediaKey.cs` 实算得出。
 * 这三个值若变，PC 与 Android 的 key 就会分叉，下载预取去重直接失效——不许改。
 */
class ContentKeysTest {
    @Test
    fun `single key 与 PC 端向量一致`() {
        assertEquals("s_838b4682be790925", ContentKeys.forSingle("videos/测试 01.mp4"))
        assertEquals("s_974199ef0e3c4c1d", ContentKeys.forSingle("a/b.mkv"))
        assertEquals("s_3c13cd0fa118d9fb", ContentKeys.forSingle("rec/2026-01-01 12-00-00.MP4"))
    }

    @Test
    fun `规范化_反斜杠与点斜杠前缀与大小写`() {
        assertEquals("a/b.mkv", ContentKeys.normalize(".\\A\\B.mkv"))
        assertEquals("a/b.mkv", ContentKeys.normalize("A/B.MKV"))
        assertEquals("a/b.mkv", ContentKeys.normalize("/a/b.mkv"))
        // 规范化后的键不区分写法
        assertEquals(ContentKeys.forSingle("A/B.MKV"), ContentKeys.forSingle(".\\a\\b.mkv"))
    }
}
