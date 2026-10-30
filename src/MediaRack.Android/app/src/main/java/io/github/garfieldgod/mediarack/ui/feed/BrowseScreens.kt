package io.github.garfieldgod.mediarack.ui.feed

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
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
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.filled.StarBorder
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.garfieldgod.mediarack.core.remote.Favorite
import io.github.garfieldgod.mediarack.core.remote.RemoteCollection
import io.github.garfieldgod.mediarack.ui.formatDuration
import kotlinx.coroutines.launch

/**
 * 收藏页的浏览位置：当前 tab / 打开的收藏夹 / 打开的合集。
 *
 * 提到播放页持有，是为了「退出连播原路返回」——从某个收藏夹点开一条视频，
 * 退出时要回到**同一个收藏夹**，而不是被打回收藏夹列表再点一次。
 * 页面自己 `remember` 的话，一关（进连播）状态就没了。
 */
class FavoritesNav {
    var tab by mutableStateOf(0)
    var favorite by mutableStateOf<Pair<Long, Favorite>?>(null)
    var collection by mutableStateOf<CollectionRef?>(null)

    /** 从「我的」重新进入收藏：回到列表根部，并落到指定 tab。 */
    fun open(tab: Int) {
        this.tab = tab
        favorite = null
        collection = null
    }

    /** 是否停在收藏夹/合集的**内页**（顶栏返回键据此逐级回退，而不是直接退出页面）。 */
    val inDetail: Boolean get() = favorite != null || collection != null
}

/** 合集页的浏览位置：打开的是哪个合集（null = 合集列表）。理由同 [FavoritesNav]。 */
class CollectionsNav {
    var opened by mutableStateOf<CollectionRef?>(null)
}

/**
 * 合集浏览（服务端的结构层，只读）。
 *
 * 合集是源级、平铺的：single 源是"文件夹"（目录即合集），dual 源是"UP 主"。
 * 一个视频恰好属于一个合集——所以点进任意合集都能看到完整内容，不存在"父目录里是空的"。
 * 这里只负责浏览与点播，改内容请去收藏。
 *
 * 入口有两个，都带明确的"哪个源"：
 * - 抽屉里某个源行尾的「合集」→ [serverId] 过滤到这一个源（合集本来就是源级的东西）；
 * - 播放页的「合集」按钮 → 调用方先把 [nav].opened 设成当前条目所属的那个合集再显示本页，
 *   并传 [detailBackCloses]=true：返回直接关整页回播放页（不落回列表）。
 */
@Composable
fun CollectionsScreen(
    vm: FeedViewModel,
    onBack: () -> Unit,
    /** 点一条视频：带上它所在的整份合集列表（连播队列）。 */
    onOpen: (FeedItem, PlayQueue) -> Unit,
    /** 浏览位置（由播放页持有，退出连播时才能回到原来那个合集）。 */
    nav: CollectionsNav,
    /** 只看这个服务器上的合集（null = 不过滤）。 */
    serverId: Long? = null,
    /** 该服务器的显示名（过滤时的标题用）。 */
    sourceName: String? = null,
    /** 播放页预开详页进来时为 true：详页返回直接关整页回播放页，不落回列表。 */
    detailBackCloses: Boolean = false,
) {
    var load by remember { mutableStateOf<CollectionsLoad?>(null) }
    val opened = nav.opened

    LaunchedEffect(Unit) { load = runCatching { vm.loadCollections() }.getOrNull() }

    opened?.let { ref ->
        CollectionItemsPage(
            vm = vm,
            title = ref.name,
            kindLabel = if (ref.kind == "up") "UP 主" else "文件夹",
            serverId = ref.serverId,
            collectionId = ref.collectionId,
            onBack = { if (detailBackCloses) onBack() else nav.opened = null },
            onOpen = onOpen,
        )
        return
    }

    BrowserScaffold(
        title = if (serverId != null) "合集 · ${sourceName ?: "该源"}" else "合集",
        onBack = onBack,
    ) {
        val l = load
        // 源过滤在渲染前做一次：分组表头（源 id）在多源视图里才有意义
        val rows = l?.rows?.filter { serverId == null || it.first == serverId } ?: emptyList()
        when {
            l == null -> CenteredSpinner()
            rows.isEmpty() && l.offline -> LoadFailedHint("合集")
            rows.isEmpty() -> EmptyHint(
                if (serverId != null) "这个源里还没有合集。\n（single 源按目录、dual 源按 UP 主自动生成）"
                else "服务端还没有合集。\n（single 源按目录、dual 源按 UP 主自动生成）"
            )
            else -> Column(Modifier.fillMaxSize()) {
                if (l.offline) OfflineBanner()
                LazyColumn(Modifier.fillMaxSize()) {
                    // 按源分组：一条配置 = 一个源。单源视图下不打表头（标题已经写明是哪个源）
                    rows.groupBy { it.second.sourceId }.forEach { (srcId, group) ->
                        if (serverId == null) {
                            item(key = "hdr-$srcId") {
                                Text(
                                    srcId,
                                    color = Color(0xFF8B919C),
                                    fontSize = 12.sp,
                                    modifier = Modifier.padding(start = 16.dp, top = 14.dp, bottom = 4.dp),
                                )
                            }
                        }
                        items(group, key = { "${it.first}|${it.second.id}" }) { row ->
                            CollectionRowItem(row.second) {
                                nav.opened = CollectionRef(row.first, row.second.id, row.second.name, row.second.kind)
                            }
                        }
                    }
                    item { Spacer(Modifier.height(24.dp)) }
                }
            }
        }
    }
}

