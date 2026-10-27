package io.github.garfieldgod.mediarack

import io.github.garfieldgod.mediarack.core.download.DownloadLayouts
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

/**
 * 下载布局：key 解析必须与 PC `MediaKey.TryParseDual` 逐行为一致；
 * entry.json 必须带 PC DualScanner 的硬性字段（顶层 avid、page_data.cid、type_tag），
 * 保证手机下载目录拷到 PC 可直接当 dual 源扫描。
 */
class DownloadLayoutsTest {

    @get:Rule
    val tmp = TemporaryFolder()

    @Test
    fun `parseDualKey mirrors PC TryParseDual`() {
        assertEquals(
            "114053930288640" to "28543681073",
            DownloadLayouts.parseDualKey("114053930288640_c28543681073"),
        )
        // 命中最后一个 "_c"，但前段 "1_c2" 不是纯数字 → 拒绝（与 PC LastIndexOf 行为一致）
        assertNull(DownloadLayouts.parseDualKey("1_c2_c23"))
        // 非 dual 形态一律拒绝
        assertNull(DownloadLayouts.parseDualKey("s_0123456789abcdef"))
        assertNull(DownloadLayouts.parseDualKey("_c123"))
        assertNull(DownloadLayouts.parseDualKey("abc_c123"))
        assertNull(DownloadLayouts.parseDualKey("114_c28x"))
        assertNull(DownloadLayouts.parseDualKey("114_c"))
        assertNull(DownloadLayouts.parseDualKey("plain"))
        assertNull(DownloadLayouts.parseDualKey(""))
    }

    @Test
    fun `entry json round trip keeps PC required fields`() {
        val dir = tmp.newFolder("c_28543681073")
        DownloadLayouts.writeEntryJson(
            dir,
            DownloadLayouts.EntrySpec(
                title = "测试视频", avid = "114053930288640", cid = "28543681073",
                typeTag = "30280", durationMs = 65_000, totalBytes = 1_234_567,
                hasAudio = true, upName = "某UP主", width = 2160, height = 3840,
            ),
        )

        val json = File(dir, "entry.json").readText()
        assertTrue(json.contains("\"avid\":114053930288640"))
        assertTrue(json.contains("\"type_tag\":\"30280\""))
        assertTrue(json.contains("\"page_data\""))
        assertTrue(json.contains("\"cid\":28543681073"))

        val back = DownloadLayouts.readEntryJson(dir)!!
        assertEquals("测试视频", back.title)
        assertEquals(114053930288640L, back.avid)
        assertEquals("30280", back.type_tag)
        assertEquals(65_000, back.total_time_milli)
        assertEquals(1_234_567L, back.total_bytes)
        assertEquals(true, back.has_dash_audio)
        assertEquals(true, back.is_completed)
        assertEquals("某UP主", back.owner_name)
        assertEquals(28543681073L, back.page_data?.cid)
        assertEquals(2160, back.page_data?.width)
        assertEquals(3840, back.page_data?.height)
    }

    @Test
    fun `readEntryJson tolerates missing file and unknown keys`() {
        val empty = tmp.newFolder("no_entry")
        assertNull(DownloadLayouts.readEntryJson(empty))

        // B 站缓存 entry.json 有大量额外字段：忽略后仍能解析
        val dir = tmp.newFolder("c_1")
        File(dir, "entry.json").writeText(
            """
            {"media_type":2,"has_dashaudio":true,"title":"x","avid":1,
             "page_data":{"cid":2,"from":0,"width":100,"height":50},
             "unknown_top": {"nested": [1,2,3]}}
            """.trimIndent()
        )
        val e = DownloadLayouts.readEntryJson(dir)!!
        assertEquals(1L, e.avid)
        assertEquals(2L, e.page_data?.cid)
        assertEquals(100, e.page_data?.width)
    }

    @Test
    fun `resolveTagDir prefers type_tag then falls back to first dir with m4s`() {
        val item = tmp.newFolder("c_1")
        // 没有 tag 目录也没有 m4s
        assertNull(DownloadLayouts.resolveTagDir(item, "30280"))

        // type_tag 命中
        val tag = tmp.newFolder("c_1/30280")
        assertEquals(tag, DownloadLayouts.resolveTagDir(item, "30280"))

        // type_tag 缺失 → 第一个含 video.m4s 的子目录；type_tag 指向不存在的目录同样退化
        val fallback = tmp.newFolder("c_1/whatever")
        File(fallback, "video.m4s").writeText("x")
        assertEquals(fallback, DownloadLayouts.resolveTagDir(item, "99999"))
        assertEquals(fallback, DownloadLayouts.resolveTagDir(item, null))
    }
}
