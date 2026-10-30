package io.github.garfieldgod.mediarack.ui.feed

import android.graphics.BitmapFactory
import android.util.Log
import android.util.LruCache
import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
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
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items as gridItems
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.pager.VerticalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.ui.draw.clip
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.CollectionsBookmark
import androidx.compose.material.icons.filled.Crop
import androidx.compose.material.icons.filled.Dns
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.FavoriteBorder
import androidx.compose.material.icons.filled.FitScreen
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Fullscreen
import androidx.compose.material.icons.filled.FullscreenExit
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.automirrored.filled.RotateRight
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ModalDrawerSheet
import androidx.compose.material3.ModalNavigationDrawer
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.runtime.State
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.MergingMediaSource
import androidx.media3.exoplayer.source.ProgressiveMediaSource
import androidx.media3.ui.PlayerView
import io.github.garfieldgod.mediarack.R
import io.github.garfieldgod.mediarack.core.remote.ApiClient
import io.github.garfieldgod.mediarack.ui.formatDuration
import io.github.garfieldgod.mediarack.ui.formatSize
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

/**
 * 主界面（UI 信息流版，design/ui_mockup_v3 + 真机反馈）：
 * 竖向 Pager 切视频（非当前页显示海报缩略图）、边缘右滑抽屉选源、左滑「我的」。
 *
 * - 切换源/过滤时立刻停掉旧视频，加载圈下显示「正在连接 <源名>」和实时网速
 * - 合集页 / 我的页是滑动进入的二级页，返回键逐级回退
 * - 播放器单实例常驻底层；翻二级页视频继续播
 */