/**
 * 收藏（用户层，跨端）：两个页面分开——收藏夹（视频）与收藏的合集（关注的 UP 主/文件夹）。
 * 服务端是唯一真源：离线时只读缓存列表，收藏动作会提示失败（不假装成功）。
 *
 * 入口在「我的」页：**收藏的视频**（tab 0）/ **收藏的合集**（tab 1）两个独立入口，
 * 进来直接落在对应 tab，但仍可切回去——两个页面本来就是同一份数据的两个视图。
 *
 * 浏览位置在 [nav] 里（播放页持有）：从某个收藏夹点开视频、退出连播后要回到同一个夹子。
 */
@Composable
fun FavoritesScreen(
    vm: FeedViewModel,
    onBack: () -> Unit,
    /** 点一条视频：带上它所在的整份列表（连播队列）。 */
    onOpen: (FeedItem, PlayQueue) -> Unit,
    nav: FavoritesNav,
) {
    val tab = nav.tab
    var load by remember { mutableStateOf<FavoritesLoad?>(null) }
    /** 新建夹子后重新拉列表（收藏夹列表没有别的刷新时机）。 */
    var reloadTick by remember { mutableStateOf(0) }
    var creating by remember { mutableStateOf(false) }
    /** 正在改名的夹子 / 正在确认删除的夹子（两个动作共用行尾「⋯」）。 */
    var renaming by remember { mutableStateOf<Pair<Long, Favorite>?>(null) }
    var deleting by remember { mutableStateOf<Pair<Long, Favorite>?>(null) }
    val servers by vm.servers.collectAsState(initial = emptyList())
    // 收藏是源级的：当前选中某个源时，只展示该源的收藏夹；「全部」才合并展示（夹子名标注来源）
    val filter by vm.filter.collectAsState()
    val scoped = filter as? FeedFilter.Server

    LaunchedEffect(reloadTick, scoped?.serverId) {
        load = runCatching { vm.loadFavorites(scoped?.serverId) }.getOrNull()
    }

    nav.favorite?.let { (serverId, fav) ->
        CollectionItemsPage(
            vm = vm,
            title = fav.name,
            kindLabel = "收藏夹",
            serverId = serverId,
            favoriteId = fav.id,
            onBack = { nav.favorite = null },
            onOpen = onOpen,
        )
        return
    }

    nav.collection?.let { ref ->
        CollectionItemsPage(
            vm = vm,
            title = ref.name,
            kindLabel = if (ref.kind == "up") "UP 主" else "文件夹",
            serverId = ref.serverId,
            collectionId = ref.collectionId,
            onBack = { nav.collection = null },
            onOpen = onOpen,
        )
        return
    }

    BrowserScaffold(
        title = if (scoped != null) "收藏 · ${scoped.name}" else "收藏",
        onBack = onBack,
        // 新建只在「收藏夹」tab 有意义（收藏的合集是关注出来的，不是新建的）
        trailing = if (tab == 0) {
            {
                Icon(
                    Icons.Default.Add,
                    contentDescription = "新建收藏夹",
                    tint = Color(0xFFFB7299),
                    modifier = Modifier
                        .size(26.dp)
                        .clickable { creating = true }
                        .padding(4.dp),
                )
            }
        } else null,
    ) {
        Column(Modifier.fillMaxSize()) {
            Row(Modifier.padding(horizontal = 12.dp, vertical = 8.dp)) {
                SegButton("收藏夹", tab == 0) { nav.tab = 0 }
                Spacer(Modifier.width(8.dp))
                SegButton("收藏的合集", tab == 1) { nav.tab = 1 }
            }

            val l = load
            when {
                l == null -> CenteredSpinner()
                l.rows.isEmpty() && l.offline -> LoadFailedHint("收藏夹")
                l.rows.isEmpty() && tab == 0 -> EmptyHint("还没有收藏夹。\n（在视频页点 ❤ 收藏，需要联网）")
                l.rows.isEmpty() -> EmptyHint("还没有收藏的合集。\n（在合集页点 ★ 关注 UP 主 / 文件夹）")
                else -> Column(Modifier.fillMaxSize()) {
                    if (l.offline) OfflineBanner()
                    if (tab == 0) {
                        // 「全部」视图：每个源都有自己的「默认收藏夹」，重名分不清谁是谁 → 名后标注来源
                        FavoriteList(
                            l.rows,
                            serverNames = if (scoped == null) servers.associate { it.id to it.name } else emptyMap(),
                            scoped = scoped != null,
                            onOpen = { nav.favorite = it },
                            onRename = { renaming = it },
                            onDelete = { deleting = it },
                        )
                    } else {
                        FavoriteCollectionsList(vm, l.rows) { serverId, c ->
                            nav.collection = CollectionRef(serverId, c.id, c.name, c.kind)
                        }
                    }
                }
            }
        }
    }

    if (creating) {
        CreateFavoriteDialog(
            vm = vm,
            servers = servers,
            // 已按源过滤时新建的夹子就落在这个源上，不再让选
            pinnedServerId = scoped?.serverId,
            onDismiss = { creating = false },
            onCreated = {
                creating = false
                reloadTick++
            },
        )
    }

    renaming?.let { row ->
        RenameFavoriteDialog(
            row = row,
            vm = vm,
            onDismiss = { renaming = null },
            onRenamed = {
                renaming = null
                reloadTick++
            },
        )
    }

    deleting?.let { row ->
        DeleteFavoriteDialog(
            row = row,
            vm = vm,
            onDismiss = { deleting = null },
            onDeleted = {
                deleting = null
                reloadTick++
            },
        )
    }
}

