using System.Text;
using AwAUI.Platform;
using AwAUI.X11;

namespace AwAUI.Platform.UnitTests;

/// <summary>X11 后端的纯逻辑：键码表、keysym → 逻辑键、XIM 预编辑状态、CLIPBOARD 选择协议（用内存中的假 X 服务器连接两个客户端）。</summary>
public class X11KeyTests
{
    [Theory]
    [InlineData(79 + 8, PhysicalKey.Numpad1)]
    [InlineData(82 + 8, PhysicalKey.Numpad0)]
    [InlineData(96 + 8, PhysicalKey.NumpadEnter)]
    [InlineData(28 + 8, PhysicalKey.Enter)]
    [InlineData(98 + 8, PhysicalKey.NumpadDivide)]
    [InlineData(55 + 8, PhysicalKey.NumpadMultiply)]
    [InlineData(69 + 8, PhysicalKey.NumLock)]
    [InlineData(86 + 8, PhysicalKey.IntlBackslash)]
    [InlineData(100 + 8, PhysicalKey.AltRight)]
    [InlineData(183 + 8, PhysicalKey.F13)]
    [InlineData(194 + 8, PhysicalKey.F24)]
    [InlineData(300, PhysicalKey.Unknown)]
    public void Key_codes(int keycode, PhysicalKey expected) => Assert.Equal(expected, X11KeyCodes.Get((uint)keycode));

    [Theory]
    [InlineData(0x61UL, null, "a")]
    [InlineData(0x41UL, null, "A")]
    [InlineData(0x40UL, null, "@")]                // AltGr+Q（德语）的 keysym 就是 at
    [InlineData(0xE9UL, null, "é")]
    [InlineData(0x10020ACUL, null, "€")]           // Unicode keysym
    [InlineData(0x101F600UL, null, "😀")]
    [InlineData(0xFE51UL, null, "Dead")]           // dead_acute
    [InlineData(0xFF0DUL, null, "Enter")]
    [InlineData(0xFF8DUL, null, "Enter")]          // KP_Enter
    [InlineData(0xFFB1UL, null, "1")]              // KP_1
    [InlineData(0xFF9CUL, null, "End")]            // KP_End（NumLock 关闭）
    [InlineData(0xFE03UL, null, "AltGraph")]       // ISO_Level3_Shift
    [InlineData(0xFFCAUL, null, "F13")]
    [InlineData(0xFF51UL, null, "ArrowLeft")]
    [InlineData(0x6C1UL, "а", "а")]                // 旧式西里尔 keysym：用查找到的文本
    [InlineData(0x6C1UL, null, null)]
    [InlineData(0x100D800UL, null, null)]          // 代理码点不是字符
    [InlineData(0UL, "\u0001", null)]
    public void Keysyms(ulong keysym, string? text, string? expected) => Assert.Equal(expected, X11Keysyms.ToKeyValue(keysym, text));
}

public class XimPreeditTests
{
    [Fact]
    public void Draw_callbacks_build_the_preedit()
    {
        var p = new XimPreeditState();
        p.Start();
        p.Draw(caret: 1, changeFirst: 0, changeLength: 0, "n", null);
        p.Draw(2, 1, 0, "i", null);
        Assert.Equal("ni", p.Text);
        Assert.Equal(TextInputRange.Caret(2), p.Selection);
        // 转换：整段替换为「你好」，第一个字反显（正在转换的文节）
        p.Draw(2, 0, 2, "你好", new[] { XimPreeditState.XIMReverse, XimPreeditState.XIMUnderline });
        Assert.Equal("你好", p.Text);
        Assert.Equal(new TextInputRange(0, 1), p.Selection);
        // 删除（text 为 null）
        p.Draw(0, 1, 1, null, null);
        Assert.Equal("你", p.Text);
        p.MoveCaret(1);
        Assert.Equal(new TextInputRange(0, 1), p.Selection); // 剩下的字仍是反显的文节
        p.Draw(1, 0, 1, "你", null);                          // 重画为没有反馈的普通预编辑：选区回到插入点
        Assert.Equal(TextInputRange.Caret(1), p.Selection);
        p.Done();
        Assert.False(p.Active);
        Assert.Equal("", p.Text);
    }

