package io.github.garfieldgod.mediarack.ui.feed

import com.journeyapps.barcodescanner.CaptureActivity
import com.journeyapps.barcodescanner.DecoratedBarcodeView
import com.journeyapps.barcodescanner.Size

/**
 * 扫码页：锁竖屏 + 正方形取景框。
 *
 * 为什么不直接用库自带的 `CaptureActivity`：
 * 1) 库在自己的 manifest 里把它声明成 `screenOrientation="sensorLandscape"`，
 *    手机竖着拿，扫码页照样是横的（`setOrientationLocked(false)` 时更没人改它）；
 * 2) 库的取景框算法 `CameraPreview.calculateFramingRect` 只在「高 > 宽」时
 *    才把框二次收成正方形，横屏时留的是一块扁长方形。
 * 锁竖屏 + 显式给正方形边长，两件事一起解决。
 *
 * 方向在 manifest 里锁（`android:screenOrientation="portrait"`），这里只定尺寸。
 */
class QrScanActivity : CaptureActivity() {
    override fun initializeContent(): DecoratedBarcodeView {
        val view = super.initializeContent()
        // 预览用 TextureView 而不是默认的 SurfaceView：SurfaceView 的画面走独立图层，
        // adb screencap / 录屏抓不到——预览区一片黑，只剩遮罩和激光线，排查取景框
        // 时极易误判成"相机没起来"。扫码页没有低延迟诉求，换成 TextureView 没有损失。
        view.barcodeView.setUseTextureView(true)
        // 边长按屏宽的 72%：库默认给 80%，配上底部提示词会顶到屏幕边缘
        val side = (resources.displayMetrics.widthPixels * 0.72f).toInt()
        view.barcodeView.setFramingRectSize(Size(side, side))
        return view
    }
}