/**
 * 收藏夹改名。名称只是显示值，改它不动夹子里的条目与来源归属。
 */
@Composable
private fun RenameFavoriteDialog(
    row: Pair<Long, Favorite>,
    vm: FeedViewModel,
    onDismiss: () -> Unit,
    onRenamed: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    var name by remember(row) { mutableStateOf(row.second.name) }
    var saving by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    AlertDialog(
        onDismissRequest = { if (!saving) onDismiss() },
        title = { Text("重命名收藏夹") },
        text = {
            Column {
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it.take(40); error = null },
                    label = { Text("名称") },
                    singleLine = true,
                    enabled = !saving,
                )
                error?.let {
                    Spacer(Modifier.height(8.dp))
                    Text(it, color = Color(0xFFE05B5B), fontSize = 12.sp)
                }
            }
        },
        confirmButton = {
            TextButton(
                // 名称没变就不给点：改名接口每次都写一遍 rev，没必要制造一次假修改
                enabled = !saving && name.isNotBlank() && name.trim() != row.second.name,
                onClick = {
                    scope.launch {
                        saving = true
                        val err = vm.renameFavorite(row.first, row.second.id, name)
                        saving = false
                        if (err == null) onRenamed() else error = err
                    }
                },
            ) { Text(if (saving) "保存中…" else "保存") }
        },
        dismissButton = { TextButton(onClick = onDismiss, enabled = !saving) { Text("取消") } },
    )
}

/**
 * 删收藏夹。服务端一个事务里删掉 `favorite` / `favorite_item` / `favorite_collection`，
 * 但**不动源里的视频文件**——「取消收藏」和「删文件」是两件事，文案必须说清，
 * 否则用户会以为删夹子等于删片子。
 */
