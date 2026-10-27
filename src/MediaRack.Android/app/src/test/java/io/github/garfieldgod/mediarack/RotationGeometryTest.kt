package io.github.garfieldgod.mediarack

import androidx.compose.ui.geometry.Size
import io.github.garfieldgod.mediarack.ui.feed.imageLayoutSizeFor
import io.github.garfieldgod.mediarack.ui.feed.layoutSizeFor
import io.github.garfieldgod.mediarack.ui.feed.normalizeRotation
import io.github.garfieldgod.mediarack.ui.feed.rotatedSize
import io.github.garfieldgod.mediarack.ui.feed.swapsDimensions
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 视频旋转的几何规则。
 *
 * 只有四档角度（0/90/180/270），但"布局尺寸该不该换"和"换完转过去是不是正好铺满容器"
 * 这两件事在真机上很难看出错——错了的表现为"旋转后画面只剩一角/一片黑"，
 * 却看不出是公式的问题。所以核心不变量锁在这里：
 * 播放视图按 [layoutSizeFor] 摆好、再转过 [normalizeRotation] 之后，
 * 视觉尺寸必须**恒等于容器尺寸**。
 */
class RotationGeometryTest {

    private val portrait = Size(1080f, 2340f)
    private val landscape = Size(2340f, 1080f)

    @Test
    fun `归一化到四档`() {
        assertEquals(0, normalizeRotation(0))
        assertEquals(90, normalizeRotation(90))
        assertEquals(180, normalizeRotation(180))
        assertEquals(270, normalizeRotation(270))
        // 超 360 / 负数都要收进来（连点 4 次回到 0 的前提）
        assertEquals(90, normalizeRotation(450))
        assertEquals(0, normalizeRotation(360))
        assertEquals(270, normalizeRotation(-90))
        assertEquals(0, normalizeRotation(-360))
        // 非 90 的倍数向下取整到 90 的倍数（视频没有"歪 37 度"这种档位）
        assertEquals(90, normalizeRotation(100))
        assertEquals(0, normalizeRotation(89))
    }

    @Test
    fun `只有 90 与 270 交换宽高`() {
        assertFalse(swapsDimensions(0))
        assertFalse(swapsDimensions(180))
        assertTrue(swapsDimensions(90))
        assertTrue(swapsDimensions(270))
        assertTrue(swapsDimensions(450)) // 归一化后是 90
        assertFalse(swapsDimensions(-180)) // 归一化后是 180
    }

    @Test
    fun `布局尺寸交换后转过 90 正好是容器`() {
        // 核心不变量：layoutSizeFor + rotation 的视觉结果 == 容器
        listOf(0, 90, 180, 270).forEach { angle ->
            val layout = layoutSizeFor(portrait, angle)
            assertEquals(portrait, rotatedSize(layout, angle))
        }
    }

    @Test
    fun `竖屏容器与横屏容器都成立`() {
        listOf(portrait, landscape).forEach { container ->
            listOf(0, 90, 180, 270).forEach { angle ->
                assertEquals(container, rotatedSize(layoutSizeFor(container, angle), angle))
            }
        }
    }

    @Test
    fun `90 度时布局尺寸是容器的宽高互换`() {
        assertEquals(Size(2340f, 1080f), layoutSizeFor(portrait, 90))
        assertEquals(Size(2340f, 1080f), layoutSizeFor(portrait, 270))
        assertEquals(portrait, layoutSizeFor(portrait, 0))
        assertEquals(portrait, layoutSizeFor(portrait, 180))
    }

    // ── 缩略图/图片：先算"转完的画面"，再反推旋转前尺寸 ──────────────

    @Test
    fun `旋转后要铺满竖屏整页而不是缩成一小块`() {
        // 真机现象：1920x1080 转 90 后本应是 1080x1920 的竖画面，
        // 旧写法（换框 + Fit 未旋转位图）只得到 608x1080，约 56%。
        val layout = imageLayoutSizeFor(portrait, 1920f, 1080f, 90)
        assertEquals(Size(1920f, 1080f), layout)
        assertEquals(Size(1080f, 1920f), rotatedSize(layout, 90))
    }

    @Test
    fun `横屏格子里按高度贴边、两侧留黑`() {
        // 16:10 的格子里放"转 90 的横屏视频"（视觉上是 9:16），只能按高度贴边
        val layout = imageLayoutSizeFor(Size(320f, 200f), 1920f, 1080f, 90)
        val shown = rotatedSize(layout, 90)
        assertEquals(200f, shown.height, 0.01f)
        assertEquals(112.5f, shown.width, 0.01f)
    }

    @Test
    fun `Cover 模式转完铺满容器`() {
        val layout = imageLayoutSizeFor(portrait, 1920f, 1080f, 90, cover = true)
        val shown = rotatedSize(layout, 90)
        assertTrue(shown.width >= portrait.width - 0.01f)
        assertTrue(shown.height >= portrait.height - 0.01f)
    }

    @Test
    fun `不旋转时就是普通 Fit`() {
        assertEquals(Size(1080f, 607.5f), imageLayoutSizeFor(portrait, 1920f, 1080f, 0))
        // 180 度不换框，几何与 0 度一致
        assertEquals(Size(1080f, 607.5f), imageLayoutSizeFor(portrait, 1920f, 1080f, 180))
    }

    @Test
    fun `转完之后永远不超出容器`() {
        listOf(0, 90, 180, 270).forEach { angle ->
            val shown = rotatedSize(imageLayoutSizeFor(portrait, 1920f, 1080f, angle), angle)
            assertTrue("角度 $angle 超宽", shown.width <= portrait.width + 0.01f)
            assertTrue("角度 $angle 超高", shown.height <= portrait.height + 0.01f)
        }
    }
}