    [Fact]
    public void Feedback_only_draw_changes_the_highlight_without_deleting_text()
    {
        // XIM 规范：XIMText 的 string 为 NULL 而 feedback 不为 NULL 时只改高亮（评审发现：原实现把它当成删除，预编辑文本消失）
        var p = new XimPreeditState();
        p.OnDraw(2, 0, 0, hasText: true, "你好", null);
        Assert.Equal(TextInputRange.Caret(2), p.Selection);
        p.OnDraw(2, 1, 1, hasText: true, text: null, new[] { XimPreeditState.XIMReverse });
        Assert.Equal("你好", p.Text);
        Assert.Equal(new TextInputRange(1, 2), p.Selection);
        // 越界的反馈被忽略
        p.OnDraw(9, -3, 99, hasText: true, text: null, new[] { XimPreeditState.XIMUnderline, XimPreeditState.XIMReverse, 7UL, 7UL });
        Assert.Equal("你好", p.Text);
        Assert.Equal(new TextInputRange(1, 2), p.Selection);
        // XIMText 为 NULL：删除
        p.OnDraw(0, 0, 1, hasText: false, text: null, null);
        Assert.Equal("好", p.Text);
        // 有 XIMText、string 与 feedback 都为 NULL：替换为空（删除）
        p.OnDraw(0, 0, 1, hasText: true, text: null, null);
        Assert.Equal("", p.Text);
    }

    [Fact]
    public void Code_point_indices_map_to_utf16()
    {
        var p = new XimPreeditState();
        p.Draw(2, 0, 0, "😀a", null);
        Assert.Equal(TextInputRange.Caret(3), p.Selection); // 2 个码点 = 3 个 UTF-16 码元
        p.Draw(1, 1, 1, "😀", new[] { XimPreeditState.XIMHighlight });
        Assert.Equal("😀😀", p.Text);
        Assert.Equal(new TextInputRange(2, 4), p.Selection);
    }

    [Fact]
    public void Hostile_values_are_clamped()
    {
        var p = new XimPreeditState();
        p.Draw(-5, -10, 999, "abc", null);
        Assert.Equal("abc", p.Text);
        Assert.Equal(TextInputRange.Caret(0), p.Selection);
        p.Draw(int.MaxValue, int.MaxValue, int.MaxValue, "d", null);
        Assert.Equal("abcd", p.Text);
        Assert.Equal(TextInputRange.Caret(4), p.Selection);
        p.Draw(0, 0, 0, new string('x', 100_000), null);
        Assert.Equal(XimPreeditState.MaxLength, p.Text.Length);
        p.Draw(0, 0, 0, "\uD800", null); // 孤立代理换成 U+FFFD（已满：不再插入）
        Assert.Equal(XimPreeditState.MaxLength, p.Text.Length);
    }
}

/// <summary>
/// 内存中的 X 服务器：窗口属性表、选择所有者、事件队列。每个客户端有自己的 <see cref="X11SelectionIO"/>，事件按 X 的规则投递：
/// SelectionRequest 给所有者，SelectionNotify 给请求者，PropertyNotify 给属性所在窗口的拥有者与选择了该窗口属性变化的客户端。
/// </summary>
sealed class FakeXServer
{
    public readonly Dictionary<(ulong Window, ulong Property), (ulong Type, int Format, byte[] Data)> Properties = new();
    public readonly Dictionary<ulong, Client> Owners = new();
    public readonly Queue<Action> Events = new();
    public readonly List<Client> Clients = new();
    public static readonly X11SelectionAtoms Atoms = new(clipboard: 100, targets: 101, utf8String: 102, textPlainUtf8: 103, @string: 31,
        text: 104, incr: 105, atom: 4, property: 106);
    public long Now { get; set; }
    public int MaxChunk = 64;

    public Client Connect(ulong window)
    {
        var c = new Client(this, window);
        Clients.Add(c);
        return c;
    }

