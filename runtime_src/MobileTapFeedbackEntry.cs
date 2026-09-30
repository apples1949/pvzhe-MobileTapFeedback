using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「点击反馈与自动拾取」Mod 的托管运行时入口。
///
/// ★ 本 Mod 是从「手机操作优化」（MobileUXFixes）**拆出来**的：原 Mod 只保留
///   ①选卡界面滑动误选撤销 / ②战斗中误触取消；原来的 ③④ 搬到这里独立成一个包。
///
/// ── 两个功能（对应手机版实测问题，2026-09-29）────────────────────
///
/// ③ 关卡/章节点击无反馈
///    根因：`DragMenuSelectItem`（关卡 `DragMenuSelectItemlevel`、章节
///    `DragMenuSelectItemChapter` 的共同基类）只挂了 `button.Pressed`，**没有按压视觉**。
///    方案：挂 `Button.ButtonDown / ButtonUp / MouseExited`，把 `graphics` 压暗 + 缩小，
///    抬起还原 —— 与游戏自家 `NinePatchButtonBase` 用 `ButtonDown` 做按压视觉同一套路。
///    ⚠️ 手机端"触摸→鼠标"是**同一帧里按下+抬起**（还会紧跟一个 MouseExited），
///       若立刻还原，变暗那一帧根本不会被渲染 ⇒ 玩家看不到任何反馈（实测就是这个原因）
///       ⇒ 按下后至少保持 `PressVisibleMs` 毫秒再还原。
///
/// ④ 无法关闭自动拾取阳光 / 金币
///    根因（源码实证，两个独立存档特性键）：
///        阳光 `TowerDefenseSunBase`:  autoCollect = GetFeatureValue("SunCollect") != 0
///        金币 `TowerDefenseCoinBase`: autoCollect = GetFeatureValue("CoinCollect") > 0 && 场上无吸金磁
///    游戏没给玩家任何入口去改。方案：在 **图鉴 → 工具页（PropLayer）** 注入一个
///    「自动拾取」框，含两个独立开关（阳光 / 金币），点击写
///    `GameSaveManager.SetFeatureValue(...)`（进存档，重启保留），并同步场上
///    已生成的阳光/金币（这两个类的 `autoCollect` 是 public 字段）。
///
/// ── 铁律（前几个 Mod 已定案）────────────────────────────────────
/// · 手写 csproj 没有 Godot 源码生成器 ⇒ 自定义 Node 子类的引擎回调不会被调用，
///   全部逻辑走 `SceneTree.Connect("process_frame", Callable.From(Action))` 信号通道。
/// · Initialize / OnAllModsLoaded / Shutdown 一律 try/catch 吞异常（抛出 = 整包回滚）。
/// · `TowerDefenseInGamePacketShow` 是被到处复用的卡类（卡池/卡槽/图鉴/商店），
///   对卡做任何操作前必须先用祖先链确认它在哪个界面。
/// </summary>
public sealed class MobileTapFeedbackEntry : IXWModRuntimeEntry
{
	private const string P = "[TapFeedback] ";

	// ================================================================ 内部开关（默认全开）

	/// <summary>③ 关卡/章节/入口按压视觉反馈。</summary>
	internal static readonly bool FixPressVisual = true;

	/// <summary>④ 图鉴-工具页"自动拾取"开关框（阳光 / 金币 两个独立开关）。</summary>
	internal static readonly bool FixAutoCollect = true;

	/// <summary>诊断日志（默认关闭；排查时置 true 重新打包即可）。</summary>
	private static readonly bool EnableLog = false;

	// ================================================================ 常量 / 字段

	/// <summary>
	/// ★ 按压态的**最短可见时长**（毫秒）。
	/// 手机上"触摸→鼠标"是同一帧里按下+抬起（还会紧跟一个 MouseExited），
	/// 若立刻还原，变暗那一帧根本不会被渲染 ⇒ 玩家看不到任何反馈（实测就是这个原因）。
	/// ⇒ 按下后至少保持这么久再还原。
	/// </summary>
	private const ulong PressVisibleMs = 160;

	private const string MetaItemHooked = "MTF_ItemHooked";
	private const string MetaAlmanacBox = "MTF_AlmanacBox";

	private SceneTree _tree;
	private Callable _tick;
	private bool _started;
	private int _diag;

	/// <summary>③ 已挂钩的关卡/章节入口（强引用，便于节点销毁后清理）。</summary>
	private readonly List<DragMenuSelectItem> _hookedItems = new List<DragMenuSelectItem>();