@Composable
private fun DeleteFavoriteDialog(
    row: Pair<Long, Favorite>,
    vm: FeedViewModel,
    onDismiss: () -> Unit,
    onDeleted: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    var saving by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    AlertDialog(
        onDismissRequest = { if (!saving) onDismiss() },
        title = { Text("删除收藏夹") },
        text = {
            Column {
                Text(
                    "「${row.second.name}」里的 ${row.second.itemCount} 个视频会从收藏夹移除，" +
                        "视频文件本身不受影响。",
                    fontSize = 14.sp,
                    color = Color(0xFFC9CDD4),
                )
                error?.let {
                    Spacer(Modifier.height(8.dp))
                    Text(it, color = Color(0xFFE05B5B), fontSize = 12.sp)
                }
            }
        },
        confirmButton = {
            TextButton(
                enabled = !saving,
                onClick = {
                    scope.launch {
                        saving = true
                        val err = vm.deleteFavorite(row.first, row.second.id, row.second.name)
                        saving = false
                        if (err == null) onDeleted() else error = err
                    }
                },
            ) { Text(if (saving) "删除中…" else "删除", color = Color(0xFFE05B5B)) }
        },
        dismissButton = { TextButton(onClick = onDismiss, enabled = !saving) { Text("取消") } },
    )
}

/**
 * 新建收藏夹。收藏夹归属某台服务器（一配置=一源），多服务器时必须先选服务器——
 * 否则会静默建在"第一个源"上，用户下次在另一台机器上找不到。
 */
@Composable
private fun CreateFavoriteDialog(
    vm: FeedViewModel,
    servers: List<io.github.garfieldgod.mediarack.core.db.ServerEntity>,
    /** 页面已按源过滤时固定建在这个源上（null = 由用户选）。 */
    pinnedServerId: Long? = null,
    onDismiss: () -> Unit,
    onCreated: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    var name by remember { mutableStateOf("") }
    var serverId by remember(servers) { mutableStateOf(pinnedServerId ?: servers.firstOrNull()?.id) }
    var saving by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    AlertDialog(
        onDismissRequest = { if (!saving) onDismiss() },
        title = { Text("新建收藏夹") },
        text = {
            Column {
                if (servers.size > 1 && pinnedServerId == null) {
                    Text("建在哪台服务器", fontSize = 12.sp, color = Color(0xFF8B919C))
                    Spacer(Modifier.height(6.dp))
                    Row {
                        servers.forEach { s ->
                            SegButton(s.name, s.id == serverId) { serverId = s.id }
                            Spacer(Modifier.width(8.dp))
                        }
                    }
                    Spacer(Modifier.height(12.dp))
                }
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it.take(40); error = null },
                    label = { Text("名称") },
                    singleLine = true,
                    enabled = !saving,
                )
                error?.let {
                    Spacer(Modifier.height(8.dp))
                    Text(it, color = Color(0xFFE05B5B), fontSize = 12.sp)
                }
            }
        },
        confirmButton = {
            TextButton(
                enabled = !saving && name.isNotBlank() && serverId != null,
                onClick = {
                    val sid = serverId ?: return@TextButton
                    scope.launch {
                        saving = true
                        val err = vm.createFavorite(sid, name)
                        saving = false
                        if (err == null) onCreated() else error = err
                    }
                },
            ) { Text(if (saving) "创建中…" else "创建") }
        },
        dismissButton = { TextButton(onClick = onDismiss, enabled = !saving) { Text("取消") } },
    )
}