    public void Pump(int max = 100_000)
    {
        int n = 0;
        while (Events.Count > 0 && n++ < max) Events.Dequeue()();
    }

    void PropertyChanged(ulong window, ulong property, bool deleted)
    {
        foreach (var c in Clients)
        {
            if (c.Window == window && !deleted) Events.Enqueue(() => c.Reader.OnPropertyNewValue(window, property, Now));
            if (c.Watching.Contains(window) && deleted) Events.Enqueue(() => c.Server.OnPropertyDeleted(window, property, Now));
        }
    }

    public sealed class Client : X11SelectionIO
    {
        readonly FakeXServer _x;
        public readonly HashSet<ulong> Watching = new();
        public readonly X11SelectionServer Server;
        public readonly X11SelectionReader Reader;
        public int Notifies;
        public bool IgnoreRequests;

        public Client(FakeXServer x, ulong window)
        {
            _x = x;
            Window = window;
            Server = new X11SelectionServer(this, Atoms);
            Reader = new X11SelectionReader(this, Atoms);
        }

        public override ulong Window { get; }

        public override int MaxChunkBytes => _x.MaxChunk;

        public override bool GetProperty(ulong window, ulong property, bool delete, long maxBytes, out ulong type, out int format, out byte[] data, out long bytesAfter)
        {
            type = 0; format = 0; data = Array.Empty<byte>(); bytesAfter = 0;
            if (!_x.Properties.TryGetValue((window, property), out var p)) return true;
            type = p.Type;
            format = p.Format;
            int n = (int)Math.Min(p.Data.Length, maxBytes);
            data = p.Data.Take(n).ToArray();
            bytesAfter = p.Data.Length - n;
            if (delete && bytesAfter == 0)
            {
                _x.Properties.Remove((window, property));
                _x.PropertyChanged(window, property, deleted: true);
            }
            return true;
        }

        void Set(ulong window, ulong property, ulong type, int format, byte[] data)
        {
            _x.Properties[(window, property)] = (type, format, data);
            _x.PropertyChanged(window, property, deleted: false);
        }

        public override void ChangeProperty(ulong window, ulong property, ulong type, byte[] data, int offset, int count) =>
            Set(window, property, type, 8, data.Skip(offset).Take(count).ToArray());

        public override void ChangePropertyAtoms(ulong window, ulong property, ulong[] atoms) =>
            Set(window, property, Atoms.Atom, 32, atoms.SelectMany(BitConverter.GetBytes).ToArray());

        public override void ChangePropertyIncr(ulong window, ulong property, long size) =>
            Set(window, property, Atoms.Incr, 32, BitConverter.GetBytes(size));

        public override void SendSelectionNotify(ulong requestor, ulong selection, ulong target, ulong property, ulong time)
        {
            Notifies++;
            var c = _x.Clients.FirstOrDefault(k => k.Window == requestor);
            if (c is not null) _x.Events.Enqueue(() => c.Reader.OnSelectionNotify(selection, target, property, _x.Now));
        }

        public override void ConvertSelection(ulong selection, ulong target, ulong property, ulong time)
        {
            if (_x.Owners.TryGetValue(selection, out var owner))
            {
                if (owner.IgnoreRequests) return;
                _x.Events.Enqueue(() => owner.Server.OnSelectionRequest(Window, selection, target, property, time, _x.Now));
            }
            else _x.Events.Enqueue(() => Reader.OnSelectionNotify(selection, target, 0, _x.Now)); // 没有所有者：服务器直接回复 None
        }

        public override void SelectPropertyChanges(ulong window, bool enable)
        {
            if (enable) Watching.Add(window);
            else Watching.Remove(window);
        }

        public override bool SetOwner(ulong selection, ulong time)
        {
            if (_x.Owners.TryGetValue(selection, out var old) && old != this)
                _x.Events.Enqueue(() => old.Server.OnSelectionClear(selection));
            _x.Owners[selection] = this;
            return true;
        }
    }
}

public class X11SelectionTests
{
    static string? Paste(FakeXServer x, FakeXServer.Client reader)
    {
        string? result = "<pending>";
        reader.Reader.Request(x.Now, r => result = r);
        x.Pump();
        return result;
    }