@OptIn(ExperimentalMaterial3Api::class)
@androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
@Composable
fun FeedScreen(
    vm: FeedViewModel = viewModel(),
) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val lifecycleOwner = androidx.lifecycle.compose.LocalLifecycleOwner.current
    val scope = rememberCoroutineScope()

    val items by vm.items.collectAsState()
    val loading by vm.loading.collectAsState()
    val message by vm.message.collectAsState()
    val connecting by vm.connecting.collectAsState()
    val likedIds by vm.likedIds.collectAsState(initial = emptySet())
    val watchedIds by vm.watchedIds.collectAsState(initial = emptySet())
    val filter by vm.filter.collectAsState()
    /** 非空 = 正在某个收藏夹 / 合集里连播（顶栏显示来源 + 退出按钮）。 */
    val queue by vm.playQueue.collectAsState()

    LaunchedEffect(Unit) { vm.buildFeed() }

    // ── 播放器（单实例，常驻底层） ─────────────────────────
    // DefaultDataSource 路由 file/http；MeteredDataSource 统计读取字节供网速显示。
    // 播放器与预载管理器必须由**同一个 Builder** 产出：共享 RenderersFactory /
    // TrackSelector / LoadControl / 带宽计，是 PreloadMediaPeriod 能被播放器
    // 直接复用（预载样本无缝移交）的前提。
    val preloader = remember { FeedPreloader() }
    val playerAndPreload = remember {
        val builder =
            androidx.media3.exoplayer.source.preload.DefaultPreloadManager.Builder(
                context,
                preloader.statusControl,
            ).setMediaSourceFactory(
                androidx.media3.exoplayer.source.DefaultMediaSourceFactory(feedDataSourceFactory(context))
            )
        val manager = builder.build()
        manager to builder.buildExoPlayer()
    }
    val preloadManager = playerAndPreload.first
    val player = playerAndPreload.second
    DisposableEffect(Unit) {
        onDispose {
            player.release()
            preloadManager.release()
        }
    }

    // ── 前后台：进后台暂停、回来恢复（方案 A，B 站同款） ──────────
    // 播放器的释放只绑在组合上（上面的 onDispose）：按 Home / 手势回桌面时
    // 组合还活着，视频会继续在后台出声。这里在 ON_STOP 记住"当时在不在播"并暂停，
    // ON_START 按记录恢复——回来接着看；onDispose 走到时 Activity 已销毁，无冲突。
    DisposableEffect(lifecycleOwner) {
        var resumeOnStart = false
        val observer = androidx.lifecycle.LifecycleEventObserver { _, event ->
            when (event) {
                androidx.lifecycle.Lifecycle.Event.ON_STOP -> {
                    resumeOnStart = player.playWhenReady
                    player.playWhenReady = false
                }
                androidx.lifecycle.Lifecycle.Event.ON_START -> {
                    if (resumeOnStart) player.playWhenReady = true
                }
                else -> {}
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    // 切换源/过滤：立刻停掉旧视频（旧条目不能在连接新源期间继续出声）
    LaunchedEffect(filter) {
        player.stop()
        player.clearMediaItems()
    }

    // 覆盖层状态：合集页 / 我的页 / 已下载管理（二级页，返回键逐级回退）
    var showMine by remember { mutableStateOf(false) }
    var showDownloads by remember { mutableStateOf(false) }
    var showCollections by remember { mutableStateOf(false) }
    var showFavorites by remember { mutableStateOf(false) }
    var showSearch by remember { mutableStateOf(false) }
    /**
     * 搜索关键词。放在这一层而不是 [SearchScreen] 内部：搜索结果的连播也记来源页
     * （[QueueHome.Search]），退出连播要原路回到搜索页并恢复关键词——页面级 `remember`
     * 会随页面关闭一起丢掉，重开的搜索页就是空白的。
     */
    var searchQuery by remember { mutableStateOf("") }
    /**
     * 收藏页 / 合集页的**浏览位置**。放在这一层而不是页面内部：
     * 进连播时页面会被关掉（否则挡着画面），页面内部 `remember` 的"进到哪个夹子"
     * 就一起没了——退出连播只能回到列表根部。提到这里，退出时能原路回到同一页。
     */
    val favoritesNav = remember { FavoritesNav() }
    val collectionsNav = remember { CollectionsNav() }

    // ── 二级页盖屏 = 暂停，关掉 = 恢复 ──────────────────────
    // 合集/我的/收藏/已下载都是全屏滑入，画面被完全盖住，视频继续播只剩声音
    // （"翻二级页视频继续播"是旧设计，全屏覆盖后就成了背景噪音）。
    // 语义与进后台一致：打开时记住"当时在不在播"并暂停，全部关掉后按记录恢复；
    // 自己本来就没播的话，关掉也保持暂停。跳转（在合集页点视频）会先关页再播新条目，
    // 关页触发的恢复不等新播放器状态生效就被 pendingJump 接管，无冲突。
    var resumeAfterOverlay by remember { mutableStateOf(false) }
    val overlayOpen = showMine || showDownloads || showCollections || showFavorites || showSearch
    LaunchedEffect(overlayOpen) {
        if (overlayOpen) {
            resumeAfterOverlay = player.playWhenReady
            player.playWhenReady = false
        } else if (resumeAfterOverlay) {
            resumeAfterOverlay = false
            player.playWhenReady = true
        }
    }
    /**
     * 合集页限定到哪个源（抽屉里每个源行尾的「合集」按钮 → id+名字）。
     * 合集是**源级**的，所以不再有"全部源混在一起"的合集列表入口。
     */
    var collectionsScope by remember { mutableStateOf<Pair<Long, String>?>(null) }
    var collectionFor by remember { mutableStateOf<FeedItem?>(null) }
    var pendingJump by remember { mutableStateOf<String?>(null) }
    /** 退出连播后要停回哪一条（媒体 id）。items 会整份换回信息流，编号全变，得记住这条。 */
    var exitKeep by remember { mutableStateOf<String?>(null) }
    /** 进连播前的落脚点：主信息流那一条 + 来源页（退出时原路返回）。 */
    var queueOrigin by remember { mutableStateOf<QueueOrigin?>(null) }
    /** 主信息流当前正在播的那条（由信息流回报，进连播时用来记"回来的位置"）。 */
    var feedCurrentId by remember { mutableStateOf<String?>(null) }

    val drawerState = androidx.compose.material3.rememberDrawerState(
        initialValue = androidx.compose.material3.DrawerValue.Closed
    )

    // 源对话框：抽屉里唯一的源管理入口（独立的「源与服务器管理」页已删除）。
    // add 与 edit 共用一个弹窗，existing 是否为 null 决定标题与是否显示删除。
    var showAddSource by remember { mutableStateOf(false) }
    var editServer by remember {
        mutableStateOf<io.github.garfieldgod.mediarack.core.db.ServerEntity?>(null)
    }

    // 全屏兜底退出：注册在最先 = 优先级最低，抽屉/合集/我的都关完才轮到退全屏。
    // 横屏下右上角退出按钮之外永远有一条返回手势兜底，"切换不回去"不再可能。
    var fullscreen by remember { mutableStateOf(false) }
    BackHandler(enabled = fullscreen) { fullscreen = false }

    /**
     * 退出连播：**原路返回**。
     *
     * 两件事同时做：
     * 1. 播放器停回**进连播前**那一条（[QueueOrigin.keepId]）——以前是停在刚播的连播条目上，
     *    结果是"从收藏夹看了一条视频，退出后主信息流也被改成那一条"，莫名其妙；
     * 2. 把来源页重新打开（收藏夹 / 合集 / 本地合集）——以前直接落回主页面，
     *    想接着挑第二条得重新走「我的 → 收藏的视频 → 那个夹子」。
     *
     * 顺序无所谓：页面是覆盖层，先开页面再换 items 也不会闪（列表换好在底下发生）。
     */
    val leaveQueue: () -> Unit = {
        val origin = queueOrigin
        queueOrigin = null
        origin?.keepId?.let { exitKeep = it }
        when (val home = origin?.home) {
            QueueHome.Favorites -> showFavorites = true
            QueueHome.Collections -> showCollections = true
            is QueueHome.Search -> {
                // 关键词也一起还原：搜索页是受控的，重开时靠它自动重跑一遍同样的搜索
                searchQuery = home.query
                showSearch = true
            }
            is QueueHome.LocalCollection -> collectionFor = home.origin
            null -> Unit
        }
        vm.exitQueue()
    }

    // 连播是"临时换了播放列表"，返回 = 回到来源页。注册得比抽屉/二级页都早（优先级最低），
    // 否则二级页开着时按返回会先把连播退掉、页面留在原地。
    BackHandler(enabled = queue != null) { leaveQueue() }

    // 返回层级：抽屉 > 合集页 > 我的页 > 已下载管理（后注册的先响应）
    BackHandler(enabled = drawerState.isOpen) { scope.launch { drawerState.close() } }
    BackHandler(enabled = collectionFor != null) { collectionFor = null }
    BackHandler(enabled = showMine) { showMine = false }
    BackHandler(enabled = showDownloads) { showDownloads = false }
    BackHandler(enabled = showSearch) { showSearch = false }
    // 合集/收藏页内部还有一层（列表 → 某个合集/收藏夹）：先退内层再关整页，
    // 否则从连播退回来站在合集的视频列表里，一按返回直接掉回信息流，跳过了上一层。
    // 例外：从播放页「合集」按钮进来的（collectionsScope==null、直接预开了详页），
    // 返回一步就该回播放页，不许中途落到"全部源的合集列表"。
    BackHandler(enabled = showCollections) {
        if (collectionsNav.opened != null && collectionsScope != null) collectionsNav.opened = null
        else showCollections = false
    }
    BackHandler(enabled = showFavorites) {
        if (favoritesNav.inDetail) favoritesNav.open(favoritesNav.tab) else showFavorites = false
    }

    /**
     * 跳到指定条目（续播 / 收藏 / 合集点选共用）。
     *
     * [queue] 非空 = 从收藏夹 / 合集页点进来的：播放页的 items 整个换成那份列表，
     * 上下滑就是在这个夹子里前后翻（"连续播放某收藏夹/合集"）。为 null（如已下载管理）
     * 只做单条注入——那种场合没有"一份列表"可依。
     */
    val jumpTo: (FeedItem, PlayQueue?) -> Unit = { target, queue ->
        // 合集/收藏页点条目：先把覆盖层关掉，否则跳转发生在它下面，用户什么都看不到。
        // 但**记住是从哪一页进来的**：退出连播要原路回到那一页（页面自己的浏览位置
        // 在 favoritesNav / collectionsNav 里，没被清掉，所以能回到同一个夹子）。
        if (queue != null) {
            queueOrigin = QueueOrigin(
                keepId = feedCurrentId,
                home = when {
                    showSearch -> QueueHome.Search(searchQuery)
                    showFavorites -> QueueHome.Favorites
                    showCollections -> QueueHome.Collections
                    collectionFor != null -> QueueHome.LocalCollection(collectionFor!!)
                    else -> null
                },
            )
        }
        showCollections = false
        showFavorites = false
        // 搜索页也是"来源页"：连播时一起关掉（否则挡着画面），退出时按 QueueHome 原路打开
        showSearch = false
        // 本地目录合集也要等上面的 queueOrigin 记完再关，否则返回来源会被清成 null。
        collectionFor = null

        pendingJump = when {
            queue != null -> vm.playInQueue(queue, target)
            // 信息流里没有的远程条目：单条注入（pinItem 会在下次 buildFeed 时被换掉）
            target.isRemote && items.none { it.id == target.id } -> vm.pinItem(target)
            else -> {
                if (!target.isRemote) vm.setFilter(FeedFilter.All)
                target.id
            }
        }
    }

    ModalNavigationDrawer(
        drawerState = drawerState,
        drawerContent = {
            FeedDrawer(
                vm = vm,
                filter = filter,
                onSelect = { scope.launch { drawerState.close() } },
                onAddSource = {
                    scope.launch { drawerState.close() }
                    showAddSource = true
                },
                onEditServer = { s ->
                    scope.launch { drawerState.close() }
                    editServer = s
                },
                onOpenDownloads = {
                    scope.launch { drawerState.close() }
                    showDownloads = true
                },
                // 合集是源级的：入口在**每个源行**的编辑铅笔左侧，不再有全局「合集」行
                onOpenSourceCollections = { s ->
                    scope.launch { drawerState.close() }
                    collectionsNav.opened = null
                    collectionsScope = s.id to s.name
                    showCollections = true
                },
            )
        },
    ) {
        FeedContent(
            vm = vm,
            player = player,
            preloader = preloader,
            preloadManager = preloadManager,
            items = items,
            loading = loading,
            likedIds = likedIds,
            watchedIds = watchedIds,
            filter = filter,
            queue = queue,
            exitKeepId = exitKeep,
            pendingJump = pendingJump,
            fullscreen = fullscreen,
            onFullscreenChange = { fullscreen = it },
            onJumpConsumed = { pendingJump = null },
            onJumpRequest = jumpTo,
            onExitKeepConsumed = { exitKeep = null },
            onCurrentChange = { feedCurrentId = it },
            onExitQueue = leaveQueue,
            onOpenDrawer = { scope.launch { drawerState.open() } },
            onOpenMine = { showMine = true },
            onOpenSearch = { showSearch = true },
            // 远程条目走服务端合集页（"像进 UP 主主页"）；本地条目仍是旧的目录合集页
            onOpenCollection = { item ->
                val sid = item.serverId
                val cid = item.collectionId
                if (item.isRemote && sid != null && !cid.isNullOrEmpty()) {
                    collectionsNav.opened = CollectionRef(
                        serverId = sid,
                        collectionId = cid,
                        name = item.collectionName ?: "合集",
                        kind = item.collectionKind ?: "",
                    )
                    // 从播放页进来的合集页不按源过滤（直接展示这一个合集）
                    collectionsScope = null
                    showCollections = true
                } else {
                    collectionFor = item
                }
            },
            onAddSource = { showAddSource = true },
        )
    }

    // ── 二级页覆盖层（滑动进入；视频在底下继续播） ─────────
    AnimatedVisibility(
        visible = showMine,
        enter = slideInHorizontally { it } + fadeIn(),
        exit = slideOutHorizontally { it } + fadeOut(),
    ) {
        MineScreen(
            vm = vm,
            onBack = { showMine = false },
            onResume = { mark ->
                showMine = false
                // 「我的」是源级的：续播目标与当前选中源一致就留在本源（目标条目本就在
                // 这份信息流里），只有跨源时才回「全部」——一律强切会把用户拽出刚选的源
                val mismatch = when (val f = vm.filter.value) {
                    is FeedFilter.Server -> mark.scope != "srv:${f.serverId}"
                    FeedFilter.Local -> !mark.scope.startsWith("local:")
                    else -> false
                }
                if (mismatch) vm.setFilter(FeedFilter.All)
                pendingJump = "${mark.scope}|${mark.key}"
            },
            // 收藏的两个入口（原先挂在抽屉/源管理里，收藏不是源的东西）
            onOpenFavorites = { tab ->
                showMine = false
                favoritesNav.open(tab)
                showFavorites = true
            },
            onOpenDownloads = { showDownloads = true },
        )
    }
    // 已下载管理：抽屉「本地缓存 → 管理」和「我的 → 已下载管理」都进这里
    AnimatedVisibility(
        visible = showDownloads,
        enter = slideInHorizontally { it } + fadeIn(),
        exit = slideOutHorizontally { it } + fadeOut(),
    ) {
        DownloadsScreen(
            vm = vm,
            onBack = { showDownloads = false },
            onOpen = { item ->
                showDownloads = false
                showMine = false
                jumpTo(item, null)
            },
        )
    }
    // 搜索页：跟随当前源（顶栏搜索图标进入）。点结果 = 把这份结果当成连播列表，
    // 上下滑就是按搜索结果顺序往下播（结果为空/单条时退回单条定位，没有"连播"可言）
    AnimatedVisibility(
        visible = showSearch,
        enter = slideInHorizontally { it } + fadeIn(),
        exit = slideOutHorizontally { it } + fadeOut(),
    ) {
        SearchScreen(
            vm = vm,
            query = searchQuery,
            onQueryChange = { searchQuery = it },
            onBack = { showSearch = false },
            onOpen = { item, queue -> jumpTo(item, queue) },
        )
    }
    AnimatedVisibility(
        visible = showCollections,
        enter = slideInHorizontally { it } + fadeIn(),
        exit = slideOutHorizontally { it } + fadeOut(),
    ) {
        CollectionsScreen(
            vm = vm,
            onBack = {
                // 返回键在合集页内部先退"合集详页"（CollectionsScreen 自己处理），
                // 退到列表再按一次才关掉整页——关的时候顺手把位置清成列表根部。
                showCollections = false
                collectionsNav.opened = null
                collectionsScope = null
            },
            onOpen = { item, q -> jumpTo(item, q) },
            nav = collectionsNav,
            serverId = collectionsScope?.first,
            sourceName = collectionsScope?.second,
            // 播放页进来的：详页没有"列表"这层可退，返回键/左上箭头都直接关整页回播放页
            detailBackCloses = collectionsScope == null,
        )
    }
    AnimatedVisibility(
        visible = showFavorites,
        enter = slideInHorizontally { it } + fadeIn(),
        exit = slideOutHorizontally { it } + fadeOut(),
    ) {
        FavoritesScreen(
            vm = vm,
            onBack = { showFavorites = false },
            onOpen = { item, q -> jumpTo(item, q) },
            nav = favoritesNav,
        )
    }
    AnimatedVisibility(
        visible = collectionFor != null,
        enter = slideInHorizontally { it } + fadeIn(),
        exit = slideOutHorizontally { it } + fadeOut(),
    ) {
        collectionFor?.let { origin ->
            CollectionPage(
                vm = vm,
                origin = origin,
                onBack = { collectionFor = null },
                onOpen = { target, q ->
                    jumpTo(target, q)
                },
            )
        }
    }

    // 切源探活期间给个明确反馈：探活最长 2s，没有提示会像卡死
    connecting?.let { name ->
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Row(
                Modifier
                    .background(Color.Black.copy(alpha = 0.78f), RoundedCornerShape(999.dp))
                    .padding(horizontal = 18.dp, vertical = 12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                CircularProgressIndicator(
                    modifier = Modifier.size(16.dp),
                    strokeWidth = 2.dp,
                    color = Color(0xFFFB7299),
                )
                Spacer(Modifier.width(10.dp))
                Text("正在连接「$name」…", color = Color.White, fontSize = 13.sp)
            }
        }
    }

    // 提示条：非阻断、自动消失，点一下立即消失。
    // 之前是 AlertDialog + 「知道了」——"某源不可达"这种一句话结论不值得打断一次操作，
    // 而且 AlertDialog 窄、长文案一换行就散架。
    message?.let { msg ->
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.BottomCenter) {
            Text(
                msg,
                color = Color.White,
                fontSize = 13.sp,
                textAlign = TextAlign.Center,
                modifier = Modifier
                    .navigationBarsPadding()
                    .padding(horizontal = 24.dp)
                    .padding(bottom = 112.dp)
                    .background(Color(0xFF2A2E3A).copy(alpha = 0.96f), RoundedCornerShape(12.dp))
                    .clickable { vm.consumeMessage() }
                    .padding(horizontal = 16.dp, vertical = 11.dp),
            )
        }
    }

    if (showAddSource) {
        ServerEditDialog(
            existing = null,
            onDismiss = { showAddSource = false },
            // 面板自己不关：探活失败的原因要留在表单里让人就地改（成功才由 dialog 关）
            onSave = { host, port, sourcePath, token, name ->
                vm.saveServer(null, host, port, sourcePath, token, name)
            },
        )
    }
    editServer?.let { server ->
        ServerEditDialog(
            existing = server,
            onDismiss = { editServer = null },
            onSave = { host, port, sourcePath, token, name ->
                vm.saveServer(server, host, port, sourcePath, token, name)
            },
            onDelete = {
                vm.deleteServer(server.id)
                editServer = null
            },
        )
    }
}

// ─────────────────────────────────────────────────────────────
// 信息流主体：底层播放器 + 竖向 Pager（缩略图页）+ 控制层
// ─────────────────────────────────────────────────────────────

@OptIn(ExperimentalMaterial3Api::class)
@androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
@Composable
private fun FeedContent(
    vm: FeedViewModel,
    player: ExoPlayer,
    preloader: FeedPreloader,
    preloadManager: androidx.media3.exoplayer.source.preload.DefaultPreloadManager,
    items: List<FeedItem>,
    loading: Boolean,
    likedIds: Set<String>,
    watchedIds: Set<String>,
    filter: FeedFilter,
    /** 非空 = 正在某个收藏夹 / 合集里连播；顶栏显示来源与位置，✕ 退出。 */
    queue: PlayQueue?,
    /** 退出连播后要停回的那一条（由外层在退出瞬间记下，避免播放器跳到别的视频）。 */
    exitKeepId: String?,
    pendingJump: String?,
    fullscreen: Boolean,
    onFullscreenChange: (Boolean) -> Unit,
    onJumpConsumed: () -> Unit,
    onJumpRequest: (FeedItem, PlayQueue?) -> Unit,
    onExitQueue: () -> Unit,
    onExitKeepConsumed: () -> Unit,
    /** 主信息流当前页变了（进连播时据此记住"回来该停在哪条"）。 */
    onCurrentChange: (String?) -> Unit,
    onOpenDrawer: () -> Unit,
    onOpenMine: () -> Unit,
    onOpenSearch: () -> Unit,
    onOpenCollection: (FeedItem) -> Unit,
    onAddSource: () -> Unit,
) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = rememberCoroutineScope()
    val pagerState = rememberPagerState(pageCount = { items.size })
    val settled by remember { derivedStateOf { pagerState.settledPage } }

    val currentId = items.getOrNull(settled)?.id
    // 空态文案要区分"没有任何源"和"有源但没条目"，所以这里也要看服务器列表
    val servers by vm.servers.collectAsState(initial = emptyList())
    var loadError by remember { mutableStateOf<String?>(null) }
    var playError by remember { mutableStateOf<String?>(null) }
    var resolving by remember { mutableStateOf(false) }
    var resolveTick by remember { mutableStateOf(0) }
    var playerReady by remember { mutableStateOf(false) }
    // 播放器里**实际装着哪一条**（第几手的标记）。
    // 只看 playerReady 有个致命空档：settled 刚变成 B 的那一帧，LaunchedEffect 还没跑，
    // playerReady 仍是 A 留下的 true —— 于是 B 页被当成"已就绪"透明处理，
    // 露出的却是播放器里 A 的最后一帧，表现为翻页/点上一条时闪一下上一个视频。
    // 加上 mediaId 就精确了：只有 mediaId == 当前条目 id 才算真的就绪。
    var mediaId by remember { mutableStateOf<String?>(null) }
    /**
     * 已经**就绪过一次**的条目 id。
     *
     * 首帧缩略图只在"这条还没出过画面"时垫底；一旦出过画面就再不撤。
     * 以前判定用的是 playerReady：拖进度条会 seek 到未缓冲区间，ExoPlayer 短暂
     * BUFFERING → playerReady=false → 缩略图顶上来（它没有 zoom/cover/旋转），
     * 于是放大着拖进度条会看到画面先缩回 1x、再跳回来。seek 不是换片，不该撤画面。
     */
    var readyOnceFor by remember { mutableStateOf<String?>(null) }
    var buffering by remember { mutableStateOf(false) }
    // 直连播不了 → 让服务端转封装后再播（HEVC 参数集缺失这类解析失败的唯一出路）。
    // remuxFor 是要回退的条目 id，remuxNote 是给用户的一句说明。
    var remuxFor by remember { mutableStateOf<String?>(null) }
    var remuxNote by remember { mutableStateOf<String?>(null) }
    /** 已经回退过的条目：同一条只试一次，免得失败—回退—再失败打转。 */
    val remuxTried = remember { mutableSetOf<String>() }
    var speedText by remember { mutableStateOf<String?>(null) }
    var playing by remember { mutableStateOf(false) }
    var playbackSpeed by remember { mutableStateOf(1f) }

    /**
     * 播放模式：播完暂停 / 循环播放 / 自动切集。存在 ViewModel 里（落盘），
     * 这里只是拿来重组用的本地镜像。
     */
    var playMode by remember { mutableStateOf(vm.playMode) }
    val playModeState = androidx.compose.runtime.rememberUpdatedState(playMode)

    // 「继续观看」要排除正在播的这条；顺带回报给上一层记"进连播前停在哪条"
    LaunchedEffect(currentId) {
        vm.setNowPlaying(currentId)
        onCurrentChange(currentId)
    }

    // 画面适配：false = Fit（整幅可见，默认）/ true = Cover（铺满，裁边）。
    // 全局设置（跟播放模式一样落盘）：不跟源、不跟视频，横竖屏共用一份，
    // 重启 App 保留上次的选择。这里只是内存镜像，切换时写回 vm。
    var cover by remember { mutableStateOf(vm.coverFit) }

    // 双指缩放 / 放大后平移（默认状态 = 原始比例、无偏移）。
    // 刻意用 State 对象而不是普通 var：捏合期间它每帧都在变，若这一层直接读它的值，
    // 整个信息流（含 Pager 内容）会跟着每帧重组，捏合手感直接崩。
    // 读它的只有三处：播放器 View 的 update、手势层（在手势协程里读）、恢复原比例按钮。
    val zoom = remember { mutableStateOf(VideoZoom()) }

    // 双击暂停的中央短暂反馈：0 无 / 1 已暂停 / 2 播放中。
    // 不给反馈的话，暂停后画面静止和卡死在观感上没有区别。
    var tapFeedback by remember { mutableStateOf(0) }
    LaunchedEffect(tapFeedback) {
        if (tapFeedback != 0) {
            delay(550)
            tapFeedback = 0
        }
    }

    // 横竖屏分支：竖屏 UI 原样；横屏走 LandscapeControls 专版控制层
    val isLandscape =
        androidx.compose.ui.platform.LocalConfiguration.current.orientation ==
            android.content.res.Configuration.ORIENTATION_LANDSCAPE

    // ExoPlayer 错误是异步回调（prepare 不抛异常）——不挂 Listener 就是一直黑屏。
    // 用 rememberUpdatedState 拿当前条目 id：错误可能来自**已经被切走**的那条媒体
    // （跳过不可达源时尤其明显），那种错误必须丢掉，否则会钉在已经回退到的正常页上。
    val currentItemId = androidx.compose.runtime.rememberUpdatedState(currentId)
    // 播完要用的三样东西都是"当前值"，闭包里读不到最新的，必须走 rememberUpdatedState
    val settledState = androidx.compose.runtime.rememberUpdatedState(settled)
    val itemsState = androidx.compose.runtime.rememberUpdatedState(items)
    // 预载限流用：当前条目 READY 了才去预载邻居，避免起播和预载抢带宽
    val playerReadyState = androidx.compose.runtime.rememberUpdatedState(playerReady)
    DisposableEffect(player) {
        val listener = object : Player.Listener {
            override fun onPlayerError(error: PlaybackException) {
                if (player.currentMediaItem?.mediaId != currentItemId.value) return
                // 完整堆栈进 logcat：code=2000 这种兜底码在界面上什么都看不出来，
                // 真正的原因（HTTP 状态码 / 超时 / 解析失败）只活在 cause 链里。
                Log.e(
                    TAG_PLAY,
                    "播放失败 code=${error.errorCode} media=${currentItemId.value} " +
                        "uri=${player.currentMediaItem?.localConfiguration?.uri}",
                    error
                )

                // 远程条目直连播不了 → 让服务端先转封装成单文件 mp4 再试一次。
                // 最典型的场景是 B 站缓存的 HEVC m4s：参数集放在分片里、hvcC 是空的，
                // ExoPlayer 一解析就 IndexOutOfBounds，除了重封装没有别的路。
                val id = currentItemId.value
                val item = itemsState.value.getOrNull(settledState.value)
                // 连不上服务器时转封装同样拿不到，白等一轮只会让提示更难懂；
                // 只有"连上了但读不懂/放不出"（解析失败、解码失败、HTTP 异常）才值得退这一步。
                val worthRemux =
                    error.errorCode != PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED &&
                        error.errorCode != PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT
                if (worthRemux && id != null && item != null && item.isRemote && remuxTried.add(id)) {
                    remuxFor = id
                    remuxNote = "本机无法直连播放，正在让服务端转封装…"
                    playError = null
                    buffering = true
                    playerReady = false
                    return
                }

                remuxNote = null
                playError = playbackErrorText(error)
                buffering = false
                playerReady = false
            }

            override fun onPlaybackStateChanged(playbackState: Int) {
                playerReady = playbackState == Player.STATE_READY || playbackState == Player.STATE_ENDED
                buffering = playbackState == Player.STATE_BUFFERING
                if (playbackState == Player.STATE_READY) {
                    playError = null
                    // 本条真的出过画面了：此后 seek / 换码率引起的 buffering 都不再垫缩略图
                    val id = player.currentMediaItem?.mediaId
                    if (id != null && id == currentItemId.value) readyOnceFor = id
                }
                // 播完：先把最终进度和最后播放时间落库，再按模式决定下一步。
                // 循环播放不走这里（REPEAT_MODE_ONE 压根不报 ENDED）。
                if (playbackState == Player.STATE_ENDED) {
                    val list = itemsState.value
                    val i = settledState.value
                    list.getOrNull(i)?.let { ended ->
                        // 迟到的 ENDED 若属于已切走的媒体，不许污染当前条目的进度。
                        if (player.currentMediaItem?.mediaId == ended.id) {
                            val duration = player.duration.coerceAtLeast(0L)
                            vm.savePosition(ended, duration, duration)
                        }
                    }
                    pageAfterPlaybackEnded(playModeState.value, i, list.lastIndex)?.let { next ->
                        scope.launch { pagerState.animateScrollToPage(next) }
                    }
                }
            }

            override fun onIsPlayingChanged(p: Boolean) {
                playing = p
            }
        }
        player.addListener(listener)
        onDispose { player.removeListener(listener) }
    }

    /**
     * 播放模式 → 播放器。循环用 ExoPlayer 自带的 REPEAT_MODE_ONE：
     * 自己监听 ENDED 再 seekTo(0) 会在结尾多插一帧黑场/重缓冲，原生循环没有这个问题。
     * 自动切集则必须自己接管 ENDED（见上面的 listener）——REPEAT_MODE 不知道"下一条"是谁。
     */
    LaunchedEffect(playMode) {
        player.repeatMode =
            if (playMode == PlayMode.LoopOne) Player.REPEAT_MODE_ONE else Player.REPEAT_MODE_OFF
        // 落盘：下次冷启动还是这个模式
        vm.playMode = playMode
    }

    // 网速采样：MeteredDataSource 统计字节，600ms 差分
    LaunchedEffect(Unit) {
        while (true) {
            val s0 = SpeedMeter.bytes.get()
            delay(600)
            val bps = (SpeedMeter.bytes.get() - s0) * 1000 / 600
            speedText = if (bps > 60_000) formatSize(bps) + "/s" else null
        }
    }

    // 进度状态（进度轮询 / 拖动 / 滑块共用；切条目时复位）
    var sliderPos by remember { mutableStateOf(0f) }
    var sliderDur by remember { mutableStateOf(0f) }
    var dragging by remember { mutableStateOf(false) }

    // ── 源边界与两条独立提示 ────────────────────────────────
    // lastSourceKey：上一次落位的源（"local" / "srv:<id>"）；lastGoodPage：最后一个确认可播的页。
    // 「源不可达」和「没有更多了」是两个独立结论，各占一条提示胶囊——合并成一句
    // 会让人误读成"没有更多"是源不可达的注解，其实它是"后面没有别的源了"另一个理由。
    var lastSourceKey by remember { mutableStateOf<String?>(null) }
    var lastGoodPage by remember { mutableStateOf(0) }
    var blockedHint by remember { mutableStateOf<String?>(null) }
    var noMoreHint by remember { mutableStateOf<String?>(null) }
    // 「正在连接 X」只在真正建立新连接（切过滤器 / 跨源探活）时显示，普通翻页不显示
    var sourceSwitching by remember { mutableStateOf(false) }
    LaunchedEffect(blockedHint) {
        if (blockedHint != null) {
            delay(2000)
            blockedHint = null
        }
    }
    LaunchedEffect(noMoreHint) {
        if (noMoreHint != null) {
            delay(2000)
            noMoreHint = null
        }
    }

    // 直连播不了 → 服务端转封装兜底。
    // 触发者是 onPlayerError（解析失败、HTTP 异常等），同一条只走一次：
    // 转封装完服务端有缓存，下次切回来 remuxReady 会立刻命中，代价只有一次直连失败。
    LaunchedEffect(remuxFor) {
        val id = remuxFor ?: return@LaunchedEffect
        val target = items.firstOrNull { it.id == id } ?: return@LaunchedEffect
        // 这里**绝不能**把 remuxFor 置回 null：它就是本 effect 的 key，
        // 一改就重启、正在跑的转封装协程当场被取消（表现是永远停在"正在转封装"）。
        // key 由切条目那边的 LaunchedEffect(currentId) 负责清。

        val url = withContext(Dispatchers.IO) {
            runCatching { vm.remuxPlayable(target) }.getOrNull()
        }
        if (url == null) {
            buffering = false
            remuxNote = null
            playError = "服务端转封装失败（本机没有 ffmpeg 或超时）"
            return@LaunchedEffect
        }

        remuxNote = "已改用服务端转封装播放"
        playError = null
        player.stop()
        player.clearMediaItems()
        player.setMediaItem(MediaItem.Builder().setUri(url).setMediaId(id).build())
        player.prepare()
        player.playWhenReady = true
    }

    // 当前页 → 载入媒体（resolve 结果缓存避免反复拉详情）
    LaunchedEffect(currentId, resolveTick) {
        // 切条目的第一件事：把上一个视频停掉并清空。
        // 不清的话解析/建连期间旧画面和旧声音还留在底层——用户看到的是
        // 「新条目首帧一闪 → 又回到上一个视频继续播」；解析失败时更是旧视频配着错误提示。
        // 这里也必须放在 item==null 的早退之前：切源瞬间 items 会短暂为空/换源。
        player.stop()
        player.clearMediaItems()
        playerReady = false
        mediaId = null
        // 每个视频回到常速（倍速不跨条目沿用）
        player.playbackParameters = player.playbackParameters.withSpeed(1f)
        playbackSpeed = 1f
        // 缩放同理：上一条放大的位置对下一条毫无意义
        zoom.value = VideoZoom()
        sliderPos = 0f
        sliderDur = 0f

        val item = items.getOrNull(settled)
        loadError = null
        playError = null
        // 换条目：清掉上一条的转封装提示，并允许它以后再回退一次
        // （服务端这时多半已有缓存，重试几乎不花时间）
        remuxNote = null
        // 清掉转封装目标：顺带取消上一条还没转完的兜底协程
        remuxFor = null
        if (currentId != null) remuxTried.remove(currentId)
        if (item == null) {
            resolving = false
            return@LaunchedEffect
        }

        // 跨源进入远程源：**先探活，再决定要不要解析**。
        // 以前是"探活和解析两个 effect 并行赛跑"：解析必然先失败并上屏
        // 「播放失败 + 重试」，探活后脚才把页面滚回去——于是回退到正常页之后
        // 那个错误按钮还挂着，看起来像跳过失败。顺序化之后不可达的源根本不会产生错误。
        // 探活有 20s 缓存：同源内翻页、紧接着的重试都是内存命中，不会真打网络。
        val itemSourceKey = item.serverId?.let { "srv:$it" } ?: "local"
        val crossedSource = itemSourceKey != lastSourceKey
        lastSourceKey = itemSourceKey
        // 「重试」必须重新探一次（force）：否则 20s 缓存里那个 false 会让"重试"变成又一次跳过
        val retrying = resolveTick > 0
        if ((crossedSource || retrying) && item.serverId != null) {
            resolving = true
            sourceSwitching = true
            val reachable = vm.probeServer(item.serverId, force = retrying)
            sourceSwitching = false
            resolving = false
            if (!reachable) {
                val name = item.sourceName ?: "该源"
                val next = nextPageOutsideSource(items, settled, item.serverId)
                blockedHint = if (next != null) "「$name」不可达，已跳过" else "「$name」不可达"
                if (next != null) {
                    pagerState.scrollToPage(next)
                } else {
                    // 不可达的源就是最后一个：退到上一个可播条目，并单独说"没有更多了"
                    noMoreHint = "没有更多了"
                    val back = lastGoodPage.coerceIn(0, items.lastIndex)
                    if (back != settled) pagerState.scrollToPage(back)
                }
                return@LaunchedEffect
            }
        }
        lastGoodPage = settled
        resolving = true
        try {
            val (video, audio) = vm.resolvePlay(item)
            // mediaId 打进 MediaItem：ExoPlayer 的异步错误才能判定"是不是当前这条的"
            val media = { uri: String ->
                MediaItem.Builder().setUri(uri).setMediaId(item.id).build()
            }
            // 这条有预载好的 → 直接接管 PreloadMediaSource：预载期建好的 MediaPeriod
            // （含已缓冲的前几秒样本）原样移交播放器，重新连网和首段缓冲全省。
            // 条件是 id 和 URI 都对得上——转封装兜底换了地址的那种不走这条路。
            val entry = preloader.registered[settled]
            val wrapper = entry?.takeIf {
                it.itemId == item.id && it.mediaItem.localConfiguration?.uri.toString() == video
            }?.let { preloadManager.getMediaSource(it.mediaItem) }
            if (wrapper != null) {
                Log.d(TAG_PLAY, "预载接管 media=${item.id} → 切换即播")
                player.setMediaSource(wrapper)
            } else if (audio != null) {
                val factory = ProgressiveMediaSource.Factory(feedDataSourceFactory(context))
                player.setMediaSource(
                    MergingMediaSource(
                        factory.createMediaSource(media(video)),
                        factory.createMediaSource(media(audio)),
                    )
                )
            } else {
                player.setMediaItem(media(video))
            }
            player.prepare()
            if (wrapper != null) {
                // prepare 已触发 onUsedByPlayer：预载的 period 归了播放器，此刻把管理器
                // 里的登记摘掉只清预载状态、不碰播放（在 prepare 前摘会毁掉预载数据）
                preloader.registered.remove(settled)
                preloadManager.remove(entry!!.mediaItem)
            }
            // 媒体已换成本条目：此后 STATE_READY 才代表"这一条"可播
            mediaId = item.id
            val resume = vm.resumePositionMs(item)
            if (resume > 0) player.seekTo(resume)
            player.playWhenReady = true
        } catch (e: kotlinx.coroutines.CancellationException) {
            // 翻页 / 跳过取消了这个 effect —— 不是播放失败，绝不能上屏
            throw e
        } catch (e: Exception) {
            loadError = e.message ?: "播放地址解析失败"
        }
        resolving = false
    }

    // ── 相邻条目预载（DefaultPreloadManager，方案 B） ─────────────
    // 给上下 ±1 页的远程条目预 prepare + 缓冲前 5 秒进内存；翻到时上面的播放
    // 效果会接管 PreloadMediaSource，样本原样移交 → 切换即播。
    // 必须声明在播放效果**之后**：换队列时播放效果先把播放器停掉，这里才清登记，
    // 顺序反了会把正在移交的预载数据 release 掉。
    LaunchedEffect(settled, items, resolveTick) {
        preloader.currentIdx = settled

        // 限流：等当前条目就绪（或最多 5 秒）再预载邻居，起播时不抢带宽
        withTimeoutOrNull(5_000) {
            snapshotFlow { playerReadyState.value }.first { it }
        }

        val window =
            (settled - FeedPreloader.RANGE..settled + FeedPreloader.RANGE).toList()

        // 注销失效登记：滑出窗口、或那页的条目已经换人（切源/换队列）。
        // 当前页的不动——要么已被播放效果接管，要么马上要接管。
        for ((idx, entry) in preloader.registered.toList()) {
            if (idx != settled &&
                (idx !in window || items.getOrNull(idx)?.id != entry.itemId)
            ) {
                preloader.registered.remove(idx)
                runCatching { preloadManager.remove(entry.mediaItem) }
            }
        }

        // 登记 + 预载窗口内的远程邻居（当前页例外：播放器正在播它）
        for (i in window) {
            if (i == settled) continue
            val neighbor = items.getOrNull(i) ?: continue
            if (!neighbor.isRemote) continue
            if (preloader.registered[i]?.itemId == neighbor.id) continue
            // 同页号挂着别的条目 → 先注销（remove 会释放它，别在用时摘）
            preloader.registered.remove(i)?.let { runCatching { preloadManager.remove(it.mediaItem) } }
            // 解析地址（vm 有缓存；邻居首次出现这里会发一次详情请求，失败就跳过）
            val urls = runCatching { vm.resolvePlay(neighbor) }.getOrNull() ?: continue
            val (nVideo, nAudio) = urls
            val mediaItem = MediaItem.Builder().setUri(nVideo).setMediaId(neighbor.id).build()
            val factory = ProgressiveMediaSource.Factory(feedDataSourceFactory(context))
            val source = if (nAudio != null) {
                MergingMediaSource(
                    factory.createMediaSource(mediaItem),
                    factory.createMediaSource(
                        MediaItem.Builder().setUri(nAudio).setMediaId(neighbor.id).build()
                    ),
                )
            } else {
                factory.createMediaSource(mediaItem)
            }
            preloader.registered[i] = FeedPreloader.Entry(neighbor.id, mediaItem, source)
            runCatching { preloadManager.add(source, i) }
        }
        preloadManager.setCurrentPlayingIndex(settled)
    }

    // 进度轮询 + 节流落库
    LaunchedEffect(currentId) {
        var tick = 0
        while (true) {
            val dur = (player.duration.takeIf { it > 0 } ?: 0L).toFloat()
            sliderDur = dur
            if (!dragging) sliderPos = player.currentPosition.coerceAtLeast(0L).toFloat()
            // 只在**真的在播**时落库：暂停 / 播完还继续写的话，「继续观看」的 played_at
            // 会被一条静止的视频每 4 秒刷一次，卡片就永远钉在它上面。
            if (++tick % 8 == 0 && player.isPlaying) {
                items.getOrNull(settled)?.let { vm.savePosition(it, player.currentPosition, player.duration) }
            }
            delay(500)
        }
    }

    // 跳转（续播 / 收藏 / 合集点选）：items 更新后执行。
    //
    // **先消费再滚动**，而且滚动丢给外层协程：scrollToPage 是挂起函数，
    // 写在 effect 里的话，一旦滚动途中 items / pendingJump 又变（进连播时必然发生），
    // effect 被取消、onJumpConsumed 永远没跑 → 那个跳转目标一直挂在状态里，
    // 退出连播后 items 整份换回信息流时它又被命中一次，主信息流被拉回刚播的那条。
    LaunchedEffect(items, pendingJump) {
        val id = pendingJump ?: return@LaunchedEffect
        val idx = items.indexOfFirst { it.id == id }
        if (idx < 0) return@LaunchedEffect
        onJumpConsumed()
        scope.launch { pagerState.scrollToPage(idx) }
    }

    // UI 显隐是「播放器界面」的状态，不是条目的属性：A 里隐藏了，滑到 B 仍然是隐藏的。
    var uiHidden by remember { mutableStateOf(false) }
    val item = items.getOrNull(settled)
    val isHidden = uiHidden

    // 退出连播后停回原来那条：items 被整份换回信息流、编号全变，
    // 不修正的话用户正看着 2/2 点 ✕，播放器会落到信息流里第 2 条（完全另一个视频）。
    // 队列与 items 都参与 key：只盯 items 的话，"队列已清但列表还没换"这一帧
    // 可能刚好错过（尤其退出瞬间列表长度没变时），恢复就再也不触发了。
    // 同样先消费再滚动：滚动途中本 effect 被取消也不影响已经记下的还原动作。
    LaunchedEffect(queue, exitKeepId, items) {
        if (queue != null) return@LaunchedEffect
        val keep = exitKeepId ?: return@LaunchedEffect
        val idx = items.indexOfFirst { it.id == keep }
        if (idx < 0) return@LaunchedEffect
        onExitKeepConsumed()
        scope.launch { pagerState.scrollToPage(idx) }
    }

    /**
     * 用户覆盖的旋转角（0/90/180/270）。**跟条目走，不跟播放器走**：
     * 远程条目来自服务端 media_meta.rotation，本地条目来自 Room 的 items.rotation。
     * `remember(item.id)` 让翻页自动换成本条目的角度，不需要额外写复位。
     */
    var rotation by remember(item?.id) { mutableStateOf(item?.rotation ?: 0) }

    /** 点一次 +90°：先上屏（立即有反馈），再写回；写失败就退回原角度并提示。 */
    fun rotateCurrent() {
        val target = item ?: return
        val prev = rotation
        val next = (prev + 90) % 360
        rotation = next
        scope.launch { if (!vm.setRotation(target, next)) rotation = prev }
    }

    // 顶/底边界提示（短暂显示后消失）
    var edgeHint by remember { mutableStateOf<String?>(null) }
    var prevSettled by remember { mutableStateOf(0) }
    LaunchedEffect(settled, items.size) {
        if (items.isEmpty()) {
            prevSettled = 0
            return@LaunchedEffect
        }
        edgeHint = when {
            settled == items.lastIndex && settled > 0 -> "↓ 没有更多了"
            settled == 0 && prevSettled > 0 -> "↑ 已经到顶了"
            else -> null
        }
        prevSettled = settled
        if (edgeHint != null) {
            delay(1600)
            edgeHint = null
        }
    }

    // ── 跨源翻页的探活与跳过，已经并进上面的媒体加载 effect ─────────
    // 之前这里是独立的 effect：它和解析各自打网络、互相赛跑，先失败的那条
    // 会把「播放失败 + 重试」留在屏上。现在"探活 → 跳过 / 解析"是有序的一串。

    // 全屏模式：横屏 + 沉浸式系统栏；控制层按 isLandscape 整层替换
    val activity = context as? android.app.Activity
    DisposableEffect(fullscreen) {
        activity?.requestedOrientation = if (fullscreen) {
            android.content.pm.ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        } else {
            android.content.pm.ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
        }
        activity?.window?.let { window ->
            val controller = androidx.core.view.WindowCompat.getInsetsController(window, window.decorView)
            if (fullscreen) {
                controller.systemBarsBehavior =
                    androidx.core.view.WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
                controller.hide(androidx.core.view.WindowInsetsCompat.Type.systemBars())
            } else {
                controller.show(androidx.core.view.WindowInsetsCompat.Type.systemBars())
                // 亮度可能被横屏竖滑手势改过，退出全屏还原为跟随系统
                window.attributes = window.attributes.apply {
                    screenBrightness = android.view.WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
                }
            }
        }
        onDispose { }
    }
    // 离开本界面时恢复竖屏与系统亮度，避免下一界面继承横屏
    DisposableEffect(Unit) {
        onDispose {
            activity?.requestedOrientation = android.content.pm.ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
            activity?.window?.let { window ->
                window.attributes = window.attributes.apply {
                    screenBrightness = android.view.WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
                }
            }
        }
    }

    Box(Modifier.fillMaxSize().background(Color.Black)) {

        // 底层：全屏视频。视图从布局充气（surface_type=texture_view）——
        // SurfaceView 的画面在系统合成器里是独立图层，缩放/平移对它无效，
        // 双指放大就完全没反应；TextureView 画在普通 View 画布上，View 的
        // scale/translation 能真正作用到画面上。代价是多一次 GPU 拷贝。
        //
        // 外面套 RotatingPlayerFrame：90/270 时它把播放视图的布局尺寸宽高互换再转，
        // 这样 AspectRatioFrameLayout 的 FIT/Cover 是在"转过去的坐标系"里算的，
        // 否则只加 rotation 会把画面裁掉大半（见该类注释）。
        AndroidView(
            factory = { ctx ->
                RotatingPlayerFrame(ctx).apply {
                    playerView.apply {
                        useController = false
                        setPlayer(player)
                    }
                }
            },
            update = { frame ->
                val view = frame.playerView
                // 捏合时这个 update 每帧都会跑：只在播放器实例真的换了才 setPlayer
                if (view.player !== player) view.setPlayer(player)
                // Fit/Cover 由控制层的按钮切换（横竖屏各有一个，共用这一份状态）
                view.resizeMode = if (cover) {
                    androidx.media3.ui.AspectRatioFrameLayout.RESIZE_MODE_ZOOM
                } else {
                    androidx.media3.ui.AspectRatioFrameLayout.RESIZE_MODE_FIT
                }
                frame.rotationDeg = rotation
                // 双指缩放/平移直接落在 View 上（不是 graphicsLayer）：
                // AndroidView 内嵌的是真实 View，用 View 变换最稳，也省一层合成。
                // translation 在父坐标系里、排在旋转之后，所以角度变了也不用换算平移量。
                val z = zoom.value
                view.scaleX = z.scale
                view.scaleY = z.scale
                view.translationX = z.offset.x
                view.translationY = z.offset.y
            },
            modifier = Modifier.fillMaxSize(),
        )

        // 视频**本条还没出过画面**时，用首帧垫在控制层下（与播放画面同一位置，避免跳变）。
        // 判定用 readyOnceFor（这条是否已经 ready 过），不是 playerReady：
        // 后者在 seek/重缓冲时会短暂变 false，导致放大着的画面被 1x 的缩略图顶掉。
        if (readyOnceFor != item?.id && item != null && (playError ?: loadError) == null) {
            ThumbImage(item = item, vm = vm, modifier = Modifier.fillMaxSize(), fitAlign = true, cover = cover)
        }

        // 竖向 Pager：当前页透明露出视频，其余页显示海报缩略图。
        // 横屏锁页：竖滑要让位给亮度/音量手势，翻页走底部 上一个/下一个 按钮。
        // 横滑不在这里处理——交给 FeedGestureLayer 拖进度（「我的」改走顶栏按钮）。
        VerticalPager(
            state = pagerState,
            userScrollEnabled = !isLandscape,
            modifier = Modifier.fillMaxSize(),
        ) { page ->
            val pageItem = items.getOrNull(page)
            val isCurrent = page == settled
            if (!isCurrent && pageItem != null) {
                // 邻页 = 首帧按播放画面几何铺放（无暗罩、无文字），
                // 滑动时看起来是"画面滑过来"，落位后视频从首帧接上。
                // 刻意不放时长角标：角标随页位移会"从上往下滑"，非常出戏。
                ThumbImage(item = pageItem, vm = vm, modifier = Modifier.fillMaxSize(), fitAlign = true, cover = cover)
            } else {
                // 当前页：单个根 Box 承载手势层。LazyLayout item 里放多个根节点，
                // 测量/摆放行为不可靠（曾有节点不参与命中测试），统一收成一个根。
                Box(Modifier.fillMaxSize()) {
                    // 手势层必须放在**当前页内部**，不能做成 Pager 的兄弟浮层：
                    // Compose 的兄弟节点默认不共享触摸（sharePointerInputWithSiblings=false），
                    // 上层浮层一旦被命中，下面的 Pager 就收不到事件、竖滑直接失效。
                    // 放在页内则 Pager 是祖先——祖先永远在命中路径里，横滑给我们、竖滑照常翻页。
                    if (isCurrent && pageItem != null) {
                        FeedGestureLayer(
                            player = player,
                            modifier = Modifier.fillMaxSize(),
                            verticalControls = isLandscape,
                            zoom = zoom,
                            onZoomChange = { zoom.value = it },
                            onTap = { uiHidden = !uiHidden },
                            // 双击暂停/继续：横竖屏都在这一层，行为一致
                            onDoubleTap = {
                                player.playWhenReady = !player.playWhenReady
                                tapFeedback = if (player.playWhenReady) 2 else 1
                            },
                        )
                    }
                }
            }
        }

        // 邻页首帧预取（当前 ±2），翻页时首帧已就绪
        LaunchedEffect(settled, items) {
            if (items.isNotEmpty()) vm.prefetchThumbs(items, settled)
        }

        // 顶部提示栈：边界提示 / 源不可达 / 没有更多，各是各的胶囊、各走各的倒计时。
        // 以前把"不可达"和"没有更多"拼成一句（"X 不可达 · 没有更多了"），
        // 看起来像同一件事的补充说明；其实前者是"这个源连不上"，后者是"后面没有别的源了"。
        val topHints = listOfNotNull(edgeHint, blockedHint, noMoreHint)
        if (topHints.isNotEmpty() && !isHidden) {
            Column(
                Modifier
                    .fillMaxSize()
                    .statusBarsPadding()
                    .padding(top = 54.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                topHints.forEach { hint ->
                    Text(
                        hint,
                        color = Color.White,
                        fontSize = 12.sp,
                        fontWeight = FontWeight.SemiBold,
                        modifier = Modifier
                            .background(Color.Black.copy(alpha = 0.75f), RoundedCornerShape(999.dp))
                            .padding(horizontal = 14.dp, vertical = 8.dp),
                    )
                }
            }
        }

        if (items.isEmpty()) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                if (loading) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        CircularProgressIndicator(color = Color(0xFFFB7299))
                        Spacer(Modifier.height(10.dp))
                        Text("正在加载信息流…", color = Color(0xFF8B919C), fontSize = 12.sp)
                    }
                } else {
                    EmptyFeedNotice(
                        filter = filter,
                        hasServer = servers.isNotEmpty(),
                        onAddSource = onAddSource,
                        onOpenDrawer = onOpenDrawer,
                    )
                }
            }
        }

        // ── 控制层（当前页 & 未隐藏时） ─────────────────────
        if (item != null) {
            AnimatedVisibility(
                visible = !isHidden,
                enter = fadeIn(),
                exit = fadeOut(),
                modifier = Modifier.fillMaxSize(),
            ) {
                if (isLandscape) {
                    LandscapeControls(
                        items = items,
                        pagerState = pagerState,
                        player = player,
                        title = topTitle(item, filter),
                        playing = playing,
                        playbackSpeed = playbackSpeed,
                        onSpeedChange = {
                            player.playbackParameters = player.playbackParameters.withSpeed(it)
                            playbackSpeed = it
                        },
                        liked = item.id in likedIds,
                        watched = item.id in watchedIds,
                        cover = cover,
                        onToggleCover = { cover = !cover; vm.coverFit = cover },
                        rotation = rotation,
                        onRotate = { rotateCurrent() },
                        playMode = playMode,
                        onCycleMode = { playMode = playMode.next() },
                        fullscreen = fullscreen,
                        onOpenDrawer = onOpenDrawer,
                        onOpenMine = onOpenMine,
                        onOpenSearch = onOpenSearch,
                        onOpenCollection = { onOpenCollection(item) },
                        cacheable = item.isRemote,
                        onCache = { scope.launch { vm.cacheItem(item) } },
                        onToggleLiked = {
                            scope.launch {
                                val nowLiked = vm.toggleFavorite(item)
                                if (nowLiked == null) vm.toggleLiked(item) // 离线/本地条目退回本地标记
                            }
                        },
                        onToggleWatched = { vm.toggleWatched(item) },
                        onToggleFullscreen = { onFullscreenChange(!fullscreen) },
                        sliderPos = sliderPos,
                        sliderDur = sliderDur,
                        onSeekPreview = {
                            dragging = true
                            sliderPos = it
                        },
                        onSeekFinished = { v ->
                            // 用回调给的终值，不读 sliderPos（同一帧内可能是旧的）
                            player.seekTo(v.toLong())
                            sliderPos = v
                            dragging = false
                        },
                    )
                } else {
                Box(Modifier.fillMaxSize()) {
                    // 顶栏：菜单 + 标题 …… 我的
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .background(Color.Black.copy(alpha = 0.35f))
                            .blockTouchPassThrough()
                            .statusBarsPadding()
                            .padding(horizontal = 14.dp, vertical = 10.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Icon(
                            Icons.Default.Menu,
                            contentDescription = "菜单",
                            tint = Color.White,
                            modifier = Modifier
                                .size(26.dp)
                                .clickable { onOpenDrawer() },
                        )
                        Spacer(Modifier.width(12.dp))
                        // 连播中：位置号做成**独立的小胶囊放在最左**，合集名跟在后面被省略。
                        // 反过来（"连播 · UP 主 · monamisa_zeco 1/2"）名字一长就把 1/2 吃掉——
                        // 位置号才是"我确实在这个夹子里翻"的证据，不能被挤掉。
                        if (queue != null) {
                            Text(
                                "${settled + 1}/${items.size}",
                                color = Color(0xFFFB7299),
                                fontWeight = FontWeight.Bold,
                                fontSize = 12.sp,
                                modifier = Modifier
                                    .background(Color(0xFFFB7299).copy(alpha = 0.18f), RoundedCornerShape(999.dp))
                                    .padding(horizontal = 8.dp, vertical = 3.dp),
                            )
                            Spacer(Modifier.width(8.dp))
                        }
                        // weight(1f) 撑满剩余宽度 —— 不然「我的」会紧贴标题，
                        // 而不是像左端菜单那样贴在栏的右边缘
                        Text(
                            queue?.let { "连播 · ${it.label}" } ?: topTitle(item, filter),
                            color = Color.White,
                            fontWeight = FontWeight.SemiBold,
                            fontSize = 14.sp,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.weight(1f),
                        )
                        // 退出连播：回到信息流（返回手势同样有效，但手势没法被发现）
                        if (queue != null) {
                            Icon(
                                Icons.Default.Close,
                                contentDescription = "退出连播",
                                tint = Color.White,
                                modifier = Modifier
                                    .size(30.dp)
                                    .clickable { onExitQueue() }
                                    .padding(6.dp),
                            )
                            Spacer(Modifier.width(4.dp))
                        }
                        Icon(
                            Icons.Default.Search,
                            contentDescription = "搜索",
                            tint = Color.White,
                            modifier = Modifier
                                .size(32.dp)
                                .clickable { onOpenSearch() }
                                .padding(5.dp),
                        )
                        Icon(
                            Icons.Default.Person,
                            contentDescription = "我的",
                            tint = Color.White,
                            modifier = Modifier
                                .size(32.dp)
                                .clickable { onOpenMine() }
                                .padding(5.dp),
                        )
                    }

                    // 底部信息卡片的高度（右侧按钮列要贴着它的上沿，必须先量出来）
                    var bottomBarH by remember { mutableStateOf(0) }

                    /**
                     * 播放设置小面板的展开状态（适应 / 旋转 / 播放模式）。
                     *
                     * 这三项都是"偶尔调一次"的画面设置，原来和合集/收藏/看过平铺在右侧栏里，
                     * 6 个圆钮竖着排下来快压到底部卡片。收进「设置」之后右侧栏只剩 5 个高频入口。
                     * UI 隐藏（点屏幕）时面板一起收，否则下次唤出界面它还开着。
                     */
                    var showSettings by remember { mutableStateOf(false) }
                    LaunchedEffect(isHidden) { if (isHidden) showSettings = false }

                    // 点面板以外的任何地方 = 收起。全屏透明遮罩垫在顶栏/按钮列/底部卡片**之前**，
                    // 后画的先命中，所以那三块仍然正常可点，只有空白处被这里吃掉。
                    if (showSettings) {
                        Box(
                            Modifier
                                .fillMaxSize()
                                .clickable(
                                    interactionSource = remember { MutableInteractionSource() },
                                    indication = null,
                                ) { showSettings = false }
                        )
                    }

                    // 右侧操作栏：合集 / 收藏 / 看过 / 设置 / 全屏。
                    // 位置由底部卡片实测高度决定——原来写死 140dp，卡片一改高就错位。
                    val railBottom =
                        with(androidx.compose.ui.platform.LocalDensity.current) { bottomBarH.toDp() } + 10.dp
                    Row(
                        Modifier
                            .align(Alignment.BottomEnd)
                            .padding(end = 10.dp, bottom = railBottom),
                        verticalAlignment = Alignment.Bottom,
                    ) {
                        // 面板挂在按钮列左侧、与列底对齐：竖屏右边就是屏幕边缘，
                        // 往上顶会撞顶栏，往左铺开是最不容易挡画面的一块地方。
                        AnimatedVisibility(
                            visible = showSettings,
                            enter = fadeIn(),
                            exit = fadeOut(),
                        ) {
                            PlaySettingsPanel(
                                cover = cover,
                                onToggleCover = { cover = !cover; vm.coverFit = cover },
                                rotation = rotation,
                                onRotate = { rotateCurrent() },
                                mode = playMode,
                                onCycleMode = { playMode = playMode.next() },
                                modifier = Modifier.padding(end = 10.dp, bottom = 4.dp),
                                cacheable = item.isRemote,
                                onCache = { scope.launch { vm.cacheItem(item) } },
                            )
                        }
                        Column(
                            Modifier.blockTouchPassThrough(),
                            horizontalAlignment = Alignment.CenterHorizontally,
                            verticalArrangement = Arrangement.spacedBy(14.dp),
                        ) {
                            RailButton(Icons.Default.CollectionsBookmark, "合集") {
                                onOpenCollection(item)
                            }
                            RailButton(
                                if (item.id in likedIds) Icons.Default.Favorite else Icons.Default.FavoriteBorder,
                                if (item.id in likedIds) "已收藏" else "收藏",
                                active = item.id in likedIds,
                            ) {
                                scope.launch {
                                    val nowLiked = vm.toggleFavorite(item)
                                    if (nowLiked == null) vm.toggleLiked(item)
                                }
                            }
                            RailButton(
                                Icons.Default.CheckCircle,
                                if (item.id in watchedIds) "已看过" else "看过",
                                active = item.id in watchedIds,
                            ) { vm.toggleWatched(item) }
                            // 设置：展开「适应 / 旋转 / 播放模式」小面板（再点一次收起）
                            RailButton(
                                Icons.Default.Settings,
                                "设置",
                                active = showSettings,
                            ) { showSettings = !showSettings }
                            RailButton(
                                if (fullscreen) Icons.Default.FullscreenExit else Icons.Default.Fullscreen,
                                if (fullscreen) "退出全屏" else "全屏",
                                active = fullscreen,
                            ) { onFullscreenChange(!fullscreen) }
                        }
                    }

                    // 底部信息卡片：标题单行 + 元信息单行 + 细进度条。
                    // 标题/标签不再各占一行，超长一律省略号截断——卡片整体矮一半，
                    // 把画面让出来，进度条也从 Material Slider 换成 3dp 细条。
                    Column(
                        Modifier
                            .align(Alignment.BottomCenter)
                            .fillMaxWidth()
                            .onSizeChanged { bottomBarH = it.height }
                            .background(Color.Black.copy(alpha = 0.35f))
                            .blockTouchPassThrough()
                            .navigationBarsPadding()
                            .padding(horizontal = 16.dp, vertical = 8.dp),
                    ) {
                        Text(
                            item.title,
                            color = Color.White,
                            fontWeight = FontWeight.Bold,
                            fontSize = 14.5.sp,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                        Spacer(Modifier.height(3.dp))
                        Text(
                            buildString {
                                append(item.collectionName ?: "本地视频")
                                append(" · ")
                                append(formatDuration(item.durationMs))
                                if (item.sizeBytes > 0) append(" · ").append(formatSize(item.sizeBytes))
                                resLabel(item)?.let { append(" · ").append(it) }
                                if (item.layout == "dual") append(" · 双流")
                            },
                            color = Color.White.copy(alpha = 0.7f),
                            fontSize = 11.sp,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                        Spacer(Modifier.height(4.dp))
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(
                                formatDuration(sliderPos.toLong()),
                                color = Color.White.copy(alpha = 0.75f),
                                fontSize = 10.5.sp,
                            )
                            ThinProgressBar(
                                pos = sliderPos,
                                dur = sliderDur,
                                onSeek = {
                                    dragging = true
                                    sliderPos = it
                                },
                                onSeekFinished = { v ->
                                    // 用回调终值，不读 sliderPos（同一帧可能是旧值）
                                    player.seekTo(v.toLong())
                                    sliderPos = v
                                    dragging = false
                                },
                                modifier = Modifier
                                    .weight(1f)
                                    .padding(horizontal = 8.dp),
                            )
                            Text(
                                formatDuration(sliderDur.toLong()),
                                color = Color.White.copy(alpha = 0.75f),
                                fontSize = 10.5.sp,
                            )
                        }
                    }
                }
                }
            }

            // UI 隐藏后不再留任何浮层按钮：点屏幕即可唤出（横竖屏一致），
            // 「恢复原比例」也不例外——藏在 UI 后面唤出即可复位。
        }

        // ── 双击暂停的中央反馈 ──────────────────────────────
        // 暂停后画面是静止的，与"卡死了"在观感上没有区别，必须给一下明确回执。
        if (tapFeedback != 0) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Icon(
                    if (tapFeedback == 1) Icons.Default.Pause else Icons.Default.PlayArrow,
                    contentDescription = null,
                    tint = Color.White,
                    modifier = Modifier
                        .size(78.dp)
                        .background(Color.Black.copy(alpha = 0.45f), RoundedCornerShape(999.dp))
                        .padding(20.dp),
                )
            }
        }

        // ── 恢复原比例 ─────────────────────────────────────
        // 处于缩放/平移状态且界面可见时显示，位置在屏幕中靠下、
        // 压在底部信息卡/横屏控制条之上——不挡标题、不挡进度条。
        RestoreZoomButton(
            zoom = zoom,
            hidden = isHidden,
            bottomPadding = if (isLandscape) 96.dp else 126.dp,
            onReset = { zoom.value = VideoZoom() },
        )

        // 加载态：解析中或缓冲中。指示延迟 700ms 才出现——视频在阈值内就绪就什么都不显示，
        // 保持滑动连贯；超时未就绪才浮出加载圈。
        val displayError = playError ?: loadError
        val busy = (resolving || buffering) && displayError == null && items.isNotEmpty()
        var busyLong by remember { mutableStateOf(false) }
        val busyState = androidx.compose.runtime.rememberUpdatedState(busy)
        LaunchedEffect(busy) {
            if (busy) {
                delay(700)
                if (busyState.value) busyLong = true
            } else {
                busyLong = false
            }
        }

        // 切源/跨源探活都会把 sourceSwitching 打开（声明在上面的媒体 effect 之前），
        // 这里只负责"切过滤器 → 打开、真正就绪 → 关上"
        LaunchedEffect(filter) { sourceSwitching = true }
        LaunchedEffect(playerReady) { if (playerReady) sourceSwitching = false }

        if (busyLong) {
            Column(
                horizontalAlignment = Alignment.CenterHorizontally,
                modifier = Modifier.align(Alignment.Center),
            ) {
                CircularProgressIndicator(color = Color(0xFFFB7299), modifier = Modifier.size(40.dp))
                Spacer(Modifier.height(10.dp))
                if (sourceSwitching) {
                    Text(
                        "正在连接 ${item?.sourceName ?: "服务器"}…",
                        color = Color.White,
                        fontSize = 12.5.sp,
                    )
                }
                // 远程视频加载慢时显示实时网速
                if (item?.isRemote == true) {
                    Text(speedText ?: " ", color = Color(0xFF8B919C), fontSize = 11.sp)
                }
            }
        }
        displayError?.let { err ->
            Column(
                horizontalAlignment = Alignment.CenterHorizontally,
                modifier = Modifier
                    .align(Alignment.Center)
                    .background(Color.Black.copy(alpha = 0.7f), RoundedCornerShape(12.dp))
                    .padding(horizontal = 18.dp, vertical = 12.dp),
            ) {
                Text("播放失败：$err", color = Color.White, fontSize = 13.sp)
                Spacer(Modifier.height(8.dp))
                Text(
                    "重试",
                    color = Color(0xFFFB7299),
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier
                        .clickable { resolveTick++ }
                        .padding(horizontal = 12.dp, vertical = 4.dp),
                )
            }
        }

        // 转封装提示（不是错误）：出现在底部信息卡上方，几秒后自然被切条目清掉
        if (displayError == null) remuxNote?.let { note ->
            Text(
                note,
                color = Color.White,
                fontSize = 12.sp,
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .padding(bottom = 168.dp)
                    .background(Color.Black.copy(alpha = 0.65f), RoundedCornerShape(10.dp))
                    .padding(horizontal = 14.dp, vertical = 7.dp),
            )
        }
    }
}

