package io.github.garfieldgod.mediarack

import io.github.garfieldgod.mediarack.core.remote.ApiClient
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import okio.Buffer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

/**
 * 断点续传协议行为：
 * - 全新下载：无 Range，写满文件
 * - 续传：带 `Range: bytes=n-`，服务器 206 → 追加；Content-Range 给出完整长度
 * - 服务器忽略 Range 回 200 → 截断重下（宁重下不错乱）
 */
class ApiClientDownloadTest {

    @get:Rule
    val tmp = TemporaryFolder()

    private lateinit var server: MockWebServer
    private lateinit var client: ApiClient

    private val fullBody = "HELLOWORLD"

    @Before
    fun setUp() {
        server = MockWebServer()
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                val range = request.getHeader("Range")
                return when {
                    // 服务器支持 Range：回 206 + 剩余部分
                    range == "bytes=5-" -> MockResponse()
                        .setResponseCode(206)
                        .setHeader("Content-Range", "bytes 5-9/10")
                        .setBody(fullBody.substring(5))
                    // 服务器不认 Range：回 200 + 完整体
                    else -> MockResponse()
                        .setResponseCode(200)
                        .setBody(fullBody)
                }
            }
        }
        server.start()
        client = ApiClient(server.url("/").toString().trimEnd('/'), null)
    }

    @After
    fun tearDown() {
        server.shutdown()
    }

    private fun url() = server.url("/api/v1/media/x/k/video.m4s").toString()

    @Test
    fun `fresh download writes full file`() {
        val dest = File(tmp.root, "fresh.part")
        val progress = mutableListOf<Pair<Long, Long>>()
        client.downloadToFile(url(), dest, 0) { d, t -> progress.add(d to t) }

        assertEquals(fullBody, dest.readText())
        assertTrue(progress.isNotEmpty())
        assertEquals(10L, progress.last().second)
        assertEquals(10L, progress.last().first)
    }

    @Test
    fun `resume sends range and appends from offset`() {
        val dest = File(tmp.root, "resume.part")
        dest.writeText("HELLO")

        val progress = mutableListOf<Pair<Long, Long>>()
        client.downloadToFile(url(), dest, 5) { d, t -> progress.add(d to t) }

        assertEquals(fullBody, dest.readText())
        // Content-Range bytes 5-9/10 → 完整长度 10（不是本次响应的 5）
        assertEquals(10L, progress.last().second)
        assertEquals(10L, progress.last().first)
    }

    @Test
    fun `server ignoring range restarts from scratch`() {
        val dest = File(tmp.root, "no-range.part")
        dest.writeText("JUNKJUNK") // 8 字节脏数据

        client.downloadToFile(url(), dest, 8)

        // 200 → 截断重下，不允许出现拼接错乱
        assertEquals(fullBody, dest.readText())
    }

    @Test
    fun `http error throws with status`() {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest) =
                MockResponse().setResponseCode(500).setBody("boom")
        }
        val dest = File(tmp.root, "err.part")
        try {
            client.downloadToFile(url(), dest, 0)
            error("应当抛出")
        } catch (e: java.io.IOException) {
            assertTrue(e.message!!.contains("500"))
        }
    }
}