    [Fact]
    public void Copy_in_one_client_paste_in_another()
    {
        var x = new FakeXServer();
        var a = x.Connect(1);
        var b = x.Connect(2);
        Assert.Null(Paste(x, b)); // 没有所有者
        Assert.True(a.Server.Own("héllo 世界", 10));
        Assert.Equal("héllo 世界", Paste(x, b));
        Assert.Empty(x.Properties); // 读后删除
    }

    [Fact]
    public void Targets_and_string_fallback()
    {
        var x = new FakeXServer();
        var a = x.Connect(1);
        a.Server.Own("a€", 1);
        a.Server.OnSelectionRequest(7, FakeXServer.Atoms.Clipboard, FakeXServer.Atoms.Targets, 50, 1, 0);
        var targets = x.Properties[(7, 50)];
        Assert.Equal(FakeXServer.Atoms.Atom, targets.Type);
        Assert.Equal(32, targets.Format);
        Assert.Contains(FakeXServer.Atoms.Utf8String, Enumerable.Range(0, targets.Data.Length / 8).Select(i => BitConverter.ToUInt64(targets.Data, i * 8)));
        a.Server.OnSelectionRequest(7, FakeXServer.Atoms.Clipboard, FakeXServer.Atoms.String, 51, 1, 0);
        Assert.Equal(new byte[] { (byte)'a', (byte)'?' }, x.Properties[(7, 51)].Data); // 非 Latin-1 → ?
        // 过时的客户端：property 为 None 时用 target 作为属性名
        a.Server.OnSelectionRequest(7, FakeXServer.Atoms.Clipboard, FakeXServer.Atoms.Utf8String, 0, 1, 0);
        Assert.Equal("a€", Encoding.UTF8.GetString(x.Properties[(7, FakeXServer.Atoms.Utf8String)].Data));
        // 不支持的目标与别的选择：拒绝（property = None）
        int before = a.Notifies;
        a.Server.OnSelectionRequest(7, FakeXServer.Atoms.Clipboard, 999, 52, 1, 0);
        a.Server.OnSelectionRequest(7, 1 /* PRIMARY */, FakeXServer.Atoms.Utf8String, 53, 1, 0);
        Assert.Equal(before + 2, a.Notifies);
        Assert.False(x.Properties.ContainsKey((7, 52)));
        Assert.False(x.Properties.ContainsKey((7, 53)));
    }

