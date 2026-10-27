package io.github.garfieldgod.mediarack.core.download

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import io.github.garfieldgod.mediarack.MediaRackApp
import io.github.garfieldgod.mediarack.R
import io.github.garfieldgod.mediarack.core.db.DownloadEntity
import io.github.garfieldgod.mediarack.ui.formatSize
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * 下载前台服务：唯一职责是保活 + 通知栏进度，下载循环本体在
 * [DownloadRepository] 的应用级协程里，进程死了重启后 recover() 续传。
 *
 * - dataSync 类型（Android 14+ 要求显式声明）
 * - 队列空了自动 stopSelf；下次入队由仓库重新 startForegroundService
 */
class DownloadService : Service() {

    companion object {
        private const val CHANNEL_ID = "download"
        private const val NOTIFICATION_ID = 100

        fun start(context: Context) {
            context.startForegroundService(Intent(context, DownloadService::class.java))
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, DownloadService::class.java))
        }
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var lastPercent = Int.MIN_VALUE
    private var lastTitle: String? = null

    override fun onCreate() {
        super.onCreate()
        val manager = getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(CHANNEL_ID) == null) {
            manager.createNotificationChannel(
                NotificationChannel(
                    CHANNEL_ID, "下载", NotificationManager.IMPORTANCE_LOW
                ).apply { description = "MediaRack 下载进度" }
            )
        }
        startForeground(
            NOTIFICATION_ID,
            buildNotification(null),
            if (Build.VERSION.SDK_INT >= 29) ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC else 0,
        )

        val repo = (application as MediaRackApp).downloads
        scope.launch {
            var seenIdle = false
            repo.ui.collect { ui ->
                if (ui.activeTitle != lastTitle || ui.percent != lastPercent) {
                    lastTitle = ui.activeTitle
                    lastPercent = ui.percent
                    manager.notify(NOTIFICATION_ID, buildNotification(ui))
                }
                if (ui.idle) {
                    if (seenIdle) stopSelf() else seenIdle = true // 排队一帧防误判（首帧可能还没落库）
                } else {
                    seenIdle = false
                }
            }
        }
    }

    private fun buildNotification(ui: DownloadRepository.Ui?): Notification {
        val text = when {
            ui == null || ui.activeTitle == null -> "准备下载…"
            ui.activeStatus == DownloadEntity.STATUS_PAUSED -> "已暂停：${ui.activeTitle}（等待 Wi-Fi）"
            ui.percent >= 0 -> "${ui.activeTitle} · ${ui.percent}% · ${formatSize(ui.bytesDone)} / ${formatSize(ui.bytesTotal)}"
            else -> ui.activeTitle
        }
        val builder = NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle("MediaRack 下载")
            .setContentText(text)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
        if (ui != null && ui.percent >= 0 && ui.activeStatus != DownloadEntity.STATUS_PAUSED) {
            builder.setProgress(100, ui.percent, false)
        }
        return builder.build()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_NOT_STICKY

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }
}
