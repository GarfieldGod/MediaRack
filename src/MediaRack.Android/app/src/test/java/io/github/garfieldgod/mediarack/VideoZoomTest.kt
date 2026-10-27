package io.github.garfieldgod.mediarack

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import io.github.garfieldgod.mediarack.ui.feed.VideoZoom
import io.github.garfieldgod.mediarack.ui.feed.ZOOM_MAX
import io.github.garfieldgod.mediarack.ui.feed.ZOOM_MIN
import io.github.garfieldgod.mediarack.ui.feed.clampPan
import io.github.garfieldgod.mediarack.ui.feed.clampZoomScale
import io.github.garfieldgod.mediarack.ui.feed.zoomAroundCentroid
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 双指缩放的几何规则。
 *
 * 这三个函数决定了"捏合时画面往哪跑"——算错了在真机上只是"有点飘"，
 * 很难看出是公式问题，所以全部抽成纯函数锁在这里：
 * - [zoomAroundCentroid]：双指中点下的内容必须一动不动（否则缩放会像在甩画面）
 * - [clampPan]：放大后不能把画面整块拖出屏幕；缩小（scale<1）时不能抛异常
 * - [clampZoomScale]：上下限
 */
class VideoZoomTest {

    private val window = Size(1000f, 2000f)
    private val center = Offset(500f, 1000f)

    @Test
    fun `缩放上下限`() {
        assertEquals(ZOOM_MIN, clampZoomScale(0.01f), 0.0001f)
        assertEquals(ZOOM_MAX, clampZoomScale(99f), 0.0001f)
        assertEquals(2.5f, clampZoomScale(2.5f), 0.0001f)
    }

    @Test
    fun `默认状态与缩放状态`() {
        assertTrue(VideoZoom().isDefault)
        assertTrue(VideoZoom(1f, Offset.Zero).isDefault)
        assertFalse(VideoZoom(1.5f).isDefault)
        assertFalse(VideoZoom(1f, Offset(3f, 0f)).isDefault)
    }

    @Test
    fun `负零偏移也算默认状态`() {
        // Offset 把两个 Float 打包进 Long，-0.0f 与 0.0f 在 Float 比较下相等、
        // 打包后却不等。平移夹到边界就会产出 -0.0f，不归一的话「恢复原比例」会常显。
        assertTrue(VideoZoom(1f, Offset(-0.0f, 0.0f)).isDefault)
        assertTrue(VideoZoom(1f, clampPan(Offset(500f, -800f), 1f, window)).isDefault)
    }

    @Test
    fun `以中点为锚放大_锚点处内容不动`() {
        val centroid = Offset(600f, 1200f)
        val s0 = 1f
        val s1 = 2f
        // 先把画面挪开一点，再从 1x 放大到 2x
        val t0 = Offset(-40f, 25f)
        val t1 = zoomAroundCentroid(s0, t0, centroid, center, s1)

        // 变换：p' = center + T + S * (p - center)
        fun project(p: Offset, t: Offset, s: Float) = center + t + (p - center) * s

        // 放大前正好落在双指中点上的那块内容（绝对坐标）
        val u = center + (centroid - center - t0) / s0
        // 放大后必须仍落在双指中点（内容不跟着手指甩出去）
        val after = project(u, t1, s1)
        assertEquals(centroid.x, after.x, 0.001f)
        assertEquals(centroid.y, after.y, 0.001f)
    }

    @Test
    fun `原点锚放大_偏移按比例反推`() {
        // 1x、无偏移、锚点就是窗口中心 → 放大后仍无偏移
        assertEquals(Offset.Zero, zoomAroundCentroid(1f, Offset.Zero, center, center, 2f))
        // 锚点在中心右侧 100px → 放大 2 倍要往左推 100px 才能让那块内容不动
        val t = zoomAroundCentroid(1f, Offset.Zero, center + Offset(100f, 0f), center, 2f)
        assertEquals(-100f, t.x, 0.001f)
        assertEquals(0f, t.y, 0.001f)
    }

    @Test
    fun `未放大时不允许平移`() {
        val clamped = clampPan(Offset(500f, -800f), 1f, window)
        assertEquals(0f, clamped.x, 0.0001f)
        assertEquals(0f, clamped.y, 0.0001f)
    }

    @Test
    fun `放大后平移被限制在露出的范围内`() {
        val clamped = clampPan(Offset(9_999f, -9_999f), 2f, window)
        assertEquals(500f, clamped.x, 0.001f) // (2-1) * 1000 / 2
        assertEquals(-1000f, clamped.y, 0.001f) // (2-1) * 2000 / 2
    }

    @Test
    fun `缩小到一以下不会抛异常`() {
        // 回归：maxX 曾为负 → coerceIn(min>max) 直接抛 IllegalArgumentException，一捏合就崩
        val clamped = clampPan(Offset(9_999f, 9_999f), 0.5f, window)
        assertEquals(250f, clamped.x, 0.001f) // (1-0.5) * 1000 / 2
        assertEquals(500f, clamped.y, 0.001f)
    }
}
