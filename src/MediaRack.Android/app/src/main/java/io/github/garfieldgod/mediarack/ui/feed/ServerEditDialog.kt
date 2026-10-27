package io.github.garfieldgod.mediarack.ui.feed

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import io.github.garfieldgod.mediarack.core.db.ServerEntity
import io.github.garfieldgod.mediarack.core.remote.ApiClient
import kotlinx.coroutines.launch

/**
 * 添加 / 编辑远程源。抽屉里唯一的源管理入口（独立的源管理页已删除）。
 *
 * **一条配置 = 一个源**（服务端源门户版）：服务器只有 `http://ip:port/s/{源地址}` 一个
 * 对外入口，令牌跟源走。所以四栏——服务器地址、端口、源地址（slug）、访问令牌，
 * 与服务端管理页「访问方式」里的字段一一对应；名称留空时自动取服务端里的源名。
 *
 * 扫码配对：电脑端管理页出的二维码内容是 `http://<ip>:<port>/s/{slug}#token=xxx`，
 * 扫到直接回填四项。只在新增时给——编辑已有源没有重新扫的理由。
 *
 * **保存失败不关面板**：探活失败/令牌不对时，原因写在表单里，改一个字就能再试。
 */
@Composable
fun ServerEditDialog(
    existing: ServerEntity?,
    onDismiss: () -> Unit,
    onSave: suspend (host: String, port: String, sourcePath: String, token: String?, name: String?) -> SaveOutcome,
    onDelete: (() -> Unit)? = null,
) {
    // 从已有 base URL 拆出 host / port / 源地址路径（老版本配置没有路径，source 为空）
    val parsed = remember(existing) { ApiClient.splitSourceBaseUrl(existing?.baseUrl) }

    var host by remember(existing) { mutableStateOf(parsed.first) }
    var port by remember(existing) { mutableStateOf(parsed.second) }
    var sourcePath by remember(existing) { mutableStateOf(parsed.third) }
    var token by remember(existing) { mutableStateOf(existing?.token.orEmpty()) }
    var name by remember(existing) { mutableStateOf(existing?.name.orEmpty()) }
    var scanError by remember { mutableStateOf<String?>(null) }
    var saveError by remember { mutableStateOf<String?>(null) }
    var saving by remember { mutableStateOf(false) }
    /** 扫到配对码后正在并发探活候选地址（按钮禁用，字段保持已回填的 slug/token）。 */
    var resolving by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    fun fillFromUrl(url: String) {
        val (h, p, s) = ApiClient.splitSourceBaseUrl(url)
        host = h
        port = p
        sourcePath = s
        if (s.isEmpty()) {
            scanError = "这个码是老版服务端的，请在电脑端升级后重新生成"
        }
    }

    val scanner = rememberLauncherForActivityResult(ScanContract()) { result ->
        val text = result.contents
        if (text.isNullOrBlank()) return@rememberLauncherForActivityResult
        val payload = ApiClient.parsePairingPayload(text)
        if (payload == null) {
            scanError = "二维码内容不是源地址"
            return@rememberLauncherForActivityResult
        }
        payload.token?.let { token = it }
        if (payload.urls.isEmpty()) {
            scanError = null
            return@rememberLauncherForActivityResult
        }
        // 单地址码：直接回填。多候选配对码：并发探活选第一个可达的——
        // 一张码在哪个网络都能扫，用户不用挑"该扫哪张"。
        if (payload.urls.size == 1) {
            fillFromUrl(payload.urls.first())
            scanError = null
            return@rememberLauncherForActivityResult
        }
        resolving = true
        scanError = null
        scope.launch {
            val picked = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
                ApiClient.pickReachableBaseUrl(payload.urls)
            }
            resolving = false
            if (picked == null) {
                // slug/token 先回填着，改地址重试总比全部重扫省事
                scanError = "候选地址都连不上（在家/在外都试过了）"
            } else {
                fillFromUrl(picked)
                scanError = null
            }
        }
    }

    fun submit() {
        if (saving) return
        saving = true
        saveError = null
        scope.launch {
            when (val r = onSave(host, port, sourcePath, token, name)) {
                is SaveOutcome.Ok -> {
                    onDismiss()
                    saving = false
                }
                is SaveOutcome.Fail -> {
                    saving = false
                    saveError = r.reason
                }
            }
        }
    }

    AlertDialog(
        onDismissRequest = { if (!saving) onDismiss() },
        confirmButton = {
            Row {
                if (onDelete != null) {
                    TextButton(onClick = onDelete, enabled = !saving) {
                        Text("删除", color = MaterialTheme.colorScheme.error)
                    }
                }
                TextButton(
                    onClick = ::submit,
                    enabled = !saving && !resolving &&
                        host.isNotBlank() && port.isNotBlank() && sourcePath.isNotBlank(),
                ) {
                    if (saving) {
                        // 只留一个转圈太像"按钮没了"：带一行字说明正在探活
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(14.dp),
                                strokeWidth = 2.dp,
                            )
                            Spacer(Modifier.width(6.dp))
                            Text("保存中")
                        }
                    } else if (resolving) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(14.dp),
                                strokeWidth = 2.dp,
                            )
                            Spacer(Modifier.width(6.dp))
                            Text("寻找可达地址")
                        }
                    } else {
                        Text("保存")
                    }
                }
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss, enabled = !saving) { Text("取消") }
        },
        title = {
            // 标题与扫码按钮同行：扫码是"填这四栏"的另一条路，摆在字段旁边而不是底部
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    if (existing == null) "添加源" else "编辑源",
                    modifier = Modifier.weight(1f),
                )
                if (existing == null) {
                    IconButton(
                        onClick = { scanner.launch(scanOptions()) },
                        enabled = !saving,
                        modifier = Modifier.size(36.dp),
                    ) {
                        Icon(
                            Icons.Default.QrCodeScanner,
                            contentDescription = "扫描配对二维码",
                            tint = MaterialTheme.colorScheme.primary,
                            modifier = Modifier.size(22.dp),
                        )
                    }
                }
            }
        },
        text = {
            Column(
                verticalArrangement = Arrangement.spacedBy(8.dp),
                modifier = Modifier.verticalScroll(rememberScrollState()),
            ) {
                OutlinedTextField(
                    value = host,
                    onValueChange = { host = it.trim(); saveError = null },
                    label = { Text("服务器地址（必填）") },
                    placeholder = { Text("如 192.168.1.20") }, // hygiene:ignore 面向用户的示例，故意用常见家庭地址
                    singleLine = true,
                    enabled = !saving,
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = port,
                    onValueChange = { port = it.filter { c -> c.isDigit() }.take(5); saveError = null },
                    label = { Text("端口（必填）") },
                    placeholder = { Text("如 8080") },
                    singleLine = true,
                    enabled = !saving,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = sourcePath,
                    onValueChange = { sourcePath = it.trim(); saveError = null },
                    label = { Text("源地址（必填）") },
                    placeholder = { Text("如 movie，对应 /s/movie") },
                    supportingText = { Text("电脑端管理页「访问方式」里可查看，也可直接粘完整路径") },
                    singleLine = true,
                    enabled = !saving,
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = token,
                    onValueChange = { token = it; saveError = null },
                    label = { Text("访问令牌（必填）") },
                    placeholder = { Text("管理页里查看，或直接扫码回填") },
                    singleLine = true,
                    enabled = !saving,
                    visualTransformation = PasswordVisualTransformation(),
                    modifier = Modifier.fillMaxWidth(),
                )
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it },
                    label = { Text("名称（选填）") },
                    placeholder = { Text("不填则用服务端里的源名") },
                    singleLine = true,
                    enabled = !saving,
                    modifier = Modifier.fillMaxWidth(),
                )
                (saveError ?: scanError)?.let {
                    Text(
                        it,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error,
                        modifier = Modifier.padding(top = 2.dp),
                    )
                }
            }
        },
    )
}

/**
 * 扫码参数：只认二维码（配对码一定是 QR）。
 * 方向锁定关掉——真正的竖屏由 [QrScanActivity] 的 manifest 声明保证，
 * 让库的 `lockOrientation()` 退场（它会跟着当前屏幕旋转走，全屏横屏时反而锁成横的）。
 */
private fun scanOptions(): ScanOptions = ScanOptions().apply {
    setDesiredBarcodeFormats(ScanOptions.QR_CODE)
    setPrompt("对准电脑端管理页的源二维码")
    setBeepEnabled(false)
    setOrientationLocked(false)
    setCaptureActivity(QrScanActivity::class.java)
}
