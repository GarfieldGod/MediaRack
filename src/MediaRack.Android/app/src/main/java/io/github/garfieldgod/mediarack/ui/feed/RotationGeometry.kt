package io.github.garfieldgod.mediarack.ui.feed

import androidx.compose.ui.geometry.Size

/**
 * 旋转几何（纯函数，可单测）。视频只有 0/90/180/270 四档——
 * 任意角度对视频没有意义（画面歪着没法看），也跟 `media_meta.rotation` 的约束一致。
 */

/** 归一化到 0/90/180/270，负数与超 360 都收进来。 */
internal fun normalizeRotation(deg: Int): Int = (((deg % 360) + 360) % 360) / 90 * 90

/** 90/270 时布局尺寸必须宽高互换，否则 `AspectRatioFrameLayout` 会按错的宽高算 FIT/Cover。 */
internal fun swapsDimensions(rotation: Int): Boolean = normalizeRotation(rotation) % 180 != 0

/**
 * 播放视图的布局尺寸：按旋转角"反向"摆好，转过之后视觉上正好等于容器。
 * 这是整个方案的核心不变量（见 [rotatedSize]）。
 */
internal fun layoutSizeFor(container: Size, rotation: Int): Size =
    if (swapsDimensions(rotation)) Size(container.height, container.width) else container

/** 一个尺寸转过 [rotation] 之后在父坐标系里占的尺寸。 */
internal fun rotatedSize(size: Size, rotation: Int): Size =
    if (swapsDimensions(rotation)) Size(size.height, size.width) else size

/**
 * 图片/缩略图在容器里的**旋转前**布局尺寸。
 *
 * 顺序不能反：先把"转过之后的画面"按 Fit / Cover 算出来（宽高比要先按旋转角互换），
 * 再反推旋转前的 layout 尺寸。如果反过来——先给框换宽高比、再让 `ContentScale.Fit`
 * 按**未旋转**的位图去适配这个框——图会先被 contain 成一条窄边，转完只剩中间一小块。
 * 播放视图（见 [layoutSizeFor]）走的是同一条不变量。
 *
 * @param contentWidth/contentHeight 画面本身的像素尺寸（优先视频宽高，拿不到时用位图宽高）。
 */
internal fun imageLayoutSizeFor(
    container: Size,
    contentWidth: Float,
    contentHeight: Float,
    rotation: Int,
    cover: Boolean = false,
): Size {
    if (contentWidth <= 0f || contentHeight <= 0f) return container
    val swap = swapsDimensions(rotation)
    // 转过之后的画面宽高：90/270 互换
    val shownWidth = if (swap) contentHeight else contentWidth
    val shownHeight = if (swap) contentWidth else contentHeight
    val scale = if (cover) {
        maxOf(container.width / shownWidth, container.height / shownHeight)
    } else {
        minOf(container.width / shownWidth, container.height / shownHeight)
    }
    // 先得到"转过之后"应占的尺寸，再反推旋转前该摆多大
    return layoutSizeFor(Size(shownWidth * scale, shownHeight * scale), rotation)
}