    [Fact]
    public void Reader_falls_back_to_string_when_utf8_is_refused()
    {
        var x = new FakeXServer();
        var b = x.Connect(2);
        var owner = x.Connect(3);
        x.Owners[FakeXServer.Atoms.Clipboard] = owner;
        owner.IgnoreRequests = true;
        string? result = "<pending>";
        b.Reader.Request(0, r => result = r);
        // 手工扮演只支持 STRING 的所有者：拒绝 UTF8_STRING，接受 STRING
        b.Reader.OnSelectionNotify(FakeXServer.Atoms.Clipboard, FakeXServer.Atoms.Utf8String, 0, 0);
        Assert.Equal("<pending>", result);
        x.Properties[(2, FakeXServer.Atoms.Property)] = (FakeXServer.Atoms.String, 8, new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x0D, 0x0A });
        b.Reader.OnSelectionNotify(FakeXServer.Atoms.Clipboard, FakeXServer.Atoms.String, FakeXServer.Atoms.Property, 0);
        Assert.Equal("café\n", result);
    }

    [Fact]
    public void Large_text_uses_incr_in_both_directions()
    {
        var x = new FakeXServer { MaxChunk = 16 };
        var a = x.Connect(1);
        var b = x.Connect(2);
        var text = string.Concat(Enumerable.Range(0, 100).Select(i => "行" + i));
        a.Server.Own(text, 1);
        Assert.Equal(text, Paste(x, b));
        Assert.Equal(0, a.Server.ActiveTransfers);
        Assert.Empty(a.Watching);
        Assert.Empty(x.Properties);
    }

    [Fact]
    public void Concurrent_requests_share_one_conversion()
    {
        var x = new FakeXServer();
        var a = x.Connect(1);
        var b = x.Connect(2);
        a.Server.Own("x", 1);
        var results = new List<string?>();
        b.Reader.Request(0, results.Add);
        b.Reader.Request(0, results.Add);
        x.Pump();
        Assert.Equal(new[] { "x", "x" }, results);
        Assert.Equal(1, a.Notifies);
    }

    [Fact]
    public void Unresponsive_owner_times_out()
    {
        var x = new FakeXServer();
        var b = x.Connect(2);
        var owner = x.Connect(3);
        x.Owners[FakeXServer.Atoms.Clipboard] = owner;
        owner.IgnoreRequests = true;
        string? result = "<pending>";
        b.Reader.Request(0, r => result = r);
        x.Pump();
        Assert.True(b.Reader.Busy);
        b.Reader.CheckTimeouts(X11SelectionReader.TimeoutTicks - 1);
        Assert.Equal("<pending>", result);
        b.Reader.CheckTimeouts(X11SelectionReader.TimeoutTicks);
        Assert.Null(result);
        Assert.False(b.Reader.Busy);
    }

    [Fact]
    public void Incr_reader_aborts_oversized_transfers_and_stalled_senders_time_out()
    {
        var x = new FakeXServer();
        var b = x.Connect(2);
        string? result = "<pending>";
        b.Reader.Request(0, r => result = r);
        // 所有者宣布 INCR，然后不断送块：超过上限时放弃
        x.Properties[(2, FakeXServer.Atoms.Property)] = (FakeXServer.Atoms.Incr, 32, BitConverter.GetBytes(1L));
        b.Reader.OnSelectionNotify(FakeXServer.Atoms.Clipboard, FakeXServer.Atoms.Utf8String, FakeXServer.Atoms.Property, 0);
        var chunk = new byte[1 << 20];
        long sent = 0;
        while (result == "<pending>" && sent <= X11SelectionReader.MaxBytes + chunk.Length)
        {
            x.Properties[(2, FakeXServer.Atoms.Property)] = (FakeXServer.Atoms.Utf8String, 8, chunk);
            b.Reader.OnPropertyNewValue(2, FakeXServer.Atoms.Property, 0);
            sent += chunk.Length;
        }
        Assert.Null(result);
        Assert.True(sent <= X11SelectionReader.MaxBytes + chunk.Length);

        // 发送方：请求者不再删除属性时，INCR 传输超时被丢弃
        var a = x.Connect(1);
        a.Server.Own(new string('z', 1000), 1);
        x.MaxChunk = 10;
        a.Server.OnSelectionRequest(9, FakeXServer.Atoms.Clipboard, FakeXServer.Atoms.Utf8String, 60, 1, 0);
        Assert.Equal(1, a.Server.ActiveTransfers);
        Assert.Equal(FakeXServer.Atoms.Incr, x.Properties[(9, 60)].Type);
        a.Server.CheckTimeouts(X11SelectionServer.TransferTimeoutTicks);
        Assert.Equal(0, a.Server.ActiveTransfers);
        Assert.DoesNotContain(9UL, a.Watching);
        // 同时进行的 INCR 传输数有上限
        for (int i = 0; i < X11SelectionServer.MaxTransfers + 5; i++)
            a.Server.OnSelectionRequest((ulong)(1000 + i), FakeXServer.Atoms.Clipboard, FakeXServer.Atoms.Utf8String, 60, 1, 0);
        Assert.Equal(X11SelectionServer.MaxTransfers, a.Server.ActiveTransfers);
    }

    [Fact]
    public void Losing_ownership_stops_serving()
    {
        var x = new FakeXServer();
        var a = x.Connect(1);
        var b = x.Connect(2);
        a.Server.Own("mine", 1);
        b.Server.Own("theirs", 2);
        x.Pump(); // a 收到 SelectionClear
        Assert.Null(a.Server.Text);
        Assert.Equal("theirs", Paste(x, a));
    }
}
