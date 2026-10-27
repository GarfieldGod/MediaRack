package io.github.garfieldgod.mediarack.core.thumb

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.media.MediaMetadataRetriever
import android.net.Uri
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.FileOutputStream
import java.util.concurrent.ConcurrentHashMap

/**
 * 首帧缩略图仓库（信息流连贯翻页用，与封面海报分开）：
 *
 * - **本地条目**：`MediaMetadataRetriever` 抽**第 0 帧**，最长边压到 1280 后存 JPEG
 * - **远程条目**：下载服务器 `/media/{source}/{key}/thumb.jpg`（ffmpeg 抽帧，服务端缓存）
 * - 缓存落 `filesDir/thumbs/<safe(id)>.jpg`，同 id 并发去重（synchronized gate）
 * - 失败返回 null（调用方回退封面海报），不弹错
 */
object ThumbStore {

    private val locks = ConcurrentHashMap<String, Any>()

    private val http by lazy {
        okhttp3.OkHttpClient.Builder()
            .connectTimeout(8, java.util.concurrent.TimeUnit.SECONDS)
            .readTimeout(20, java.util.concurrent.TimeUnit.SECONDS)
            .build()
    }

    fun cacheFile(context: Context, id: String): File {
        val safe = id.replace(Regex("[^A-Za-z0-9_-]"), "_")
        return File(File(context.filesDir, "thumbs"), "$safe.jpg")
    }

    /**
     * 首帧就绪返回文件绝对路径；失败返回 null。
     * [localVideoUri] 非 null 时优先本地抽帧；否则用 [remoteUrl] 下载。
     */
    suspend fun ensure(
        context: Context,
        id: String,
        localVideoUri: String?,
        remoteUrl: String?,
    ): String? = withContext(Dispatchers.IO) {
        val dest = cacheFile(context, id)
        if (dest.isFile && dest.length() > 0) return@withContext dest.absolutePath

        val gate = locks.getOrPut(id) { Any() }
        synchronized(gate) {
            // 等锁期间别人可能已经抽好。
            if (dest.isFile && dest.length() > 0) return@withContext dest.absolutePath

            dest.parentFile?.mkdirs()
            val ok = when {
                localVideoUri != null -> extractFrame(localVideoUri, dest)
                remoteUrl != null -> downloadFrame(remoteUrl, dest)
                else -> false
            }
            if (ok) dest.absolutePath else {
                dest.delete()
                null
            }
        }
    }

    /** 第 0 帧 → 最长边 ≤1280 的 JPEG。fMP4（m4s）与普通文件都能解。 */
    private fun extractFrame(videoUri: String, dest: File): Boolean = runCatching {
        val mmr = MediaMetadataRetriever()
        try {
            val uri = Uri.parse(videoUri)
            if (uri.scheme == "file") {
                mmr.setDataSource(uri.path ?: return false)
            } else {
                mmr.setDataSource(uri.toString())
            }
            val frame = mmr.getFrameAtTime(0, MediaMetadataRetriever.OPTION_CLOSEST_SYNC)
                ?: return false
            val scaled = scaleDown(frame, 1280)
            FileOutputStream(dest).use { out ->
                scaled.compress(Bitmap.CompressFormat.JPEG, 85, out)
            }
            dest.length() > 0
        } finally {
            mmr.release()
        }
    }.getOrDefault(false)

    private fun downloadFrame(url: String, dest: File): Boolean = runCatching {
        val req = okhttp3.Request.Builder().url(url).build()
        http.newCall(req).execute().use { resp ->
            if (!resp.isSuccessful) return false
            val bytes = resp.body?.bytes() ?: return false
            // 验证确实是可解码图片，防止把错误页 JSON 存成 .jpg
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size) ?: return false
            dest.writeBytes(bytes)
            true
        }
    }.getOrDefault(false)

    private fun scaleDown(src: Bitmap, maxSide: Int): Bitmap {
        val longest = maxOf(src.width, src.height)
        if (longest <= maxSide) return src
        val scale = maxSide.toFloat() / longest
        return Bitmap.createScaledBitmap(
            src,
            (src.width * scale).toInt().coerceAtLeast(1),
            (src.height * scale).toInt().coerceAtLeast(1),
            true,
        )
    }
}