@Composable
private fun FavoriteList(
    rows: List<Pair<Long, Favorite>>,
    /** 「全部」视图下夹子名后标注来源；空 map = 已按源过滤，不标。 */
    serverNames: Map<Long, String>,
    /** 已按源过滤：计数是这个源里的，「收藏的合集」没按源过滤所以不并排显示。 */
    scoped: Boolean,
    onOpen: (Pair<Long, Favorite>) -> Unit,
    onRename: (Pair<Long, Favorite>) -> Unit,
    onDelete: (Pair<Long, Favorite>) -> Unit,
) {
    if (rows.isEmpty()) {
        EmptyHint("还没有收藏夹。\n（在视频页点 ❤ 收藏，需要联网）")
        return
    }

    LazyColumn(Modifier.fillMaxSize()) {
        items(rows, key = { "${it.first}|${it.second.id}" }) { row ->
            var menuOpen by remember { mutableStateOf(false) }
            Box {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .clickable { onOpen(row) }
                        .padding(start = 16.dp, end = 6.dp, top = 12.dp, bottom = 12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Box(
                        Modifier
                            .size(38.dp)
                            .clip(RoundedCornerShape(10.dp))
                            .background(Color(0xFFFB7299).copy(alpha = 0.16f)),
                        contentAlignment = Alignment.Center,
                    ) {
                        Icon(Icons.Default.Star, null, tint = Color(0xFFFB7299), modifier = Modifier.size(20.dp))
                    }
                    Spacer(Modifier.width(12.dp))
                    Column(Modifier.weight(1f)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(
                                row.second.name,
                                color = Color.White,
                                fontSize = 15.sp,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.weight(1f, fill = false),
                            )
                            serverNames[row.first]?.let { src ->
                                Spacer(Modifier.width(6.dp))
                                Text(
                                    "· $src",
                                    color = Color(0xFF8B919C),
                                    fontSize = 12.sp,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                            }
                        }
                        Text(
                            // 按源过滤时只报视频数：合集数是全库口径，两个数字并排会自相矛盾。
                            if (scoped) "${row.second.itemCount} 个视频"
                            else "${row.second.itemCount} 个视频 · ${row.second.collectionCount} 个合集",
                            color = Color(0xFF8B919C),
                            fontSize = 12.sp,
                        )
                    }
                    // 行尾「⋯」：改名 / 删除。夹子是用户自己的东西，两个动作该在列表上就能做，
                    // 不必先点进夹子再找设置（以前只能在 PC 管理台改）。
                    Box(Modifier.size(36.dp), contentAlignment = Alignment.Center) {
                        Icon(
                            Icons.Default.MoreVert,
                            contentDescription = "收藏夹操作",
                            tint = Color(0xFF8B919C),
                            modifier = Modifier
                                .size(20.dp)
                                .clickable { menuOpen = true },
                        )
                    }
                }
                DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                    DropdownMenuItem(
                        text = { Text("重命名") },
                        onClick = {
                            menuOpen = false
                            onRename(row)
                        },
                    )
                    DropdownMenuItem(
                        text = { Text("删除", color = Color(0xFFE05B5B)) },
                        onClick = {
                            menuOpen = false
                            onDelete(row)
                        },
                    )
                }
            }
        }
        item { Spacer(Modifier.height(24.dp)) }
    }
}

/**
 * 收藏的合集 = 关注的 UP 主 / 文件夹。合并所有收藏夹去重展示——
 * 客户端不关心它被收在哪个夹子里，只关心"我关注了什么"。
 */
@Composable
private fun FavoriteCollectionsList(
    vm: FeedViewModel,
    rows: List<Pair<Long, Favorite>>,
    onOpen: (Long, RemoteCollection) -> Unit,
) {
    var collections by remember(rows) { mutableStateOf<List<Pair<Long, RemoteCollection>>?>(null) }
    var allFailed by remember(rows) { mutableStateOf(false) }

    LaunchedEffect(rows) {
        val out = mutableListOf<Pair<Long, RemoteCollection>>()
        var failed = 0
        for ((serverId, fav) in rows) {
            // null = 该服务器不可达（与「夹子里真没有合集」区分）
            val cs = vm.loadFavoriteCollections(fav.id, serverId)
            if (cs == null) {
                failed++
                continue
            }
            cs.forEach { c ->
                if (out.none { it.second.id == c.id }) out += serverId to c
            }
        }
        collections = out
        allFailed = rows.isNotEmpty() && failed == rows.size
    }

    val list = collections
    when {
        list == null -> CenteredSpinner()
        allFailed -> LoadFailedHint("收藏的合集")
        list.isEmpty() -> EmptyHint("还没有收藏的合集。\n（在合集页点 ★ 关注 UP 主 / 文件夹）")
        else -> LazyColumn(Modifier.fillMaxSize()) {
            items(list, key = { "${it.first}|${it.second.id}" }) { row ->
                CollectionRowItem(row.second) { onOpen(row.first, row.second) }
            }
            item { Spacer(Modifier.height(24.dp)) }
        }
    }
}

