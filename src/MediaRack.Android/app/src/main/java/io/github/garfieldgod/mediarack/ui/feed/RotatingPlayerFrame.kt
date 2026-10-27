package io.github.garfieldgod.mediarack.ui.feed

import android.content.Context
import android.view.View
import android.widget.FrameLayout
import androidx.media3.ui.PlayerView
import io.github.garfieldgod.mediarack.R

/**
 * 播放视图的旋转容器。**唯一职责：把 90/270 的布局尺寸换过来，再绕中心转。**
 *
 * 为什么不能只给 PlayerView 设 `rotation`：`AspectRatioFrameLayout` 是按 View 自己的
 * width/height 算 FIT/Cover 的。尺寸不换，90° 之后内容仍按原来的（竖着的）空间排布，
 * 转过去就被裁掉大半。交换尺寸后，(W,H) 的容器里放一个 (H,W) 的播放视图，
 * 转 90° 恰好落回容器矩形，FIT/Cover 也在正确的空间里算。
 *
 * 旋转中心是 View 中心，而 View 是被居中的 —— 所以旋转中心 = 容器中心。
 * 这也是缩放/平移可以直接沿用屏幕坐标的前提：`translationX/Y` 在父坐标系里，
 * 不受 rotation 影响（View 的矩阵是 R·S 后再 T）。
 */
internal class RotatingPlayerFrame(context: Context) : FrameLayout(context) {

    /** 布局在里面的播放视图（充气自 view_mediarack_player.xml，surface 是 TextureView）。 */
    val playerView: PlayerView

    var rotationDeg: Int = 0
        set(value) {
            val normalized = normalizeRotation(value)
            if (field == normalized) return
            field = normalized
            playerView.rotation = normalized.toFloat()
            requestLayout()
        }

    init {
        // 播放视图在 90/270 时是"宽高交换"的，布局矩形会超出父容器。
        // 它转完之后的**视觉**矩形正好落在父容器里，但裁剪发生在父的坐标系上，
        // 所以这里放开 clipChildren，让旋转后的内容完整画出来。
        clipChildren = false
        clipToPadding = false

        playerView = android.view.LayoutInflater.from(context)
            .inflate(R.layout.view_mediarack_player, this, false) as PlayerView
        addView(playerView, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT))
    }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        // 自己撑满容器（Compose 侧给的是 EXACTLY）
        super.onMeasure(widthMeasureSpec, heightMeasureSpec)

        val swap = swapsDimensions(rotationDeg)
        val childW = if (swap) measuredHeight else measuredWidth
        val childH = if (swap) measuredWidth else measuredHeight
        // 覆盖 super 里按 MATCH_PARENT 测出来的尺寸：FOLLOW 后再 onLayout 用的就是这个值
        playerView.measure(
            MeasureSpec.makeMeasureSpec(childW, MeasureSpec.EXACTLY),
            MeasureSpec.makeMeasureSpec(childH, MeasureSpec.EXACTLY),
        )
    }

    override fun onLayout(changed: Boolean, left: Int, top: Int, right: Int, bottom: Int) {
        val w = right - left
        val h = bottom - top
        val cw = playerView.measuredWidth
        val ch = playerView.measuredHeight
        // 居中：90/270 时 cw > w，left 为负 —— 旋转后正好补回容器矩形
        val x = (w - cw) / 2
        val y = (h - ch) / 2
        playerView.layout(x, y, x + cw, y + ch)
    }

    /** 便于 Compose 侧写 `frame.rotationDeg = 90` 时顺手取到里面的 View。 */
    val inner: View get() = playerView
}