private fun topTitle(item: FeedItem, filter: FeedFilter): String {
    val source = item.sourceName ?: return filterTitle(filter)
    return item.collectionName?.let { "$source：$it" } ?: source
}

private fun filterTitle(filter: FeedFilter): String = when (filter) {
    FeedFilter.All -> "MediaRack"
    FeedFilter.Local -> "本地缓存"
    is FeedFilter.Server -> filter.name
    FeedFilter.Liked -> "收藏夹连播"
}

/**
 * 空信息流。这个页面**必须存在**——四种完全不同的"没有东西可播"都落到这里：
 *
 * 1. 一次都没配过源（新装）：提示去添加服务器；
 * 2. 配了源但都还没扫到条目 / 全部离线且无缓存：等扫描即可；
 * 3. 某个源里确实 0 条（切过去才发现的，探活只证明服务活着）；
 * 4. 收藏夹连播里一条没收藏 / 本地缓存里还没下载任何东西。
 *
 * 之前的文案对所有情况都写"右滑打开侧边栏添加源"，2/3/4 全都在骗人。
 */
@Composable
private fun EmptyFeedNotice(
    filter: FeedFilter,
    hasServer: Boolean,
    onAddSource: () -> Unit,
    onOpenDrawer: () -> Unit,
) {
    val title: String
    val sub: String
    when {
        filter is FeedFilter.Liked -> {
            title = "还没有收藏的视频"
            sub = "在播放页点「收藏」，就会出现在这里"
        }
        filter is FeedFilter.Local -> {
            title = "本地还没有缓存视频"
            sub = "下载到设备后，本地缓存里就能直接播"
        }
        filter is FeedFilter.Server -> {
            title = "「${filter.name}」里还没有条目"
            sub = "这个源里还没有可播的视频"
        }
        hasServer -> {
            title = "源里都还没有条目"
            sub = "服务器扫描完成后会自动出现在这里"
        }
        else -> {
            title = "还没有任何源"
            sub = "添加一台服务器，或先把视频放进手机目录"
        }
    }
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Text(title, color = Color(0xFFECEEF2), fontWeight = FontWeight.SemiBold, fontSize = 15.sp)
        Spacer(Modifier.height(8.dp))
        Text(sub, color = Color(0xFF8B919C), fontSize = 12.sp, textAlign = TextAlign.Center)
        Spacer(Modifier.height(16.dp))
        // 空态下控制层整层不渲染（没有当前条目就没有顶栏），
        // 必须在这里留一个去抽屉的入口——否则"收藏夹为空"的页面上
        // 连菜单按钮都没有，只剩边缘手势，等于出不去。
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (!hasServer) {
                Button(onClick = onAddSource) { Text("添加服务器") }
                Spacer(Modifier.width(6.dp))
            }
            TextButton(onClick = onOpenDrawer) { Text("打开源列表", color = Color(0xFFFB7299)) }
        }
    }
}