/** 通用二级页外壳：返回箭头 + 标题 + 内容槽。 */
@Composable
private fun BrowserScaffold(
    title: String,
    onBack: () -> Unit,
    trailing: (@Composable () -> Unit)? = null,
    content: @Composable () -> Unit,
) {
    Column(
        Modifier
            .fillMaxSize()
            .background(Color(0xFF121419))
            .statusBarsPadding()
    ) {
        Row(
            Modifier
                .fillMaxWidth()
                .padding(horizontal = 8.dp, vertical = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                Icons.AutoMirrored.Filled.ArrowBack,
                contentDescription = "返回",
                tint = Color.White,
                modifier = Modifier
                    .size(26.dp)
                    .clickable { onBack() }
                    .padding(4.dp),
            )
            Spacer(Modifier.width(8.dp))
            Text(
                title,
                color = Color.White,
                fontWeight = FontWeight.Bold,
                fontSize = 16.sp,
                modifier = Modifier.weight(1f),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            trailing?.invoke()
            Spacer(Modifier.width(8.dp))
        }
        Box(Modifier.weight(1f).navigationBarsPadding()) { content() }
    }
}

/** 一行合集：folder 用文件夹图标，up 用人物图标 + 条数。 */
@Composable
private fun CollectionRowItem(c: RemoteCollection, onClick: () -> Unit) {
    val up = c.kind == "up"
    Row(
        Modifier
            .fillMaxWidth()
            .clickable { onClick() }
            .padding(horizontal = 16.dp, vertical = 11.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier
                .size(38.dp)
                .clip(if (up) CircleShape else RoundedCornerShape(10.dp))
                .background(if (up) Color(0xFF2A2E3A) else Color(0xFFFB7299).copy(alpha = 0.14f)),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                if (up) Icons.Default.Person else Icons.Default.Folder,
                contentDescription = null,
                tint = if (up) Color(0xFFECEEF2) else Color(0xFFFB7299),
                modifier = Modifier.size(20.dp),
            )
        }
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(c.name, color = Color.White, fontSize = 15.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            // 副标题：文件夹显示相对路径（重名时能分清"科幻/2024"与"纪录片/2024"）
            Text(
                if (up) "${c.itemCount} 个视频" else listOfNotNull(c.path.ifEmpty { null }, "${c.itemCount} 个视频").joinToString(" · "),
                color = Color(0xFF8B919C),
                fontSize = 12.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

/**
 * 合集 / 收藏夹里的视频列表。点一条就交给外部去播（注入信息流 + 跳转）。
 *
 * 离线兜底（[CollectionPageLoad.offline]）：列表是上次的快照，未缓存到本地的条目
 * 打「离线」标且点了只提示不播——收藏是服务端真源，离线时不该给出能看的错觉。
 */
@Composable
private fun CollectionItemsPage(
    vm: FeedViewModel,
    title: String,
    kindLabel: String,
    serverId: Long,
    collectionId: String? = null,
    favoriteId: String? = null,
    onBack: () -> Unit,
    onOpen: (FeedItem, PlayQueue) -> Unit,
) {
    var load by remember { mutableStateOf<CollectionPageLoad?>(null) }
    var reloadTick by remember { mutableStateOf(0) }
    var following by remember { mutableStateOf<Boolean?>(null) }
    /** 当前离线快照里「已缓存到本地、仍可播」的条目 id。 */
    var playable by remember { mutableStateOf<Set<String>>(emptySet()) }
    /** 正在"移到其他收藏夹"的条目（仅收藏夹页有）。 */
    var movingItem by remember { mutableStateOf<FeedItem?>(null) }
    val scope = rememberCoroutineScope()

    LaunchedEffect(collectionId, favoriteId, reloadTick) {
        // 星标初值：合集可能已被关注过（在任一收藏夹里）
        following = if (collectionId != null) vm.collectionFollowed(serverId, collectionId) else null
        val result = runCatching {
            vm.loadCollectionItems(
                serverId = serverId,
                collectionId = collectionId,
                favoriteId = favoriteId,
                limit = 200,
            )
        }.getOrNull()
        load = result
        playable = if (result != null && result.offline) {
            vm.cachedSubset(result.items).map { it.id }.toSet()
        } else {
            emptySet()
        }
    }

    BrowserScaffold(
        title = title,
        onBack = onBack,
        // 只有"合集"页能关注；收藏夹页本身不用再关注一次
        trailing = if (collectionId != null) {
            {
                Icon(
                    if (following == true) Icons.Default.Star else Icons.Default.StarBorder,
                    contentDescription = if (following == true) "取消关注" else "关注",
                    tint = if (following == true) Color(0xFFFB7299) else Color(0xFF8B919C),
                    modifier = Modifier
                        .size(26.dp)
                        .clickable {
                            scope.launch {
                                val now = vm.toggleCollectionFavorite(serverId, collectionId, title)
                                if (now != null) following = now
                            }
                        }
                        .padding(4.dp),
                )
            }
        } else null,
    ) {
        val l = load
        when {
            l == null -> CenteredSpinner()
            l.items.isEmpty() && l.offline -> LoadFailedHint(kindLabel, onRetry = { reloadTick++ })
            l.items.isEmpty() -> EmptyHint("这个${kindLabel}里还没有视频")
            else -> Column(Modifier.fillMaxSize()) {
                if (l.offline) OfflineBanner()
                Text(
                    "$kindLabel · ${l.total} 个视频",
                    color = Color(0xFF8B919C),
                    fontSize = 12.sp,
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
                )
                LazyColumn(Modifier.fillMaxSize()) {
                    items(l.items, key = { it.id }) { item ->
                        val offline = l.offline && item.id !in playable
                        VideoRow(
                            vm = vm,
                            item = item,
                            offline = offline,
                            // 只有收藏夹详情页能"移到其他夹"——合集是结构层（只读），不提供这个操作
                            onMore = if (favoriteId != null) {
                                { movingItem = item }
                            } else null,
                        ) {
                            if (offline) {
                                vm.toast("当前离线，该视频未缓存到本地")
                            } else {
                                // 连播队列 = 本页这份列表（顺序即屏幕上的顺序）
                                onOpen(item, PlayQueue("$kindLabel · $title", l.items))
                            }
                        }
                    }
                    item { Spacer(Modifier.height(24.dp)) }
                }
            }
        }
    }

    val fromFav = favoriteId
    val moving = movingItem
    if (fromFav != null && moving != null) {
        MoveFavoriteDialog(
            vm = vm,
            item = moving,
            serverId = serverId,
            fromFavoriteId = fromFav,
            onDismiss = { movingItem = null },
            onMoved = {
                movingItem = null
                reloadTick++ // 移走的条目不该再出现在这个夹里
            },
        )
    }
}

@Composable
private fun VideoRow(
    vm: FeedViewModel,
    item: FeedItem,
    offline: Boolean = false,
    /** 行尾的「⋯」：收藏夹详情页用它打开"移到其他收藏夹"。null = 不显示。 */
    onMore: (() -> Unit)? = null,
    onClick: () -> Unit,
) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable { onClick() }
            .padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier
                .width(96.dp)
                .height(54.dp)
                .clip(RoundedCornerShape(8.dp))
                .background(Color(0xFF1B1E26)),
        ) {
            ThumbImage(vm = vm, item = item, modifier = Modifier.fillMaxSize())
        }
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(
                item.title,
                color = Color.White,
                fontSize = 14.sp,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Spacer(Modifier.height(4.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    listOfNotNull(
                        item.collectionName,
                        if (item.durationMs > 0) formatDuration(item.durationMs) else null,
                    ).joinToString(" · "),
                    color = Color(0xFF8B919C),
                    fontSize = 12.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f, fill = false),
                )
                if (offline) {
                    Spacer(Modifier.width(6.dp))
                    // 离线徽标：服务端不可达且没缓存到本地，点了也只提示不播
                    Text(
                        "离线",
                        color = Color(0xFF9AA0AB),
                        fontSize = 10.sp,
                        modifier = Modifier
                            .background(Color(0xFF2A2E3A), RoundedCornerShape(4.dp))
                            .padding(horizontal = 5.dp, vertical = 1.dp),
                    )
                }
            }
        }
        if (onMore != null) {
            // 子层的 clickable 先吃掉事件，所以点「⋯」不会顺带进播放
            Icon(
                Icons.Default.MoreVert,
                contentDescription = "更多操作",
                tint = Color(0xFF8B919C),
                modifier = Modifier
                    .size(34.dp)
                    .clickable { onMore() }
                    .padding(7.dp),
            )
        }
    }
}

