package io.github.garfieldgod.mediarack.ui.feed

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.selection.TextSelectionColors
import androidx.compose.ui.draw.clip
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * App 内搜索：顶栏搜索图标进入，**跟随当前源**——
 * 选中某源只搜该源；「本地缓存」只搜本地；「全部」本地 + 各服务器并行搜。
 * 输入防抖 300ms；点结果 = 把这份结果当**连播列表**播下去（上下滑按结果顺序翻），
 * 返回时回到搜索页并恢复关键词。
 *
 * 关键词由调用方受控（[query] / [onQueryChange]）：退出连播要原路回到这一页，
 * 状态若留在这里会随页面销毁一起丢，重开就是空白框。
 */
@Composable
fun SearchScreen(
    vm: FeedViewModel,
    query: String,
    onQueryChange: (String) -> Unit,
    onBack: () -> Unit,
    onOpen: (FeedItem, PlayQueue?) -> Unit,
) {
    var results by remember { mutableStateOf(emptyList<FeedItem>()) }
    var searching by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val focusRequester = remember { FocusRequester() }
    val focusManager = LocalFocusManager.current

    // 只有空关键词才自动聚焦/弹键盘：从连播退回来时带着上次的词和结果，直接聚焦会
    // 把键盘盖在结果上（用户是回来接着挑的，不是重新输入）。
    LaunchedEffect(Unit) {
        if (query.isEmpty()) focusRequester.requestFocus()
    }

    LaunchedEffect(query) {
        val q = query.trim()
        if (q.isEmpty()) {
            results = emptyList()
            searching = false
            return@LaunchedEffect
        }
        searching = true
        delay(300) // 防抖：停手才打网络/查库
        results = runCatching { vm.search(q) }.getOrDefault(emptyList())
        searching = false
    }

    Column(
        Modifier
            .fillMaxSize()
            .background(Color(0xFF121419)),
    ) {
        // 顶栏：返回 + 搜索框
        Row(
            Modifier
                .fillMaxWidth()
                .background(Color(0xFF241B2B))
                .statusBarsPadding()
                .padding(horizontal = 10.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                Icons.AutoMirrored.Filled.ArrowBack,
                contentDescription = "返回",
                tint = Color.White,
                modifier = Modifier
                    .size(30.dp)
                    .clickable { onBack() }
                    .padding(5.dp),
            )
            Spacer(Modifier.width(10.dp))
            Row(
                Modifier
                    .weight(1f)
                    .background(Color(0xFF1B1E26), RoundedCornerShape(999.dp))
                    .padding(horizontal = 14.dp, vertical = 9.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(
                    Icons.Default.Search,
                    contentDescription = null,
                    tint = Color(0xFF8B919C),
                    modifier = Modifier.size(16.dp),
                )
                Spacer(Modifier.width(8.dp))
                BasicTextField(
                    value = query,
                    onValueChange = onQueryChange,
                    singleLine = true,
                    textStyle = TextStyle(color = Color.White, fontSize = 14.sp),
                    cursorBrush = SolidColor(Color(0xFFFB7299)),
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                    keyboardActions = KeyboardActions(onSearch = {
                        focusManager.clearFocus()
                        scope.launch { results = runCatching { vm.search(query) }.getOrDefault(emptyList()) }
                    }),
                    decorationBox = { inner ->
                        Box {
                            if (query.isEmpty()) {
                                Text(
                                    "搜索标题 / UP 主…",
                                    color = Color(0xFF5A6070),
                                    fontSize = 13.sp,
                                )
                            }
                            inner()
                        }
                    },
                    modifier = Modifier
                        .fillMaxWidth()
                        .focusRequester(focusRequester),
                )
                if (query.isNotEmpty()) {
                    Spacer(Modifier.width(6.dp))
                    Icon(
                        Icons.Default.Close,
                        contentDescription = "清空",
                        tint = Color(0xFF8B919C),
                        modifier = Modifier
                            .size(16.dp)
                            .clickable { onQueryChange("") },
                    )
                }
            }
        }

        // 结果计数
        if (query.isNotBlank()) {
            Text(
                when {
                    searching -> "搜索中…"
                    else -> "${results.size} 个结果 · ${currentScopeLabel(vm)}"
                },
                color = Color(0xFF8B919C),
                fontSize = 11.sp,
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 8.dp),
            )
        }

        if (results.isEmpty()) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Icon(
                        Icons.Default.Search,
                        contentDescription = null,
                        tint = Color(0xFF3A3F4B),
                        modifier = Modifier.size(46.dp),
                    )
                    Spacer(Modifier.height(12.dp))
                    Text(
                        when {
                            query.isBlank() -> "输入关键词搜索当前源的视频"
                            searching -> "搜索中…"
                            else -> "没有匹配的视频"
                        },
                        color = Color(0xFF8B919C),
                        fontSize = 13.sp,
                    )
                }
            }
        } else {
            LazyColumn(Modifier.fillMaxSize()) {
                items(results, key = { it.id }) { item ->
                    SearchRow(item, vm) {
                        // 点开这一刻的 results 就是连播队列（List 不可变，之后改输入不影响
                        // 正在播的那份）。只有一条时没有"连播"可言，退回单条定位。
                        val queue = if (results.size > 1) {
                            PlayQueue("搜索 · ${query.trim()}", results)
                        } else {
                            null
                        }
                        onOpen(item, queue)
                    }
                }
                item { Spacer(Modifier.navigationBarsPadding().height(24.dp)) }
            }
        }
    }
}

@Composable
private fun SearchRow(item: FeedItem, vm: FeedViewModel, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 4.dp)
            .background(Color(0xFF1B1E26), RoundedCornerShape(14.dp))
            .clickable { onClick() }
            .padding(10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier
                .width(110.dp)
                .aspectRatio(16f / 10f)
                // clip 在 background 之前：缩略图按 Cover 铺满后会画到容器外（见 ThumbImage
                // 的 rotatedFrame），没有这层裁剪就会盖住标题、窜到相邻行；顺带让圆角生效
                .clip(RoundedCornerShape(10.dp))
                .background(Color(0xFF2A2E3A)),
        ) {
            ThumbImage(item = item, vm = vm, modifier = Modifier.fillMaxSize())
        }
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(
                item.title,
                color = Color.White,
                fontSize = 13.sp,
                fontWeight = FontWeight.SemiBold,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                buildString {
                    item.sourceName?.takeIf { it.isNotBlank() }?.let { append(it).append(" · ") }
                    item.collectionName?.takeIf { it.isNotBlank() }?.let { append(it).append(" · ") }
                    append(if (item.isRemote) "远程" else "本地")
                },
                color = Color(0xFF8B919C),
                fontSize = 11.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

/** 当前搜索范围的文案（结果计数用）。 */
private fun currentScopeLabel(vm: FeedViewModel): String = when (val f = vm.filter.value) {
    is FeedFilter.Server -> f.name
    FeedFilter.Local -> "本地缓存"
    FeedFilter.Liked -> "收藏夹"
    FeedFilter.All -> "全部"
}