/** 播放日志 tag：真机排障 `adb logcat -s MediaRackPlay` 只看这一路。 */
private const val TAG_PLAY = "MediaRackPlay"

/**
 * ExoPlayer 异步错误 → 人话（不映射就一直黑屏无提示）。
 *
 * 顺序很重要：**先看 cause 链再看错误码**。HTTP 状态码、超时、文件缺失全都埋在
 * cause 里，而它们的 errorCode 一律是 2000（IO_UNSPECIFIED）——只看码就会显示
 * 一句没有任何信息量的「播放出错（code=2000）」。
 */
private fun playbackErrorText(e: PlaybackException): String {
    var cause: Throwable? = e.cause
    var depth = 0
    while (cause != null && depth++ < 10) {
        when (cause) {
            is androidx.media3.datasource.HttpDataSource.InvalidResponseCodeException ->
                when (val code = cause.responseCode) {
                    401, 403 -> return "令牌不正确或已过期（HTTP $code）"
                    404 -> return "服务端上没有这个文件（HTTP 404）"
                    in 500..599 -> return "服务端处理出错（HTTP $code）"
                    else -> return "服务端返回 HTTP $code"
                }
            is java.net.SocketTimeoutException -> return "读取超时（网络太慢或服务端卡住）"
            is java.net.ConnectException -> return "连不上服务器（地址/端口不可达）"
            is java.net.UnknownHostException -> return "服务器地址解析失败"
            is java.io.FileNotFoundException -> return "本地文件不存在（可能已被移动或删除）"
        }
        cause = cause.cause
    }

    return when (e.errorCode) {
        PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED,
        PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT -> "网络连接失败，请检查服务器可达性"
        PlaybackException.ERROR_CODE_IO_FILE_NOT_FOUND -> "本地文件不存在（可能已被移动或删除）"
        PlaybackException.ERROR_CODE_IO_READ_POSITION_OUT_OF_RANGE -> "文件读取越界（文件可能不完整）"
        PlaybackException.ERROR_CODE_DECODER_INIT_FAILED,
        PlaybackException.ERROR_CODE_DECODING_FAILED,
        PlaybackException.ERROR_CODE_DECODING_FORMAT_EXCEEDS_CAPABILITIES -> "解码失败（编码格式不受支持）"
        else -> {
            val msg = e.message ?: e.cause?.message
            if (msg.isNullOrBlank()) "播放出错（code=${e.errorCode}）"
            else "播放出错（code=${e.errorCode}）：$msg"
        }
    }
}

