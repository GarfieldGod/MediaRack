package io.github.garfieldgod.mediarack.core.scan

import android.content.Context
import android.media.MediaMetadataRetriever
import android.graphics.Bitmap
import io.github.garfieldgod.mediarack.core.ContentKeys
import io.github.garfieldgod.mediarack.core.db.AppDatabase
import io.github.garfieldgod.mediarack.core.db.ItemEntity
import io.github.garfieldgod.mediarack.core.db.SourceEntity
import io.github.garfieldgod.mediarack.core.download.DownloadLayouts
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File

/**
 * 本地源扫描器：single 布局语义的 Android 版，对齐 PC 端既有决策：
 * - 递归扫视频文件；**子目录即合集**（条目的 collection_dir = 父目录相对路径，null = 根）
 * - key = `s_<sha1(规范化相对路径)[:16]>`，与 PC 跨端一致
 * - 0 字节 → corrupt（single 源无完整性契约，不判其他坏态）
 * - 海报：中点帧、宽 ≤560 不放大、**是否已有以磁盘为准**（删目录后重扫自愈）
 * - 元数据 / 海报失败不阻塞入库
 * - 重扫后删除磁盘上已消失的条目
 *
 * 同时兼收 dual 条目：`<avid>/c_<cid>/entry.json` 目录形态（下载目录就是这种布局），
 * key/元数据来自 entry.json（镜像 PC DualScanner），海报已在下载时写好、重扫直接命中。
 */
class LocalSourceScanner(context: Context, private val db: AppDatabase) {

    companion object {
        /** 允许的子目录名/源名的非法字符（Windows 风格，推文件时少见坑）。 */
        private val VIDEO_EXTS = setOf("mp4", "mkv", "webm", "mov", "m4v", "avi", "ts", "flv", "wmv", "3gp", "mpg", "mpeg")
        const val POSTER_MAX_WIDTH = 560
    }

    private val appContext = context.applicationContext

    /** posters/<sourceId>/<key>.jpg，对齐 PC 的 meta/poster/<source_id>/<key>.jpg 结构。 */
    fun posterDir(sourceId: Long): File = File(appContext.filesDir, "posters/$sourceId")

    /**
     * 全量扫描一个源。返回入库条目数。
     * 期间可中断（协程取消会自然中止遍历）。
     */
    suspend fun scan(source: SourceEntity): Int = withContext(Dispatchers.IO) {
        val root = File(source.rootPath)
        require(root.isDirectory) { "源目录不存在：${source.rootPath}" }

        val files = mutableListOf<File>()
        val dualDirs = mutableListOf<DualDir>()
        walk(root, files, dualDirs)

        val items = ArrayList<ItemEntity>(files.size + dualDirs.size)
        val posterDir = posterDir(source.id)

        // rotation 是用户态（不是探测结果）：@Upsert 会整行替换，所以扫之前先取旧值带上，
        // 否则每次重扫都会把用户设的旋转角抹回 0。
        val prevRotation = db.itemDao().listAll(source.id).associate { it.key to it.rotation }

        for (d in dualDirs) {
            items.add(scanDual(root, source.id, d, posterDir, prevRotation))
        }

        for (file in files) {
            val relPath = root.toPath().relativize(file.toPath()).toString()
            val key = ContentKeys.forSingle(relPath)
            val collectionDir = file.parent?.let { p ->
                root.toPath().relativize(File(p).toPath()).toString().takeIf { it.isNotEmpty() }
            }
            val title = file.nameWithoutExtension

            if (file.length() == 0L) {
                items.add(
                    ItemEntity(
                        sourceId = source.id, key = key, title = title,
                        relPath = relPath, absPath = file.absolutePath,
                        size = 0, durationMs = 0, width = 0, height = 0,
                        collectionDir = collectionDir, hasPoster = false,
                        status = ItemEntity.STATUS_CORRUPT,
                        rotation = prevRotation[key] ?: 0,
                    )
                )
                continue
            }

            val meta = readMeta(file)
            val hasPoster = ensurePoster(file, meta.durationMs, posterDir, key)

            items.add(
                ItemEntity(
                    sourceId = source.id, key = key, title = title,
                    relPath = relPath, absPath = file.absolutePath,
                    size = file.length(), durationMs = meta.durationMs,
                    width = meta.width, height = meta.height,
                    collectionDir = collectionDir, hasPoster = hasPoster,
                    status = ItemEntity.STATUS_OK,
                    rotation = prevRotation[key] ?: 0,
                )
            )
        }

        db.itemDao().upsertAll(items)
        db.itemDao().deleteMissing(source.id, items.map { it.key })
        items.size
    }

    /** 删除源时连同它的海报目录清掉（条目由外键级联在 ViewModel 里显式调 deleteBySource）。 */
    fun deletePosters(sourceId: Long) {
        posterDir(sourceId).deleteRecursively()
    }

    /** dual 条目目录：`<avid>/c_<cid>/`，含 entry.json（下载目录布局，PC DualScanner 兼容）。 */
    private class DualDir(val avid: String, val dir: File)