/** 把收藏夹里的一条移到另一个夹（或只移出本夹）。在线才写，失败会提示。 */
@Composable
private fun MoveFavoriteDialog(
    vm: FeedViewModel,
    item: FeedItem,
    serverId: Long,
    fromFavoriteId: String,
    onDismiss: () -> Unit,
    onMoved: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    var targets by remember { mutableStateOf<List<Favorite>?>(null) }
    var busy by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) {
        targets = vm.loadFavorites().rows
            .filter { it.first == serverId }
            .map { it.second }
            .filter { it.id != fromFavoriteId }
    }

    fun move(to: String?) {
        if (busy) return
        scope.launch {
            busy = true
            val ok = vm.moveFavoriteItem(item, serverId, fromFavoriteId, to)
            busy = false
            if (ok) onMoved()
        }
    }

    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = { Text("移到其他收藏夹") },
        text = {
            Column {
                Text(
                    item.title,
                    color = Color(0xFF8B919C),
                    fontSize = 12.sp,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
                Spacer(Modifier.height(10.dp))
                val list = targets
                when {
                    list == null -> Box(
                        Modifier.fillMaxWidth().padding(12.dp),
                        contentAlignment = Alignment.Center,
                    ) {
                        CircularProgressIndicator(modifier = Modifier.size(20.dp), strokeWidth = 2.dp)
                    }
                    list.isEmpty() -> Text(
                        "这台服务器上还没有别的收藏夹。\n回收藏页右上角 + 新建一个。",
                        color = Color(0xFF8B919C),
                        fontSize = 12.sp,
                    )
                    else -> list.forEach { fav ->
                        Row(
                            Modifier
                                .fillMaxWidth()
                                .clickable(enabled = !busy) { move(fav.id) }
                                .padding(vertical = 10.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Icon(
                                Icons.Default.Star, null,
                                tint = Color(0xFFFB7299),
                                modifier = Modifier.size(16.dp),
                            )
                            Spacer(Modifier.width(8.dp))
                            Text(
                                fav.name,
                                color = Color.White,
                                fontSize = 14.sp,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.weight(1f),
                            )
                            Text("${fav.itemCount}", color = Color(0xFF8B919C), fontSize = 11.sp)
                        }
                    }
                }
            }
        },
        confirmButton = {
            TextButton(enabled = !busy, onClick = { move(null) }) {
                Text("移出本夹", color = Color(0xFFE05B5B))
            }
        },
        dismissButton = { TextButton(onClick = onDismiss, enabled = !busy) { Text("取消") } },
    )
}