@Composable
private fun RailButton(icon: ImageVector, label: String, active: Boolean = false, onClick: () -> Unit) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Icon(
            icon,
            contentDescription = label,
            tint = if (active) Color(0xFFFB7299) else Color.White,
            modifier = Modifier
                .size(44.dp)
                // clip 必须在 background/clickable 之前：clickable 的水波纹不认识圆角，
                // 不裁的话按下时方形涟漪会从圆钮外露出来
                .clip(CircleShape)
                .background(Color.White.copy(alpha = 0.14f), CircleShape)
                .clickable { onClick() }
                .padding(10.dp),
        )
        Spacer(Modifier.height(4.dp))
        Text(
            label,
            color = if (active) Color(0xFFFB7299) else Color.White.copy(alpha = 0.85f),
            fontSize = 10.5.sp,
        )
    }
}

/**
 * 「恢复原比例」：双指缩放/平移后出现在屏幕中靠下处，点一下回到原始比例。
 *
 * 跟其它浮层一样受 UI 隐藏约束：单击视频藏界面时一起藏（想复位就先点屏幕唤出 UI）。
 * 单独成 composable 是为了把 `zoom.value` 的**读**关在这里：
 * 放在 FeedContent 里读，捏合时整个信息流每帧都会重组（见 zoom 声明处注释）。
 */