    private fun walk(dir: File, out: MutableList<File>, dual: MutableList<DualDir>) {
        val children = dir.listFiles() ?: return
        for (child in children) {
            if (child.name.startsWith(".")) continue
            when {
                child.isDirectory -> {
                    if (child.name.startsWith("c_") && File(child, "entry.json").isFile) {
                        dual.add(DualDir(dir.name, child))
                    } else {
                        walk(child, out, dual)
                    }
                }
                child.isFile && child.extension.lowercase() in VIDEO_EXTS -> out.add(child)
            }
        }
    }

    /**
     * dual 条目：key 与元数据以 entry.json 为准（镜像 PC：avid + page_data.cid），
     * 拿不到时退化到目录名。absPath 指向 video.m4s，播放侧用同级 audio.m4s 合成双流。
     * 不做完整性校验（Android 侧无 index.json 契约），video.m4s 缺失才判 corrupt。
     */
    private fun scanDual(
        root: File,
        sourceId: Long,
        d: DualDir,
        posterDir: File,
        prevRotation: Map<String, Int> = emptyMap(),
    ): ItemEntity {
        val entry = DownloadLayouts.readEntryJson(d.dir)
        val avid = entry?.avid?.toString() ?: d.avid
        val cid = entry?.page_data?.cid?.toString() ?: d.dir.name.removePrefix("c_")
        val key = "${avid}_c$cid"
        val relPath = root.toPath().relativize(d.dir.toPath()).toString()

        val tagDir = DownloadLayouts.resolveTagDir(d.dir, entry?.type_tag)
        val video = tagDir?.let { File(it, "video.m4s") }?.takeIf { it.isFile }
        val audio = tagDir?.let { File(it, "audio.m4s") }?.takeIf { it.isFile }

        if (video == null) {
            return ItemEntity(
                sourceId = sourceId, key = key,
                title = entry?.title ?: key,
                relPath = relPath, absPath = d.dir.absolutePath,
                size = 0, durationMs = 0, width = 0, height = 0,
                collectionDir = d.avid.takeIf { it.isNotEmpty() },
                hasPoster = false, status = ItemEntity.STATUS_CORRUPT,
                rotation = prevRotation[key] ?: 0,
            )
        }

        val size = entry?.total_bytes ?: (video.length() + (audio?.length() ?: 0L))
        val hasPoster = ensurePoster(video, (entry?.total_time_milli?.toLong() ?: 0L), posterDir, key)

        return ItemEntity(
            sourceId = sourceId, key = key,
            title = entry?.title ?: key,
            relPath = relPath, absPath = video.absolutePath,
            size = size,
            durationMs = entry?.total_time_milli?.toLong() ?: 0L,
            width = entry?.page_data?.width ?: 0,
            height = entry?.page_data?.height ?: 0,
            collectionDir = d.avid.takeIf { it.isNotEmpty() },
            hasPoster = hasPoster,
            status = ItemEntity.STATUS_OK,
            rotation = prevRotation[key] ?: 0,
        )
    }

    private class Meta(val durationMs: Long, val width: Int, val height: Int)

    private fun readMeta(file: File): Meta {
        val retriever = MediaMetadataRetriever()
        return try {
            retriever.setDataSource(file.absolutePath)
            val durationMs = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLongOrNull() ?: 0L
            var width = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_WIDTH)?.toIntOrNull() ?: 0
            var height = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_HEIGHT)?.toIntOrNull() ?: 0
            val rotation = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_ROTATION)?.toIntOrNull() ?: 0
            if (rotation == 90 || rotation == 270) {
                val t = width; width = height; height = t
            }
            Meta(durationMs, width, height)
        } catch (_: Exception) {
            Meta(0, 0, 0)
        } finally {
            runCatching { retriever.release() }
        }
    }

    /** 已有则跳过（磁盘为准）；失败返回 false 不阻塞。 */
    private fun ensurePoster(file: File, durationMs: Long, posterDir: File, key: String): Boolean {
        val posterFile = File(posterDir, "$key.jpg")
        if (posterFile.exists() && posterFile.length() > 0) return true
        return try {
            val retriever = MediaMetadataRetriever()
            try {
                retriever.setDataSource(file.absolutePath)
                val atUs = if (durationMs > 0) durationMs * 500 else 0L
                val frame = retriever.getFrameAtTime(atUs, MediaMetadataRetriever.OPTION_CLOSEST_SYNC) ?: return false
                val scaled = scaleToFit(frame)
                posterDir.mkdirs()
                posterFile.outputStream().use { out ->
                    scaled.compress(Bitmap.CompressFormat.JPEG, 85, out)
                }
                true
            } finally {
                runCatching { retriever.release() }
            }
        } catch (_: Exception) {
            false
        }
    }

    /** 宽 > 560 才缩，竖屏等比不裁（与 PC 端一致）。 */
    private fun scaleToFit(bitmap: Bitmap): Bitmap {
        if (bitmap.width <= POSTER_MAX_WIDTH) return bitmap
        val scale = POSTER_MAX_WIDTH.toFloat() / bitmap.width
        val w = POSTER_MAX_WIDTH
        val h = (bitmap.height * scale).toInt().coerceAtLeast(1)
        return Bitmap.createScaledBitmap(bitmap, w, h, true)
    }
}