/** 离线兜底提示条：列表是上次的内容，未缓存的视频不可播。 */
@Composable
private fun OfflineBanner() {
    Text(
        "当前离线，显示上次加载的内容（未缓存的视频不可播放）",
        color = Color(0xFFB6BAC2),
        fontSize = 11.5.sp,
        modifier = Modifier
            .fillMaxWidth()
            .background(Color(0xFF22252D))
            .padding(horizontal = 16.dp, vertical = 8.dp),
    )
}

/** 加载失败（离线且没有快照）。与「空空如也」分开——前者该给重试。 */
@Composable
private fun LoadFailedHint(what: String, onRetry: (() -> Unit)? = null) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text("${what}加载失败", color = Color(0xFF8B919C), fontSize = 13.sp)
            Spacer(Modifier.height(4.dp))
            Text("服务器不可达，且没有离线缓存", color = Color(0xFF8B919C), fontSize = 12.sp)
            if (onRetry != null) {
                Spacer(Modifier.height(12.dp))
                Text(
                    "重试",
                    color = Color(0xFFFB7299),
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier
                        .clickable { onRetry() }
                        .padding(horizontal = 16.dp, vertical = 6.dp),
                )
            }
        }
    }
}

@Composable
private fun SegButton(label: String, active: Boolean, onClick: () -> Unit) {
    Text(
        label,
        color = if (active) Color.White else Color(0xFF8B919C),
        fontSize = 13.sp,
        fontWeight = if (active) FontWeight.SemiBold else FontWeight.Normal,
        modifier = Modifier
            .background(
                if (active) Color(0xFFFB7299).copy(alpha = 0.18f) else Color.White.copy(alpha = 0.05f),
                RoundedCornerShape(999.dp),
            )
            .clickable { onClick() }
            .padding(horizontal = 14.dp, vertical = 7.dp),
    )
}

@Composable
private fun CenteredSpinner() {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        CircularProgressIndicator(modifier = Modifier.size(22.dp), strokeWidth = 2.dp, color = Color(0xFFFB7299))
    }
}

@Composable
private fun EmptyHint(text: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(text, color = Color(0xFF8B919C), fontSize = 13.sp)
    }
}