@Composable
private fun RestoreZoomButton(
    zoom: State<VideoZoom>,
    hidden: Boolean,
    bottomPadding: Dp,
    onReset: () -> Unit,
) {
    if (hidden || zoom.value.isDefault) return
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.BottomCenter) {
        Text(
            "恢复原比例",
            color = Color.White,
            fontSize = 12.sp,
            fontWeight = FontWeight.SemiBold,
            modifier = Modifier
                .navigationBarsPadding()
                .padding(bottom = bottomPadding)
                .background(Color.Black.copy(alpha = 0.66f), RoundedCornerShape(999.dp))
                .clickable { onReset() }
                .padding(horizontal = 16.dp, vertical = 9.dp),
        )
    }
}

private fun resLabel(item: FeedItem): String? = when {
    item.width >= 3800 || item.height >= 2100 -> "4K"
    item.width >= 2500 || item.height >= 1400 -> "2K"
    item.width >= 1900 || item.height >= 1000 -> "1080P"
    item.width > 0 -> "${item.width}P"
    else -> null
}

// ─────────────────────────────────────────────────────────────
// 网速测量：包装 DataSource 统计读取字节
// ─────────────────────────────────────────────────────────────

private object SpeedMeter {
    val bytes = AtomicLong(0)
}

@androidx.media3.common.util.UnstableApi
private class MeteredDataSource(private val up: androidx.media3.datasource.DataSource) :
    androidx.media3.datasource.DataSource {

    override fun addTransferListener(transferListener: androidx.media3.datasource.TransferListener) {
        up.addTransferListener(transferListener)
    }

    override fun open(dataSpec: androidx.media3.datasource.DataSpec): Long = up.open(dataSpec)

    override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
        val n = up.read(buffer, offset, length)
        if (n > 0) SpeedMeter.bytes.addAndGet(n.toLong())
        return n
    }

    override fun getUri(): android.net.Uri? = up.uri

    override fun close() {
        up.close()
    }
}

