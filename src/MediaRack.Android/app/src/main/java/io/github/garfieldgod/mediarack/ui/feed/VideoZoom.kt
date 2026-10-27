package io.github.garfieldgod.mediarack.ui.feed

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size

/**
 * 画面缩放状态（双指缩放 + 放大后平移）。
 *
 * - [scale]：画面放大倍数，1f = 原始大小
 * - [offset]：平移偏移（像素），放大后单指拖动用来挪动画面
 *
 * 缩放作用在整个播放器视图上（PlayerView 用 TextureView 承载，
 * SurfaceView 的内容在系统合成器里是独立图层，父级变换对它无效）。
 */
data class VideoZoom(val scale: Float = 1f, val offset: Offset = Offset.Zero) {
    /**
     * 是否处于默认状态（未缩放、未平移）。
     *
     * **不能写 `offset == Offset.Zero`**：Offset 是把两个 Float 打包进一个 Long 的
     * inline class，逐位比较。-0.0f 与 0.0f 在 Float 世界里相等（`==` 为 true），
     * 打包后却是两个不同的 Long —— 平移被夹到边界时会产出 -0.0f，
     * 于是"没缩放"被判成"缩放中"，「恢复原比例」按钮凭空常显。
     */
    val isDefault: Boolean
        get() = scale == 1f && offset.x == 0f && offset.y == 0f
}

/**
 * 缩放下限：允许缩到原比例的一半（双指捏合"缩小"要真的有反馈，
 * 停在 1f 会让人以为捏合没生效）。再小就只是一个屏幕中央的小方块了。
 */
internal const val ZOOM_MIN = 0.5f

/** 缩放上限：4x 足够看清字幕/细节，再大只是糊。 */
internal const val ZOOM_MAX = 4f

internal fun clampZoomScale(scale: Float): Float = scale.coerceIn(ZOOM_MIN, ZOOM_MAX)

/**
 * 以 [centroid]（双指中点）为锚点从 [scale0] 缩放到 [scale1]，求新的平移偏移。
 *
 * Compose 的 graphicsLayer 变换等价于 `p' = origin + T + S * (p - origin)`，
 * 其中 origin 是视图中心（TransformOrigin 默认 0.5/0.5），T 是 translation。
 * 要让锚点处的画面内容保持不动（缩放时内容不跟着手指跑），解出新的 T：
 *
 *   T1 = (c - o) - (S1/S0) * ((c - o) - T0)
 *
 * 抽成纯函数是为了能单测——缩放"往哪跑"这种细节错了很难在真机上看出来。
 */
internal fun zoomAroundCentroid(
    scale0: Float,
    offset0: Offset,
    centroid: Offset,
    center: Offset,
    scale1: Float,
): Offset {
    val s0 = if (scale0 <= 0f) 1f else scale0
    val ratio = scale1 / s0
    val d = centroid - center
    return d - d * ratio + offset0 * ratio
}

/**
 * 平移边界：缩放 s 倍时，画面比窗口大出 (s-1) 部分，
 * 平移量最多允许把这部分露出来再多一点，避免把画面整个拖出屏幕。
 *
 * 取绝对值是因为 s < 1（捏合缩小）时 max 会变成负数——`coerceIn(min>max)`
 * 会直接抛 IllegalArgumentException，表现为一捏合就崩。
 */
internal fun clampPan(offset: Offset, scale: Float, size: Size): Offset {
    val maxX = kotlin.math.abs(scale - 1f) * size.width / 2f
    val maxY = kotlin.math.abs(scale - 1f) * size.height / 2f
    // 归零负零：coerceIn(-0f, 0f) 会返回 -0f（Float 比较下等于 0，但打包进 Offset 后
    // 与 Offset.Zero 不等）。见 VideoZoom.isDefault 的注释。
    fun norm(v: Float) = if (v == 0f) 0f else v
    return Offset(
        norm(offset.x.coerceIn(-maxX, maxX)),
        norm(offset.y.coerceIn(-maxY, maxY)),
    )
}
