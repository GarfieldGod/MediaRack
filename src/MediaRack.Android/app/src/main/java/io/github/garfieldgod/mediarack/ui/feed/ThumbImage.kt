package io.github.garfieldgod.mediarack.ui.feed

import android.graphics.BitmapFactory
import android.util.LruCache
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.layout
import androidx.compose.ui.unit.Constraints
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.util.concurrent.TimeUnit
import kotlin.math.roundToInt

/**
 * 首帧缩略图（信息流连贯翻页）+ 封面兜底。信息流与「我的」页共用。
 */

/** 按字节限额的位图缓存：720p 一张 ≈ 3.7MB，64MB ≈ 17 张。 */
internal val thumbCache = object : LruCache<String, android.graphics.Bitmap>(64 * 1024 * 1024) {
    override fun sizeOf(key: String, value: android.graphics.Bitmap): Int = value.byteCount
}

private val posterHttp by lazy {
    okhttp3.OkHttpClient.Builder()
        .connectTimeout(5, TimeUnit.SECONDS)
        .readTimeout(10, TimeUnit.SECONDS)
        .build()
}

/** 封面海报兜底（首帧拿不到时）：本地海报文件 / 远程海报 URL。 */
internal suspend fun loadPoster(item: FeedItem): android.graphics.Bitmap? = withContext(Dispatchers.IO) {
    runCatching {
        when {
            item.posterFile != null -> BitmapFactory.decodeFile(item.posterFile)
            item.posterUrl != null -> {
                val req = okhttp3.Request.Builder().url(item.posterUrl).build()
                posterHttp.newCall(req).execute().use { resp ->
                    if (!resp.isSuccessful) return@use null
                    resp.body?.bytes()?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }
                }
            }
            else -> null
        }
    }.getOrNull()
}

/**
 * 缩略图：优先首帧（vm.thumbFile → 本地抽帧 / 服务器 thumb 端点），失败回退封面海报。
 *
 * - [fitAlign] = true（信息流整页）：跟播放器当前的 Fit/Cover 模式同一几何——
 *   Fit 时黑底居中按宽高比 letterbox（与 RESIZE_MODE_FIT 画面重合，滑动时"画面就在那里"），
 *   Cover 时裁切铺满整页（与 RESIZE_MODE_ZOOM 一致）
 * - [fitAlign] = false（列表小格）：裁切填满格子
 */
@Composable
internal fun ThumbImage(
    item: FeedItem,
    vm: FeedViewModel,
    modifier: Modifier = Modifier,
    fitAlign: Boolean = false,
    /** 播放器当前的 Cover 状态：true 时整页缩略图裁切铺满，与 ZOOM 模式画面重合。 */
    cover: Boolean = false,
) {
    val bitmap by produceState<android.graphics.Bitmap?>(initialValue = thumbCache.get(item.id), key1 = item.id) {
        if (value == null) {
            val path = runCatching { vm.thumbFile(item) }.getOrNull()
            val bmp = path?.let { BitmapFactory.decodeFile(it) } ?: loadPoster(item)
            if (bmp != null) thumbCache.put(item.id, bmp)
            value = bmp
        }
    }
    val bmp = bitmap
    // 缩略图（首帧/海报）是按容器原样抽的，不含用户旋转覆盖（media_meta.rotation /
    // items.rotation 是"额外再转"）。在显示层补上同一角度，旋转与播放画面一致；
    // 位图缓存按条目 id 存原样位图，旋转放渲染层，转动时缓存不用失效。
    val rot = item.rotation.toFloat()
    when {
        bmp != null && fitAlign && !cover -> Box(modifier.background(Color.Black), contentAlignment = Alignment.Center) {
            // 与 PlayerView(RESIZE_MODE_FIT) 同一几何：同比例黑底居中。
            // 关键在顺序——先按"转过之后的画面"算 Fit 尺寸，再反推旋转前该摆多大
            // （见 imageLayoutSizeFor）。反过来做（换框 + Fit 未旋转位图）会让画面
            // 缩成中间一小块，这正是之前旋转过的视频看起来"变小"的原因。
            Image(
                bitmap = bmp.asImageBitmap(),
                contentDescription = null,
                contentScale = ContentScale.Fit,
                modifier = Modifier
                    .rotatedFrame(item.rotation, bmp.width, bmp.height, item.width, item.height, cover = false)
                    .rotate(rot),
            )
        }
        bmp != null -> Image(
            bitmap = bmp.asImageBitmap(),
            contentDescription = null,
            contentScale = ContentScale.Crop,
            // Cover 分支按 [rotatedFrame] 量出的尺寸**大于容器**（那是"铺满后再裁"的
            // 正确画法），多出来的部分靠这层 clipToBounds 收掉。ContentScale 只算缩放、
            // 不裁剪，Image 自己也不裁——少了它，竖屏/旋转过的缩略图会画到卡片外面。
            // 圆角仍由调用方容器的 clip(shape) 负责（这里只能裁方框，不覆盖圆角）。
            modifier = modifier
                .clipToBounds()
                .rotatedFrame(item.rotation, bmp.width, bmp.height, item.width, item.height, cover = true)
                .rotate(rot),
        )
        else -> Box(modifier.background(Color(0xFF101216)), contentAlignment = Alignment.Center) {
            Icon(
                Icons.Default.PlayArrow,
                contentDescription = null,
                tint = Color(0xFF3A3F4C),
                modifier = Modifier.size(52.dp),
            )
        }
    }
}

/**
 * 按旋转角摆好画面：节点占满容器，画面按 [imageLayoutSizeFor] 算出的尺寸居中放置，
 * 随后由调用方的 `rotate()` 以**容器中心**为轴旋转——因为画面本来就居中，
 * 旋转中心正好是画面中心，转完与播放器（RotatingPlayerFrame 换 measure 约束）几何一致。
 */
private fun Modifier.rotatedFrame(
    rotation: Int,
    bitmapWidth: Int,
    bitmapHeight: Int,
    videoWidth: Int,
    videoHeight: Int,
    cover: Boolean,
): Modifier = layout { measurable, constraints ->
    val cw = constraints.maxWidth
    val ch = constraints.maxHeight

    // 无界约束（极端情况）下退回"按内容原样量"，别拿 Infinity 去做除法
    if (cw <= 0 || ch <= 0 || cw == Constraints.Infinity || ch == Constraints.Infinity) {
        val placeable = measurable.measure(constraints)
        return@layout layout(placeable.width, placeable.height) { placeable.place(0, 0) }
    }

    // 优先用视频宽高（与播放器同一套几何）；探不到时退回位图尺寸
    val useVideo = videoWidth > 0 && videoHeight > 0
    val size = imageLayoutSizeFor(
        container = Size(cw.toFloat(), ch.toFloat()),
        contentWidth = if (useVideo) videoWidth.toFloat() else bitmapWidth.toFloat(),
        contentHeight = if (useVideo) videoHeight.toFloat() else bitmapHeight.toFloat(),
        rotation = rotation,
        cover = cover,
    )
    val w = size.width.roundToInt().coerceAtLeast(1)
    val h = size.height.roundToInt().coerceAtLeast(1)
    val placeable = measurable.measure(Constraints.fixed(w, h))
    layout(cw, ch) { placeable.place((cw - w) / 2, (ch - h) / 2) }
}
