package io.github.garfieldgod.mediarack

import android.app.Application
import io.github.garfieldgod.mediarack.core.db.AppDatabase
import io.github.garfieldgod.mediarack.core.download.DownloadRepository
import io.github.garfieldgod.mediarack.core.scan.LocalSourceScanner
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

class MediaRackApp : Application() {
    lateinit var db: AppDatabase
        private set
    lateinit var scanner: LocalSourceScanner
        private set

    /** 应用级协程域：下载 worker 与启动期任务挂这里，不随 UI 生命周期取消。 */
    val appScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    val downloads: DownloadRepository by lazy { DownloadRepository(this, db, scanner) }

    override fun onCreate() {
        super.onCreate()
        db = AppDatabase.get(this)
        scanner = LocalSourceScanner(this, db)
        appScope.launch {
            // 「下载」本地源注册 + 断点续传自愈（进程被杀后重启接着下）
            downloads.ensureSource()
            downloads.recover()
        }
    }
}