/** feed 用的数据源工厂：DefaultDataSource 路由 file/http + 外层字节计量。 */
@androidx.media3.common.util.UnstableApi
private fun feedDataSourceFactory(context: android.content.Context): androidx.media3.datasource.DataSource.Factory {
    val base = androidx.media3.datasource.DefaultDataSource.Factory(context, feedHttpDataSourceFactory())
    return androidx.media3.datasource.DataSource.Factory { MeteredDataSource(base.createDataSource()) }
}

/** feed 用的 HTTP 数据源：307 跨协议重定向 + Range（R1 探针同款）。file:// 由 DefaultDataSource 路由。 */
@androidx.media3.common.util.UnstableApi
private fun feedHttpDataSourceFactory(): androidx.media3.datasource.DefaultHttpDataSource.Factory =
    androidx.media3.datasource.DefaultHttpDataSource.Factory()
        .setUserAgent("MediaRack-Android/1.0")
        .setAllowCrossProtocolRedirects(true)
        .setConnectTimeoutMs(8000)
        .setReadTimeoutMs(15000)

// 首帧缩略图组件与缓存见 ThumbImage.kt（信息流与「我的」页共用）

// ─────────────────────────────────────────────────────────────
// 合集二级页（列表 / 1排2个 / 1排3个）——返回键回信息流
// ─────────────────────────────────────────────────────────────