	/// <summary>各条目"最早可以还原"的时间戳（Time.GetTicksMsec()）。</summary>
	private readonly Dictionary<ulong, ulong> _dimUntil = new Dictionary<ulong, ulong>();

	/// <summary>被推迟的还原动作（到点后由 OnFrame 执行）。</summary>
	private readonly Dictionary<ulong, Action> _pendingRestore = new Dictionary<ulong, Action>();

	/// <summary>③ 挂钩时记录的 graphics 原始 Modulate / Scale（抬起还原用）。</summary>
	private readonly Dictionary<ulong, Color> _origModulate = new Dictionary<ulong, Color>();
	private readonly Dictionary<ulong, Vector2> _origScale = new Dictionary<ulong, Vector2>();

	/// <summary>扫描节流（视觉反馈与图鉴注入不需要每帧扫）。</summary>
	private int _frame;

	// ================================================================ 生命周期

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			Log("初始化完成（关卡按压反馈 / 图鉴自动拾取开关）。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Initialize 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started)
			{
				return;
			}
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				Log("拿不到 SceneTree，本 Mod 不生效。");
				return;
			}
			_tick = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tick);
			_started = true;
			Log("已挂载 process_frame（③按压反馈 / ④图鉴自动拾取开关）。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "OnAllModsLoaded 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void Shutdown()
	{
		try
		{
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tick);
			}
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Shutdown 异常（已吞）：" + ex.Message); } catch { }
		}
		finally
		{
			_started = false;
		}
	}

	// ================================================================ 每帧驱动

	private void OnFrame()
	{
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}
			_frame++;

			FlushPendingRestore();                     // ③ 到点的按压态还原
			if (_frame % 15 == 0)
			{
				ScanSelectItems();                     // ③ 关卡/章节按压反馈
				ScanAlmanac();                         // ④ 图鉴注入自动拾取框
			}
		}
		catch (Exception ex)
		{
			if (_diag < 100)
			{
				_diag = 100;
				Log("每帧驱动异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>③ 把到点的"按压态还原"执行掉（保证按压反馈至少可见 PressVisibleMs 毫秒）。</summary>
	private void FlushPendingRestore()
	{
		if (_pendingRestore.Count == 0)
		{
			return;
		}
		try
		{
			ulong now = Time.GetTicksMsec();
			List<ulong> done = null;
			foreach (KeyValuePair<ulong, Action> kv in _pendingRestore)
			{
				bool ready = !_dimUntil.TryGetValue(kv.Key, out ulong until) || now >= until;
				if (ready)
				{
					(done ??= new List<ulong>()).Add(kv.Key);
					kv.Value?.Invoke();
				}
			}
			if (done != null)
			{
				foreach (ulong k in done)
				{
					_pendingRestore.Remove(k);
				}
			}
		}
		catch { }
	}

	// ================================================================ ③ 关卡/章节按压反馈

	/// <summary>
	/// 给所有 `DragMenuSelectItem`（关卡/章节共同基类）挂按压视觉：
	/// ButtonDown → graphics 压暗 0.62 + 缩放 ×0.94；ButtonUp / MouseExited → 还原。
	/// 缩放必须用"乘"不能用"设"——`InitLevel()` 会把某些入口 `graphics.Scale = 0.5`。
	/// </summary>
	private void ScanSelectItems()
	{
		try
		{
			if (!FixPressVisual)
			{
				return;
			}
			// 清掉已销毁的条目，避免列表无限增长
			_hookedItems.RemoveAll(x => x == null || !GodotObject.IsInstanceValid(x));
			int before = _hookedItems.Count;

			var items = new List<DragMenuSelectItem>();
			// ★ is 判定（覆盖 level/chapter 两个子类）—— 不能按类名精确匹配
			CollectByType(_tree != null && GodotObject.IsInstanceValid(_tree) ? _tree.Root : null,
				0, items, 200);

			foreach (DragMenuSelectItem item in items)
			{
				if (item == null || !GodotObject.IsInstanceValid(item) || item.button == null)
				{
					continue;
				}
				if (item.HasMeta(MetaItemHooked))
				{
					continue;
				}
				Control g = item.graphics;
				if (g == null || !GodotObject.IsInstanceValid(g))
				{
					continue;
				}
				item.SetMeta(MetaItemHooked, true);
				_hookedItems.Add(item);
				ulong id = item.GetInstanceId();
				_origModulate[id] = g.Modulate;
				_origScale[id] = g.Scale;

				item.button.ButtonDown += () =>
				{
					try
					{
						if (!GodotObject.IsInstanceValid(g))
						{
							return;
						}
						_dimUntil[id] = Time.GetTicksMsec() + PressVisibleMs;
						_pendingRestore.Remove(id);
						Color m = _origModulate.TryGetValue(id, out Color om) ? om : g.Modulate;
						Vector2 s = _origScale.TryGetValue(id, out Vector2 os) ? os : g.Scale;
						g.Modulate = new Color(m.R * 0.62f, m.G * 0.62f, m.B * 0.62f, m.A);
						g.Scale = s * 0.94f;
					}
					catch { }
				};
				Action applyRestore = () =>
				{
					try
					{
						if (!GodotObject.IsInstanceValid(g))
						{
							return;
						}
						if (_origModulate.TryGetValue(id, out Color om))
						{
							g.Modulate = om;
						}
						if (_origScale.TryGetValue(id, out Vector2 os))
						{
							g.Scale = os;
						}
					}
					catch { }
				};
				Action restore = () =>
				{
					try
					{
						ulong now = Time.GetTicksMsec();
						ulong until = _dimUntil.TryGetValue(id, out ulong u) ? u : 0UL;
						if (now >= until)
						{
							applyRestore();
							_pendingRestore.Remove(id);
						}
						else
						{
							// 未达到最短可见时长 ⇒ 推迟到 OnFrame 里到点还原
							_pendingRestore[id] = applyRestore;
						}
					}
					catch { }
				};
				item.button.ButtonUp += restore;
				item.button.MouseExited += restore;
			}
			if (_hookedItems.Count != before)
			{
				Log("③按压反馈已挂钩条目数 = " + _hookedItems.Count);
			}
		}
		catch (Exception ex)
		{
			if (_diag < 104)
			{
				_diag = 104;
				Log("关卡按压反馈异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	// ================================================================ ④ 图鉴-工具页 自动拾取开关

	/// <summary>
	/// 找到图鉴（Almanac）就往**工具页（PropLayer）**注入「自动拾取」框。
	/// 图鉴的页签切换就是 `propLayer.Visible` 切换 ⇒ 挂在它下面，可见性天然跟工具页一致。
	/// 每次打开图鉴都会新建对话框节点 ⇒ 用 meta 防重复注入，节点关了标记随节点一起没了。
	/// </summary>
	private void ScanAlmanac()
	{
		try
		{
			if (!FixAutoCollect)
			{
				return;
			}
			var list = new List<Almanac>();
			CollectByType(_tree != null && GodotObject.IsInstanceValid(_tree) ? _tree.Root : null,
				0, list, 20);
			foreach (Almanac al in list)
			{
				if (al == null || !GodotObject.IsInstanceValid(al) || al.HasMeta(MetaAlmanacBox))
				{
					continue;
				}
				if (al.propLayer == null || !GodotObject.IsInstanceValid(al.propLayer))
				{
					continue;
				}
				al.SetMeta(MetaAlmanacBox, true);
				BuildAutoCollectBox(al);
				Log("已在图鉴-工具页注入「自动拾取」开关框。");
			}
		}
		catch (Exception ex)
		{
			if (_diag < 105)
			{
				_diag = 105;
				Log("图鉴注入异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>构建「自动拾取」框：标题 + 阳光开关 + 金币开关（两个独立开关）。</summary>
	private void BuildAutoCollectBox(Almanac al)
	{
		PanelContainer panel = new PanelContainer();
		panel.Name = "MTFAutoCollectBox";
		// 右上角（距右 24 / 距顶 24）。
		panel.AnchorLeft = 1f;
		panel.AnchorTop = 0f;
		panel.AnchorRight = 1f;
		panel.AnchorBottom = 0f;
		panel.OffsetLeft = -304f;
		panel.OffsetTop = 24f;
		panel.OffsetRight = -24f;
		panel.OffsetBottom = 184f;
		panel.GrowHorizontal = Control.GrowDirection.Begin;
		panel.GrowVertical = Control.GrowDirection.End;

		VBoxContainer box = new VBoxContainer();
		box.Name = "Box";
		box.AddThemeConstantOverride("separation", 6);
		panel.AddChild(box);

		Label title = new Label();
		title.Text = "自动拾取";
		box.AddChild(title);

		box.AddChild(MakeCollectToggle("阳光", "SunCollect", sunOnly: true));
		box.AddChild(MakeCollectToggle("金币", "CoinCollect", sunOnly: false));

		al.propLayer.AddChild(panel);
	}

	// ================================================================ ④ 购买状态与偏好
	//
	// ── 源码事实 ────────────────────────────────────────────────────
	// · `SunCollect`（阳光自动拾取，商店 1000 金币）/ `CoinCollect`（金币自动拾取，2000 金币）
	//   是 **WWM 商店商品**（Asset/Config/Shop/WWM/WWMShop.json，"Type":"Item"，一次性）；
	//   购买走 `ApplySave("Feature", key)` ⇒ `SetFeatureValue(key, true)`。
	// · `FeatureInit.json` 里两者默认 **0.0**（未购买）。
	// · `Feature` 分类挂在 `config.saveDictionary[userCurrent]` 下 ⇒ **按存档位独立**，
	//   多存档互不影响。
	// · 阳光的 `autoCollect` 只在**生成后的下一个物理帧**从 Feature 值求值一次
	//   （`OnRefreshPhysicsFrame`，一次性回调）⇒ 改场上实例字段会被这次评估覆盖，
	//   **改 Feature 值是唯一可靠路径**。
	//
	// ── 语义设计 ───────────────────────────────────────────────────
	// 游戏里「已购买」与「开启」是同一个键（>0 即开启且视为已购）⇒ 直接写 0 会把
	// 购买记录抹掉（商店里商品回到可购买状态，可能重复花金币）。
	// ⇒ Mod 自己按**存档位**记一份「曾经拥有」（user://mobiletapfeedback.json）：
	//    · 已购买 = `Feature > 0` **或** 本 Mod 记录里有（被旧版本开关抹掉的也能识别）；
	//    · **兼容旧包**：本 Mod 从「手机操作优化」拆出，后者用的是
	//      `user://mobileuxfixes.json` ⇒ 这里把它当**只读的历史记录**一起查。
	//    · 未购买 ⇒ 开关置灰（不允许白嫖商店商品）；
	//    · 开关 = 写 Feature 值（游戏自己的开关语义）；
	//      开 ⇒ Feature=1（若只有本 Mod 记录，等于免费恢复——因为它本来就该有）；
	//      关 ⇒ Feature=0（商店会重新可买，UI 已提示）。

	/// <summary>本 Mod 的偏好文件（user:// 根下，按存档位分档）。</summary>
	private const string RecPath = "user://mobiletapfeedback.json";

	/// <summary>拆分前旧包的偏好文件（只读兼容）。</summary>
	private const string LegacyRecPath = "user://mobileuxfixes.json";

	/// <summary>当前存档位标识（游戏存档就是按它分的）。</summary>
	private string CurrentSlot()
	{
		try
		{
			return GameSaveManager.Instance.GetUserCurrent() ?? "";
		}
		catch
		{
			return "";
		}
	}

	private Godot.Collections.Dictionary LoadRec(string path)
	{
		try
		{
			if (Godot.FileAccess.FileExists(path))
			{
				using Godot.FileAccess f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
				if (f != null)
				{
					Variant v = Json.ParseString(f.GetAsText());
					if (v.VariantType == Variant.Type.Dictionary)
					{
						return v.AsGodotDictionary();
					}
				}
			}
		}
		catch { }
		return new Godot.Collections.Dictionary();
	}

	private void SaveRec(Godot.Collections.Dictionary root)
	{
		try
		{
			using Godot.FileAccess f = Godot.FileAccess.Open(RecPath, Godot.FileAccess.ModeFlags.Write);
			if (f != null)
			{
				f.StoreString(Json.Stringify(root, "\t"));
			}
		}
		catch { }
	}

	/// <summary>从指定偏好文件的当前存档位里取键值。</summary>
	private bool RecGetFrom(string path, string key)
	{
		try
		{
			Godot.Collections.Dictionary root = LoadRec(path);
			string slot = CurrentSlot();
			if (root.ContainsKey(slot))
			{
				Godot.Collections.Dictionary d = root[slot].AsGodotDictionary();
				if (d.ContainsKey(key))
				{
					return d[key].AsBool();
				}
			}
		}
		catch { }
		return false;
	}

	/// <summary>本 Mod（或拆分前旧包）记录的「这个存档曾拥有该拾取商品」。</summary>
	private bool RecGet(string key)
	{
		return RecGetFrom(RecPath, key) || RecGetFrom(LegacyRecPath, key);
	}

	private void RecSet(string key, bool val)
	{
		try
		{
			Godot.Collections.Dictionary root = LoadRec(RecPath);
			string slot = CurrentSlot();
			Godot.Collections.Dictionary d = root.ContainsKey(slot)
				? root[slot].AsGodotDictionary()
				: new Godot.Collections.Dictionary();
			d[key] = val;
			root[slot] = d;
			SaveRec(root);
		}
		catch { }
	}

	/// <summary>这个存档是否拥有该拾取商品（游戏购买记录 **或** 本 Mod 记录）。</summary>
	private bool IsOwned(string key)
	{
		return GetFeature(key) > 0 || RecGet(key);
	}

	/// <summary>单个拾取开关：未购买置灰；切换写 Feature 值（游戏自己的开关语义）。</summary>
	private CheckBox MakeCollectToggle(string label, string key, bool sunOnly)
	{
		bool owned = IsOwned(key);
		CheckBox cb = new CheckBox();
		cb.Text = owned ? label : label + "（未购买）";
		cb.ButtonPressed = owned && GetFeature(key) > 0;
		cb.Disabled = !owned;
		if (!owned)
		{
			cb.TooltipText = "在商店购买后可用";
		}
		cb.Pressed += () =>
		{
			try
			{
				ApplyAutoCollect(key, cb.ButtonPressed, sunOnly);
			}
			catch (Exception ex)
			{
				try { GD.PrintErr(P + "开关异常：" + ex.Message); } catch { }
			}
		};
		return cb;
	}

	/// <summary>
	/// 切换某个拾取特性：写 Feature 值（游戏自己的开关）+ 记录「曾经拥有」+ 同步场上已有实例。
	/// 未购买时强制保持关闭（防御：不允许白嫖商店商品）。
	/// </summary>
	private void ApplyAutoCollect(string key, bool on, bool sunOnly)
	{
		try
		{
			bool owned = IsOwned(key);
			if (!owned)
			{
				on = false;                       // 未购买 ⇒ 不允许开启
			}
			else
			{
				RecSet(key, true);                // 记录「曾经拥有」（恢复依据）
			}
			GameSaveManager.Instance.SetFeatureValue(key, on ? 1 : 0);

			// 场上已生成的实例：autoCollect 是 public 字段，直接同步（新生成的会自己读 Feature 值）
			var nodes = new List<Node>();
			CollectByClassName(_tree != null && GodotObject.IsInstanceValid(_tree) ? _tree.Root : null,
				0, sunOnly ? "TowerDefenseSunBase" : "TowerDefenseCoinBase", nodes, 500);
			bool goldMagnet = _tree != null && GodotObject.IsInstanceValid(_tree)
				&& _tree.GetNodeCountInGroup("GoldMagnet") > 0;
			foreach (Node n in nodes)
			{
				try
				{
					if (sunOnly)
					{
						if (n is TowerDefenseSunBase s && GodotObject.IsInstanceValid(s))
						{
							s.autoCollect = on;
						}
					}
					else if (n is TowerDefenseCoinBase c && GodotObject.IsInstanceValid(c))
					{
						// 与游戏原判据保持一致：场上有吸金磁时金币不自动收
						c.autoCollect = on && !goldMagnet;
					}
				}
				catch { }
			}
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "切换自动拾取异常：" + ex.Message); } catch { }
		}
	}

	private static int GetFeature(string key)
	{
		try
		{
			return GameSaveManager.Instance.GetFeatureValue(key);
		}
		catch
		{
			return 0;
		}
	}

	// ================================================================ 工具

	/// <summary>
	/// 按**类型**递归收集节点（深度优先，最多 max 个）。
	///
	/// ★ `is T` 判定天然覆盖子类 —— 不能按类名精确匹配：
	///   关卡/章节入口的实际类型是 `DragMenuSelectItemlevel` / `DragMenuSelectItemChapter`
	///   （都是 `DragMenuSelectItem` 的**子类**）⇒ 精确匹配永远匹配不上 ⇒ 按压反馈整个不生效（实测）。
	/// </summary>
	private static void CollectByType<T>(Node node, int depth, List<T> outList, int max) where T : class
	{
		try
		{
			if (node == null || !GodotObject.IsInstanceValid(node) || outList.Count >= max || depth > 40)
			{
				return;
			}
			if (node is T t)
			{
				outList.Add(t);
			}
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				CollectByType(node.GetChild(i), depth + 1, outList, max);
			}
		}
		catch { }
	}

	/// <summary>按类名递归收集节点（深度优先，最多 max 个）。</summary>
	private static void CollectByClassName(Node node, int depth, string className,
		List<Node> outList, int max)
	{
		try
		{
			if (node == null || !GodotObject.IsInstanceValid(node) || outList.Count >= max || depth > 40)
			{
				return;
			}
			if (node.GetType().Name == className)
			{
				outList.Add(node);
			}
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				CollectByClassName(node.GetChild(i), depth + 1, className, outList, max);
			}
		}
		catch { }
	}

	private void Log(string msg)
	{
		if (!EnableLog)
		{
			return;
		}
		try { GD.Print(P + msg); } catch { }
	}
}