@Composable
private fun CollectionPage(
    vm: FeedViewModel,
    origin: FeedItem,
    onBack: () -> Unit,
    /** 点一条：带上整份本地合集列表（连播队列）。 */
    onOpen: (FeedItem, PlayQueue) -> Unit,
) {
    var result by remember { mutableStateOf<List<FeedItem>?>(null) }
    var viewMode by remember { mutableStateOf(0) } // 0 列表 / 1 两个 / 2 三个

    LaunchedEffect(origin.id) {
        result = vm.collectionItems(origin)
    }

    Box(Modifier.fillMaxSize().background(Color(0xFF121419))) {
        Column(Modifier.fillMaxSize().statusBarsPadding()) {
            // 顶栏
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
                    origin.sourceName?.let { s ->
                        origin.collectionName?.let { "$s：$it" } ?: s
                    } ?: (origin.collectionName ?: "合集"),
                    color = Color.White,
                    fontWeight = FontWeight.Bold,
                    fontSize = 16.sp,
                    modifier = Modifier.weight(1f),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                // 视图切换
                Row(
                    horizontalArrangement = Arrangement.spacedBy(4.dp),
                    modifier = Modifier
                        .background(Color.White.copy(alpha = 0.08f), RoundedCornerShape(10.dp))
                        .padding(3.dp),
                ) {
                    listOf("列表", "2个", "3个").forEachIndexed { i, label ->
                        Text(
                            label,
                            color = if (viewMode == i) Color.White else Color(0xFF8B919C),
                            fontSize = 12.sp,
                            fontWeight = if (viewMode == i) FontWeight.Bold else FontWeight.Normal,
                            modifier = Modifier
                                .background(
                                    if (viewMode == i) Color(0xFF2A2E3A) else Color.Transparent,
                                    RoundedCornerShape(8.dp),
                                )
                                .clickable { viewMode = i }
                                .padding(horizontal = 12.dp, vertical = 6.dp),
                        )
                    }
                }
            }
            Spacer(Modifier.height(2.dp))
            Text(
                "点任意视频 = 从它开始连播本合集 · 返回键回到视频",
                color = Color(0xFF8B919C),
                fontSize = 11.sp,
                modifier = Modifier.padding(horizontal = 18.dp),
            )
            Spacer(Modifier.height(10.dp))

            val list = result
            when {
                list == null -> Box(Modifier.fillMaxWidth().height(120.dp), contentAlignment = Alignment.Center) {
                    CircularProgressIndicator(modifier = Modifier.size(26.dp), color = Color(0xFFFB7299))
                }
                list.isEmpty() -> Text(
                    "该条目没有合集信息（远程条目的合集信息待契约补充）",
                    color = Color(0xFF8B919C),
                    fontSize = 13.sp,
                    modifier = Modifier.padding(horizontal = 18.dp),
                )
                else -> when (viewMode) {
                    0 -> LazyColumn(
                        Modifier.fillMaxSize(),
                        contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 14.dp, vertical = 4.dp),
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        items(list, key = { it.id }) { it ->
                            CollectionRow(it, vm, origin.id) {
                                onOpen(it, PlayQueue("文件夹 · ${origin.collectionName ?: "合集"}", list))
                            }
                        }
                    }
                    else -> LazyVerticalGrid(
                        columns = GridCells.Fixed(if (viewMode == 1) 2 else 3),
                        Modifier.fillMaxSize(),
                        contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 14.dp, vertical = 4.dp),
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        gridItems(list, key = { it.id }) { it ->
                            CollectionGridCell(it, vm, origin.id) {
                                onOpen(it, PlayQueue("文件夹 · ${origin.collectionName ?: "合集"}", list))
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun CollectionRow(item: FeedItem, vm: FeedViewModel, currentId: String, onClick: () -> Unit) {
    val isCurrent = item.id == currentId
    Row(
        Modifier
            .fillMaxWidth()
            .background(Color(0xFF1B1E26), RoundedCornerShape(12.dp))
            .clickable { onClick() }
            .padding(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier
                .width(96.dp)
                .aspectRatio(16f / 10f)
                // Cover 铺满会画到框外，裁剪交给容器（ThumbImage 只负责缩放，不裁）
                .clip(RoundedCornerShape(8.dp))
                .background(Color(0xFF2A2E3A)),
        ) {
            ThumbImage(item = item, vm = vm, modifier = Modifier.fillMaxSize())
        }
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(
                item.title,
                color = if (isCurrent) Color(0xFFFB7299) else Color.White,
                fontWeight = if (isCurrent) FontWeight.Bold else FontWeight.SemiBold,
                fontSize = 13.5.sp,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            Spacer(Modifier.height(3.dp))
            Text(
                formatDuration(item.durationMs) + if (item.sizeBytes > 0) " · ${formatSize(item.sizeBytes)}" else "",
                color = Color(0xFF8B919C),
                fontSize = 11.sp,
            )
        }
        if (isCurrent) {
            Spacer(Modifier.width(8.dp))
            Icon(Icons.Default.PlayArrow, contentDescription = null, tint = Color(0xFFFB7299), modifier = Modifier.size(18.dp))
        }
    }
}

@Composable
private fun CollectionGridCell(item: FeedItem, vm: FeedViewModel, currentId: String, onClick: () -> Unit) {
    val isCurrent = item.id == currentId
    Column(
        Modifier
            .background(Color(0xFF1B1E26), RoundedCornerShape(12.dp))
            .clickable { onClick() }
            .padding(8.dp),
    ) {
        Box(
            Modifier
                .fillMaxWidth()
                .aspectRatio(16f / 9f)
                .clip(RoundedCornerShape(8.dp))
                .background(Color(0xFF2A2E3A)),
        ) {
            ThumbImage(item = item, vm = vm, modifier = Modifier.fillMaxSize())
        }
        Spacer(Modifier.height(6.dp))
        Text(
            item.title,
            color = if (isCurrent) Color(0xFFFB7299) else Color.White,
            fontWeight = if (isCurrent) FontWeight.Bold else FontWeight.SemiBold,
            fontSize = 12.sp,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
        )
        Spacer(Modifier.height(2.dp))
        Text(formatDuration(item.durationMs), color = Color(0xFF8B919C), fontSize = 10.5.sp)
    }
}

// ─────────────────────────────────────────────────────────────
// 右滑抽屉：源选择
// ─────────────────────────────────────────────────────────────

@Composable
private fun FeedDrawer(
    vm: FeedViewModel,
    filter: FeedFilter,
    onSelect: () -> Unit,
    onAddSource: () -> Unit,
    onEditServer: (io.github.garfieldgod.mediarack.core.db.ServerEntity) -> Unit,
    onOpenDownloads: () -> Unit,
    /** 某个源的「查看全部合集」：合集是源级的，所以入口挂在每一个源行上。 */
    onOpenSourceCollections: (io.github.garfieldgod.mediarack.core.db.ServerEntity) -> Unit,
) {
    val servers by vm.servers.collectAsState(initial = emptyList())
    val localCount by vm.localCount.collectAsState()

    ModalDrawerSheet(drawerContainerColor = Color(0xFF1B1E26)) {
        // verticalScroll：源多了能滚（竖屏也受益，横屏矮屏必备）
        Column(
            Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 14.dp)
        ) {
            Spacer(Modifier.height(40.dp))

            // 标题行：「添加源」贴右侧，不再单独占一行（扫码入口在弹窗标题行里）
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("MediaRack", color = Color.White, fontWeight = FontWeight.Bold, fontSize = 18.sp)
                Spacer(Modifier.weight(1f))
                DrawerAction("＋ 添加源", filled = true) { onAddSource() }
            }
            Spacer(Modifier.height(14.dp))

            DrawerRow(
                label = "全部",
                sub = null,
                selected = filter is FeedFilter.All,
                icon = Icons.Default.Folder,
            ) { vm.setFilter(FeedFilter.All); onSelect() }

            // 合集与收藏都**不在这里**：
            // - 合集是源级的结构层，入口在下面每个源行尾（编辑铅笔左侧的「合集」）；
            // - 收藏是用户层、跨源跨端的，「我的」页给「收藏的视频 / 收藏的合集」两个入口。
            // 抽屉只负责"选源"，混进组织层的东西会让人以为收藏是某个源的属性。

            // 单一源列表：第一项固定「本地缓存」（所有本地源合一），其余都是远程源。
            // 行尾：远程源是 [合集][铅笔]（看合集 / 改信息），本地缓存是「管理」（进已下载管理）。
            DrawerRow(
                label = "本地缓存",
                sub = "$localCount 条目",
                selected = filter is FeedFilter.Local,
                icon = Icons.Default.Folder,
                trailingLabel = "管理",
                onTrailing = onOpenDownloads,
            ) { vm.setFilter(FeedFilter.Local); onSelect() }

            // 服务器分组渲染：一条配置 = 一个源之后，同一台机器会有多个源
            // （一台电脑建了 movie / music 两个源就是两行）。按 host:port 分组，
            // 组头是机器地址，组内每行是一个源——跟服务端管理页的层级一致。
            servers.groupBy { hostPortLabel(it.baseUrl) }.forEach { (hostPort, list) ->
                Row(
                    Modifier.padding(start = 4.dp, top = 10.dp, bottom = 2.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Icon(
                        Icons.Default.Dns,
                        contentDescription = null,
                        tint = Color(0xFF8B919C),
                        modifier = Modifier.size(14.dp),
                    )
                    Spacer(Modifier.width(6.dp))
                    Text(hostPort, color = Color(0xFF8B919C), fontSize = 11.sp)
                }
                list.forEach { s ->
                    DrawerRow(
                        label = s.name,
                        sub = sourcePathLabel(s.baseUrl),
                        selected = filter is FeedFilter.Server && filter.serverId == s.id,
                        icon = Icons.Default.CollectionsBookmark,
                        // 「合集」在编辑铅笔左侧：先看结构、再改信息
                        trailingLabel = "合集",
                        onTrailing = { onOpenSourceCollections(s) },
                        onEdit = { onEditServer(s) },
                    ) { vm.selectSource(FeedFilter.Server(s.id, s.name)); onSelect() }
                }
            }

            Spacer(Modifier.height(12.dp))
        }
    }
}

/** `http://192.0.2.10:8080/s/movie` → `192.0.2.10:8080`（抽屉分组头）。 */
private fun hostPortLabel(baseUrl: String): String {
    val (h, p, _) = ApiClient.splitSourceBaseUrl(baseUrl)
    return if (p.isEmpty()) h else "$h:$p"
}

/** base URL 里的源地址路径 → 副标题 `源地址 /s/movie`；老版本配置（无路径）回退完整地址。 */
private fun sourcePathLabel(baseUrl: String): String? {
    val path = ApiClient.splitSourceBaseUrl(baseUrl).third
    return if (path.isEmpty()) baseUrl else "源地址 /$path"
}

@Composable
private fun DrawerAction(label: String, filled: Boolean, modifier: Modifier = Modifier, onClick: () -> Unit) {
    Text(
        label,
        color = if (filled) Color.White else Color(0xFFECEEF2),
        fontSize = 13.sp,
        fontWeight = FontWeight.SemiBold,
        modifier = modifier
            .background(
                if (filled) Color(0xFFFB7299) else Color.White.copy(alpha = 0.08f),
                RoundedCornerShape(12.dp),
            )
            .clickable { onClick() }
            .padding(horizontal = 14.dp, vertical = 9.dp),
        textAlign = TextAlign.Center,
    )
}

@Composable
private fun DrawerRow(
    label: String,
    sub: String?,
    selected: Boolean,
    icon: ImageVector,
    onEdit: (() -> Unit)? = null,
    /** 行尾文字按钮：本地缓存是「管理」、远程源是「合集」；与 [onEdit] 可同时使用（合集在铅笔左侧）。 */
    trailingLabel: String? = null,
    onTrailing: (() -> Unit)? = null,
    onClick: () -> Unit,
) {
    Row(
        Modifier
            .fillMaxWidth()
            .background(
                if (selected) Color(0xFFFB7299).copy(alpha = 0.14f) else Color.Transparent,
                RoundedCornerShape(12.dp),
            )
            .clickable { onClick() }
            .padding(
                start = 10.dp,
                end = when {
                    onEdit != null -> 2.dp
                    trailingLabel != null -> 8.dp
                    else -> 10.dp
                },
                top = 11.dp,
                bottom = 11.dp,
            ),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            icon,
            contentDescription = null,
            tint = if (selected) Color(0xFFFB7299) else Color(0xFF8B919C),
            modifier = Modifier.size(20.dp),
        )
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(label, color = Color.White, fontSize = 14.sp, fontWeight = FontWeight.SemiBold)
            sub?.let {
                Text(it, color = Color(0xFF8B919C), fontSize = 11.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        if (trailingLabel != null && onTrailing != null) {
            // 自己是 clickable，点它不会冒泡到整行（不会顺手切换 filter）
            Text(
                trailingLabel,
                color = Color(0xFFFB7299),
                fontSize = 11.5.sp,
                fontWeight = FontWeight.SemiBold,
                modifier = Modifier
                    .background(Color(0xFFFB7299).copy(alpha = 0.16f), RoundedCornerShape(999.dp))
                    .clickable { onTrailing() }
                    .padding(horizontal = 12.dp, vertical = 6.dp),
            )
        }
        onEdit?.let {
            IconButton(onClick = it, modifier = Modifier.size(36.dp)) {
                Icon(
                    Icons.Default.Edit,
                    contentDescription = "编辑源",
                    tint = Color(0xFF8B919C),
                    modifier = Modifier.size(18.dp),
                )
            }
        }
    }
}
