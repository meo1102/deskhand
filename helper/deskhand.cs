// deskhand helper —— Windows 桌面眼睛与手。
// 命令：state / diag / shot / move / click / type / key / scroll / selftest
// 常驻：serve（命名管道 broker，给插件用）/ watch（跟随 DSH 启停 broker）
// 输入统一走 SendInput（与真实用户同一注入路径）。broker 必须以普通用户权限启动，
// 才能向普通应用注入；由 DSH 插件直接 spawn 的子进程会被 DSH 沙箱降到 Low 完整性。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Automation;
using System.Globalization;
using System.Speech.Recognition;

namespace Deskhand
{
    static class Program
    {
        const string Version = "0.3.0";
        const string PipeName = "deskhand";
        const string DshProcess = "DeepSeek Harness";
        static bool watchStop = false;

        [STAThread]
        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                Native.SetProcessDPIAware();
                if (args.Length == 0)
                {
                    Console.WriteLine(Err("missing command"));
                    return 2;
                }
                string cmd = args[0].ToLowerInvariant();
                if (cmd == "serve") return Serve(args);
                if (cmd == "watch") return Watch();
                string json = Execute(args);
                Console.WriteLine(json);
                return json.StartsWith("{\"ok\":true") ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine(Err(ex.Message));
                return 1;
            }
        }

        static string Ok(string body)
        {
            return "{\"ok\":true," + body + "}";
        }

        static string Err(string message)
        {
            return "{\"ok\":false,\"error\":" + Q(message) + "}";
        }

        static string Q(string value)
        {
            if (value == null) return "null";
            StringBuilder sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        static string Execute(string[] args)
        {
            string cmd = args[0].ToLowerInvariant();
            if (cmd == "move" || cmd == "click" || cmd == "type" || cmd == "key" || cmd == "scroll" || cmd == "uia_act" || cmd == "drag")
            {
                string violation = Blacklist.Violation();
                if (violation != null) return Err(violation);
            }
            switch (cmd)
            {
                case "version": return Ok("\"version\":" + Q(Version));
                case "state": return State();
                case "diag": return Diag();
                case "shot": return Shot(args);
                case "move": return Move(args);
                case "click": return Click(args);
                case "type": return TypeText(args);
                case "key": return Key(args);
                case "scroll": return Scroll(args);
                case "uia": return UiaDump();
                case "uia_act": return UiaAct(args);
                case "windows": return WindowsList();
                case "activate": return Activate(args);
                case "drag": return Drag(args);
                case "clipboard": return ClipboardCommand(args);
                case "audio_info": return AudioInfo();
                case "hear": return Hear(args);
                case "transcribe": return Transcribe(args);
                case "selftest": return SelfTest();
                default: return Err("unknown command: " + args[0]);
            }
        }

        static string State()
        {
            Rectangle b = Screen.PrimaryScreen.Bounds;
            Point c = Cursor.Position;
            IntPtr h = Native.GetForegroundWindow();
            StringBuilder title = new StringBuilder(512);
            Native.GetWindowText(h, title, title.Capacity);
            StringBuilder cls = new StringBuilder(256);
            Native.GetClassName(h, cls, cls.Capacity);
            uint pid = 0;
            Native.GetWindowThreadProcessId(h, out pid);
            string proc = string.Empty;
            try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { }
            int parentPid;
            string parentIntegrity;
            Native.ParentInfo(out parentPid, out parentIntegrity);
            string body = "\"version\":" + Q(Version) +
                ",\"session\":" + Process.GetCurrentProcess().SessionId +
                ",\"integrity\":" + Q(Native.IntegrityLevel()) +
                ",\"parent\":{\"pid\":" + parentPid + ",\"integrity\":" + Q(parentIntegrity) + "}" +
                ",\"screen\":{\"x\":" + b.X + ",\"y\":" + b.Y + ",\"width\":" + b.Width + ",\"height\":" + b.Height + "}" +
                ",\"cursor\":{\"x\":" + c.X + ",\"y\":" + c.Y + "}" +
                ",\"foreground\":{\"hwnd\":" + Q("0x" + h.ToInt64().ToString("X")) +
                ",\"title\":" + Q(title.ToString()) +
                ",\"class\":" + Q(cls.ToString()) +
                ",\"pid\":" + pid +
                ",\"process\":" + Q(proc) + "}";
            return Ok(body);
        }

        static string Diag()
        {
            Point before = Cursor.Position;
            bool setOk = Native.SetCursorPos(before.X, before.Y);
            int setErr = Marshal.GetLastWin32Error();
            bool setMove = Native.SetCursorPos(before.X + 40, before.Y + 40);
            int setMoveErr = Marshal.GetLastWin32Error();
            Point afterSet = Cursor.Position;
            Native.SetCursorPos(before.X, before.Y);
            Thread.Sleep(60);
            bool sendOk = Native.MoveAbsolute(before.X + 40, before.Y + 40);
            uint sendErr = (uint)Marshal.GetLastWin32Error();
            Thread.Sleep(60);
            Point afterSend = Cursor.Position;
            Native.MoveAbsolute(before.X, before.Y);
            int parentPid;
            string parentIntegrity;
            Native.ParentInfo(out parentPid, out parentIntegrity);
            string body = "\"version\":" + Q(Version) +
                ",\"session\":" + Process.GetCurrentProcess().SessionId +
                ",\"desktop\":" + Q(Native.DesktopName()) +
                ",\"integrity\":" + Q(Native.IntegrityLevel()) +
                ",\"parent\":{\"pid\":" + parentPid + ",\"integrity\":" + Q(parentIntegrity) + "}" +
                ",\"setCursorPos\":{\"same\":" + B(setOk) + ",\"err\":" + setErr + ",\"move\":" + B(setMove) + ",\"err2\":" + setMoveErr + ",\"after\":{\"x\":" + afterSet.X + ",\"y\":" + afterSet.Y + "}}" +
                ",\"sendInputAbsolute\":{\"ok\":" + B(sendOk) + ",\"err\":" + sendErr + ",\"after\":{\"x\":" + afterSend.X + ",\"y\":" + afterSend.Y + "}}";
            return Ok(body);
        }

        static string B(bool value) { return value ? "true" : "false"; }

        static string Shot(string[] args)
        {
            if (args.Length < 2) throw new Exception("shot requires an output path");
            string path = args[1];
            Rectangle screen = Screen.PrimaryScreen.Bounds;
            int x = screen.X, y = screen.Y, w = screen.Width, h = screen.Height;
            if (args.Length >= 6)
            {
                x = int.Parse(args[2]);
                y = int.Parse(args[3]);
                w = int.Parse(args[4]);
                h = int.Parse(args[5]);
            }
            if (w <= 0 || h <= 0) throw new Exception("invalid capture region");
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (Bitmap bmp = new Bitmap(w, h))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
                }
                bmp.Save(path, ImageFormat.Png);
            }
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) throw new Exception("capture produced no bytes");
            return Ok("\"path\":" + Q(path) + ",\"width\":" + w + ",\"height\":" + h + ",\"bytes\":" + info.Length);
        }

        static string Move(string[] args)
        {
            if (args.Length < 3) throw new Exception("move requires x y");
            int x = int.Parse(args[1]);
            int y = int.Parse(args[2]);
            Native.MoveAbsolute(x, y);
            Thread.Sleep(80);
            Point p = Cursor.Position;
            if (Math.Abs(p.X - x) > 2 || Math.Abs(p.Y - y) > 2) throw new Exception("input injection blocked: cursor did not move to (" + x + "," + y + ")");
            return Ok("\"cursor\":{\"x\":" + p.X + ",\"y\":" + p.Y + "}");
        }

        static string Click(string[] args)
        {
            if (args.Length < 3) throw new Exception("click requires x y");
            int x = int.Parse(args[1]);
            int y = int.Parse(args[2]);
            string button = args.Length >= 4 ? args[3].ToLowerInvariant() : "left";
            int clicks = args.Length >= 5 ? int.Parse(args[4]) : 1;
            uint down, up;
            if (button == "right") { down = Native.RIGHTDOWN; up = Native.RIGHTUP; }
            else if (button == "middle") { down = Native.MIDDLEDOWN; up = Native.MIDDLEUP; }
            else { down = Native.LEFTDOWN; up = Native.LEFTUP; }
            Native.MoveAbsolute(x, y);
            Thread.Sleep(80);
            Point moved = Cursor.Position;
            if (Math.Abs(moved.X - x) > 2 || Math.Abs(moved.Y - y) > 2) throw new Exception("input injection blocked: cursor did not move to (" + x + "," + y + ")");
            for (int i = 0; i < clicks; i++)
            {
                Native.MouseButton(down);
                Thread.Sleep(40);
                Native.MouseButton(up);
                if (i + 1 < clicks) Thread.Sleep(90);
            }
            Point p = Cursor.Position;
            return Ok("\"click\":{\"x\":" + x + ",\"y\":" + y + ",\"button\":" + Q(button) + ",\"clicks\":" + clicks + ",\"cursor\":{\"x\":" + p.X + ",\"y\":" + p.Y + "}}");
        }

        static string TypeText(string[] args)
        {
            if (args.Length < 2) throw new Exception("type requires base64 text");
            string text = Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
            if (text.Length == 0) throw new Exception("empty text");
            bool copied = false;
            for (int attempt = 0; attempt < 5 && !copied; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    copied = true;
                }
                catch
                {
                    Thread.Sleep(80);
                }
            }
            if (!copied) throw new Exception("clipboard is busy");
            Thread.Sleep(150);
            Native.KeyDown(Native.VK_CONTROL);
            Native.KeyDown(Native.VK_V);
            Native.KeyUp(Native.VK_V);
            Native.KeyUp(Native.VK_CONTROL);
            return Ok("\"typed\":" + text.Length);
        }

        static string Key(string[] args)
        {
            if (args.Length < 2) throw new Exception("key requires a combo such as ctrl+shift+s");
            string combo = args[1];
            string[] parts = combo.Split('+');
            byte[] vks = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                vks[i] = Vk(parts[i].Trim().ToLowerInvariant());
            }
            for (int i = 0; i < vks.Length; i++)
            {
                Native.KeyDown(vks[i]);
                Thread.Sleep(25);
            }
            for (int i = vks.Length - 1; i >= 0; i--)
            {
                Native.KeyUp(vks[i]);
                Thread.Sleep(25);
            }
            return Ok("\"keys\":" + Q(combo));
        }

        static byte Vk(string token)
        {
            switch (token)
            {
                case "ctrl": return Native.VK_CONTROL;
                case "shift": return Native.VK_SHIFT;
                case "alt": return Native.VK_MENU;
                case "win": return Native.VK_LWIN;
                case "esc":
                case "escape": return 0x1B;
                case "enter":
                case "return": return 0x0D;
                case "tab": return 0x09;
                case "space": return 0x20;
                case "backspace":
                case "back": return 0x08;
                case "delete":
                case "del": return 0x2E;
                case "home": return 0x24;
                case "end": return 0x23;
                case "pageup": return 0x21;
                case "pagedown": return 0x22;
                case "insert": return 0x2D;
                case "up": return 0x26;
                case "down": return 0x28;
                case "left": return 0x25;
                case "right": return 0x27;
            }
            if (token.Length >= 2 && token[0] == 'f')
            {
                int n;
                if (int.TryParse(token.Substring(1), out n) && n >= 1 && n <= 12) return (byte)(0x6F + n);
            }
            if (token.Length == 1)
            {
                char c = char.ToUpperInvariant(token[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return (byte)c;
            }
            throw new Exception("unsupported key: " + token);
        }

        static string Scroll(string[] args)
        {
            if (args.Length < 2) throw new Exception("scroll requires delta");
            int delta = int.Parse(args[1]);
            Native.MouseWheel(delta);
            return Ok("\"scroll\":" + delta);
        }

        // ---------- 窗口 / 输入补齐 ----------

        static string WindowsList()
        {
            List<string> items = new List<string>();
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h)) return true;
                StringBuilder title = new StringBuilder(512);
                Native.GetWindowText(h, title, title.Capacity);
                string t = title.ToString();
                if (t.Length == 0) return true;
                int ex = Native.GetWindowLong(h, -20);
                if ((ex & 0x80) != 0) return true;
                Native.RECT rect;
                if (!Native.GetWindowRect(h, out rect)) return true;
                uint pid = 0;
                Native.GetWindowThreadProcessId(h, out pid);
                string proc = string.Empty;
                try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { }
                StringBuilder cls = new StringBuilder(256);
                Native.GetClassName(h, cls, cls.Capacity);
                bool blocked = Blacklist.Match(proc, t, cls.ToString());
                items.Add("{\"hwnd\":" + Q("0x" + h.ToInt64().ToString("X")) +
                    ",\"title\":" + Q(t) +
                    ",\"process\":" + Q(proc) +
                    ",\"class\":" + Q(cls.ToString()) +
                    ",\"rect\":[" + rect.Left + "," + rect.Top + "," + (rect.Right - rect.Left) + "," + (rect.Bottom - rect.Top) + "]" +
                    ",\"blacklisted\":" + B(blocked) + "}");
                return true;
            }, IntPtr.Zero);
            return Ok("\"count\":" + items.Count + ",\"windows\":[" + string.Join(",", items.ToArray()) + "]");
        }

        static string Activate(string[] args)
        {
            if (args.Length < 2) return Err("activate 需要窗口标题片段或 pid");
            string needle = args[1];
            int wantedPid = 0;
            int.TryParse(needle, out wantedPid);
            IntPtr found = IntPtr.Zero;
            string foundTitle = string.Empty;
            string foundProc = string.Empty;
            string foundClass = string.Empty;
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h)) return true;
                StringBuilder title = new StringBuilder(512);
                Native.GetWindowText(h, title, title.Capacity);
                string t = title.ToString();
                if (t.Length == 0) return true;
                uint pid = 0;
                Native.GetWindowThreadProcessId(h, out pid);
                bool match = wantedPid > 0 ? (int)pid == wantedPid : t.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!match) return true;
                int ex = Native.GetWindowLong(h, -20);
                if ((ex & 0x80) != 0) return true;
                found = h;
                foundTitle = t;
                try { foundProc = Process.GetProcessById((int)pid).ProcessName; } catch { }
                StringBuilder cls = new StringBuilder(256);
                Native.GetClassName(h, cls, cls.Capacity);
                foundClass = cls.ToString();
                return false;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero) return Err("没有找到匹配的窗口: " + needle);
            if (Blacklist.Match(foundProc, foundTitle, foundClass)) return Err("目标应用在黑名单中（" + foundTitle + "），拒绝操作");
            Native.ShowWindow(found, 9);
            uint dummy = 0;
            uint fgThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out dummy);
            uint targetThread = Native.GetWindowThreadProcessId(found, out dummy);
            bool attached = Native.AttachThreadInput(fgThread, targetThread, true);
            bool ok = Native.SetForegroundWindow(found);
            if (attached) Native.AttachThreadInput(fgThread, targetThread, false);
            Thread.Sleep(150);
            IntPtr now = Native.GetForegroundWindow();
            StringBuilder nowTitle = new StringBuilder(512);
            Native.GetWindowText(now, nowTitle, nowTitle.Capacity);
            return Ok("\"activated\":" + Q(foundTitle) + ",\"process\":" + Q(foundProc) + ",\"setForeground\":" + B(ok) + ",\"foreground\":" + Q(nowTitle.ToString()));
        }

        static string Drag(string[] args)
        {
            if (args.Length < 5) return Err("drag 需要 x1 y1 x2 y2");
            int x1 = int.Parse(args[1]);
            int y1 = int.Parse(args[2]);
            int x2 = int.Parse(args[3]);
            int y2 = int.Parse(args[4]);
            string button = args.Length >= 6 ? args[5].ToLowerInvariant() : "left";
            int steps = args.Length >= 7 ? int.Parse(args[6]) : 24;
            if (steps < 2) steps = 2;
            if (steps > 100) steps = 100;
            uint down, up;
            if (button == "right") { down = Native.RIGHTDOWN; up = Native.RIGHTUP; }
            else if (button == "middle") { down = Native.MIDDLEDOWN; up = Native.MIDDLEUP; }
            else { down = Native.LEFTDOWN; up = Native.LEFTUP; }
            Native.MoveAbsolute(x1, y1);
            Thread.Sleep(80);
            Point start = Cursor.Position;
            if (Math.Abs(start.X - x1) > 2 || Math.Abs(start.Y - y1) > 2) return Err("input injection blocked: cursor did not move");
            Native.MouseButton(down);
            Thread.Sleep(60);
            for (int i = 1; i <= steps; i++)
            {
                int x = x1 + (x2 - x1) * i / steps;
                int y = y1 + (y2 - y1) * i / steps;
                Native.MoveAbsolute(x, y);
                Thread.Sleep(15);
            }
            Native.MouseButton(up);
            Point end = Cursor.Position;
            return Ok("\"drag\":{\"from\":[" + x1 + "," + y1 + "],\"to\":[" + x2 + "," + y2 + "],\"button\":" + Q(button) + ",\"steps\":" + steps + ",\"cursor\":{\"x\":" + end.X + ",\"y\":" + end.Y + "}}");
        }

        static string ClipboardCommand(string[] args)
        {
            if (args.Length < 2) return Err("clipboard 需要 get 或 set");
            string op = args[1].ToLowerInvariant();
            if (op == "get")
            {
                string text = string.Empty;
                for (int i = 0; i < 5; i++)
                {
                    try { text = Clipboard.GetText(); break; }
                    catch { Thread.Sleep(80); }
                }
                return Ok("\"text\":" + Q(text));
            }
            if (op == "set")
            {
                if (args.Length < 3) return Err("clipboard set 需要 base64 文本");
                string text = Encoding.UTF8.GetString(Convert.FromBase64String(args[2]));
                return SetClipboardText(text);
            }
            if (op == "backup")
            {
                if (args.Length < 3) return Err("clipboard backup 需要文件路径");
                string path = args[2];
                string text = ReadClipboardText();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, text, new UTF8Encoding(false));
                return Ok("\"file\":" + Q(path) + ",\"chars\":" + text.Length);
            }
            if (op == "restore")
            {
                if (args.Length < 3) return Err("clipboard restore 需要文件路径");
                string path = args[2];
                if (!File.Exists(path)) return Err("备份文件不存在: " + path);
                string text = File.ReadAllText(path);
                return SetClipboardText(text);
            }
            return Err("clipboard 只支持 get / set / backup / restore");
        }

        static string ReadClipboardText()
        {
            string text = string.Empty;
            for (int i = 0; i < 5; i++)
            {
                try { text = Clipboard.GetText(); break; }
                catch { Thread.Sleep(80); }
            }
            return text;
        }

        static string SetClipboardText(string text)
        {
            bool ok = false;
            for (int i = 0; i < 5 && !ok; i++)
            {
                try { Clipboard.SetText(text); ok = true; }
                catch { Thread.Sleep(80); }
            }
            if (!ok) return Err("剪贴板被占用");
            return Ok("\"chars\":" + text.Length);
        }

        // ---------- UIA ----------

        static string UiaDump()
        {
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return Err("没有前台窗口");
            AutomationElement root;
            try { root = AutomationElement.FromHandle(h); }
            catch (Exception ex) { return Err("无法读取前台窗口的 UIA 树：" + ex.Message); }
            if (root == null) return Err("无法读取前台窗口的 UIA 树");
            List<string> nodes = new List<string>();
            int count = 0;
            DateTime deadline = DateTime.UtcNow.AddSeconds(2.5);
            WalkUia(root, "0", 0, nodes, ref count, 400, 12, deadline);
            uint pid = 0;
            Native.GetWindowThreadProcessId(h, out pid);
            string title = "";
            try { title = root.Current.Name ?? ""; } catch { }
            string body = "\"window\":{\"title\":" + Q(title) + ",\"pid\":" + pid + "}" +
                ",\"count\":" + count +
                ",\"truncated\":" + B(count >= 400) +
                ",\"nodes\":[" + string.Join(",", nodes.ToArray()) + "]";
            return Ok(body);
        }

        static void WalkUia(AutomationElement element, string refPath, int depth, List<string> nodes, ref int count, int maxNodes, int maxDepth, DateTime deadline)
        {
            if (count >= maxNodes || DateTime.UtcNow > deadline) return;
            nodes.Add(DescribeUia(element, refPath));
            count++;
            if (depth >= maxDepth) return;
            AutomationElement child = TreeWalker.ControlViewWalker.GetFirstChild(element);
            int index = 0;
            while (child != null)
            {
                if (count >= maxNodes || DateTime.UtcNow > deadline) return;
                WalkUia(child, refPath + "." + index, depth + 1, nodes, ref count, maxNodes, maxDepth, deadline);
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                index++;
            }
        }

        static string DescribeUia(AutomationElement element, string refPath)
        {
            string name = "";
            string type = "";
            string automationId = "";
            bool enabled = false;
            bool offscreen = false;
            int x = 0, y = 0, w = 0, h = 0;
            try
            {
                AutomationElement.AutomationElementInformation current = element.Current;
                name = current.Name ?? "";
                type = current.ControlType != null ? current.ControlType.ProgrammaticName ?? "" : "";
                if (type.StartsWith("ControlType.")) type = type.Substring("ControlType.".Length);
                automationId = current.AutomationId ?? "";
                enabled = current.IsEnabled;
                offscreen = current.IsOffscreen;
                System.Windows.Rect rect = current.BoundingRectangle;
                x = (int)Math.Round(rect.X);
                y = (int)Math.Round(rect.Y);
                w = (int)Math.Round(rect.Width);
                h = (int)Math.Round(rect.Height);
            }
            catch { }
            List<string> patterns = new List<string>();
            object pattern;
            try
            {
                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) patterns.Add("invoke");
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) patterns.Add("value");
                if (element.TryGetCurrentPattern(TogglePattern.Pattern, out pattern)) patterns.Add("toggle");
                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) patterns.Add("select");
                if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern)) patterns.Add("expandcollapse");
                if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out pattern)) patterns.Add("scrollitem");
            }
            catch { }
            List<string> quotedPatterns = new List<string>();
            foreach (string p in patterns) quotedPatterns.Add(Q(p));
            return "{\"ref\":" + Q(refPath) +
                ",\"name\":" + Q(name) +
                ",\"type\":" + Q(type) +
                ",\"automationId\":" + Q(automationId) +
                ",\"rect\":[" + x + "," + y + "," + w + "," + h + "]" +
                ",\"enabled\":" + B(enabled) +
                ",\"offscreen\":" + B(offscreen) +
                ",\"patterns\":[" + string.Join(",", quotedPatterns.ToArray()) + "]}";
        }

        static AutomationElement ResolveRefPath(AutomationElement root, string refPath)
        {
            if (refPath == "0") return root;
            string[] parts = refPath.Split('.');
            if (parts.Length < 2 || parts[0] != "0") return null;
            AutomationElement element = root;
            for (int i = 1; i < parts.Length; i++)
            {
                int index;
                if (!int.TryParse(parts[i], out index)) return null;
                AutomationElement child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                int n = 0;
                while (child != null && n < index)
                {
                    child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                    n++;
                }
                if (child == null) return null;
                element = child;
            }
            return element;
        }

        static bool Supports(AutomationElement element, string action)
        {
            object pattern;
            try
            {
                switch (action)
                {
                    case "invoke": return element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern);
                    case "setvalue": return element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern);
                    case "toggle": return element.TryGetCurrentPattern(TogglePattern.Pattern, out pattern);
                    case "select": return element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern);
                    case "expand":
                    case "collapse": return element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern);
                    case "focus": return true;
                }
            }
            catch { }
            return false;
        }

        static AutomationElement FindByName(AutomationElement root, string name, string action)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            Queue<AutomationElement> queue = new Queue<AutomationElement>();
            queue.Enqueue(root);
            int visited = 0;
            while (queue.Count > 0 && visited < 600 && DateTime.UtcNow < deadline)
            {
                AutomationElement current = queue.Dequeue();
                visited++;
                string currentName = "";
                bool enabled = false;
                try
                {
                    currentName = current.Current.Name ?? "";
                    enabled = current.Current.IsEnabled;
                }
                catch { }
                if (enabled && currentName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 && Supports(current, action))
                {
                    return current;
                }
                AutomationElement child = TreeWalker.ControlViewWalker.GetFirstChild(current);
                while (child != null)
                {
                    queue.Enqueue(child);
                    child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                }
            }
            return null;
        }

        static string UiaAct(string[] args)
        {
            if (args.Length < 3) return Err("uia_act 需要 action 和 ref");
            string action = args[1].ToLowerInvariant();
            string refPath = args[2];
            string valueB64 = args.Length >= 4 ? args[3] : "";
            string nameQuery = "";
            if (args.Length >= 5 && args[4].Length > 0)
            {
                try { nameQuery = Encoding.UTF8.GetString(Convert.FromBase64String(args[4])); }
                catch { }
            }
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return Err("没有前台窗口");
            AutomationElement root;
            try { root = AutomationElement.FromHandle(h); }
            catch (Exception ex) { return Err("无法读取前台窗口的 UIA 树：" + ex.Message); }
            if (root == null) return Err("无法读取前台窗口的 UIA 树");
            AutomationElement element = null;
            if (nameQuery.Length > 0) element = FindByName(root, nameQuery, action);
            if (element == null && refPath != "" && refPath != "-") element = ResolveRefPath(root, refPath);
            if (element == null)
            {
                Thread.Sleep(300);
                if (nameQuery.Length > 0) element = FindByName(root, nameQuery, action);
                if (element == null && refPath != "" && refPath != "-") element = ResolveRefPath(root, refPath);
            }
            if (element == null) return Err("元素不存在（界面可能已变化，请重新 desk_uia，或改用 name 定位）");
            string name = "";
            try { name = element.Current.Name ?? ""; } catch { }
            object pattern;
            try
            {
                switch (action)
                {
                    case "invoke":
                        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) return Err("该元素不支持 invoke");
                        ((InvokePattern)pattern).Invoke();
                        break;
                    case "setvalue":
                        if (valueB64.Length == 0) return Err("setvalue 需要 base64 文本");
                        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) return Err("该元素不支持 setvalue");
                        string text = Encoding.UTF8.GetString(Convert.FromBase64String(valueB64));
                        ((ValuePattern)pattern).SetValue(text);
                        break;
                    case "toggle":
                        if (!element.TryGetCurrentPattern(TogglePattern.Pattern, out pattern)) return Err("该元素不支持 toggle");
                        ((TogglePattern)pattern).Toggle();
                        break;
                    case "select":
                        if (!element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) return Err("该元素不支持 select");
                        ((SelectionItemPattern)pattern).Select();
                        break;
                    case "expand":
                    case "collapse":
                        if (!element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern)) return Err("该元素不支持 expand/collapse");
                        if (action == "expand") ((ExpandCollapsePattern)pattern).Expand();
                        else ((ExpandCollapsePattern)pattern).Collapse();
                        break;
                    case "focus":
                        element.SetFocus();
                        break;
                    default:
                        return Err("unknown action: " + action);
                }
            }
            catch (Exception ex)
            {
                return Err("UIA 动作失败：" + ex.Message);
            }
            return Ok("\"action\":" + Q(action) + ",\"ref\":" + Q(refPath) + ",\"name\":" + Q(name));
        }

        // ---------- 耳朵 ----------

        static string AudioInfo()
        {
            List<string> items = new List<string>();
            try
            {
                foreach (RecognizerInfo info in SpeechRecognitionEngine.InstalledRecognizers())
                {
                    items.Add("{\"id\":" + Q(info.Id) + ",\"culture\":" + Q(info.Culture.Name) + ",\"name\":" + Q(info.Name) + "}");
                }
            }
            catch { }
            bool mic = false;
            try
            {
                using (SpeechRecognitionEngine engine = new SpeechRecognitionEngine())
                {
                    engine.SetInputToDefaultAudioDevice();
                    mic = true;
                }
            }
            catch { }
            return Ok("\"recognizers\":[" + string.Join(",", items.ToArray()) + "],\"microphone\":" + B(mic));
        }

        static string Hear(string[] args)
        {
            int seconds = 5;
            if (args.Length >= 2) int.TryParse(args[1], out seconds);
            if (seconds < 1) seconds = 1;
            if (seconds > 30) seconds = 30;
            SpeechRecognitionEngine engine = null;
            try
            {
                engine = new SpeechRecognitionEngine(new CultureInfo("zh-CN"));
                engine.LoadGrammar(new DictationGrammar());
                engine.SetInputToDefaultAudioDevice();
                List<string> texts = new List<string>();
                DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
                while (true)
                {
                    double remaining = (deadline - DateTime.UtcNow).TotalSeconds;
                    if (remaining < 0.5) break;
                    RecognitionResult result = null;
                    try { result = engine.Recognize(TimeSpan.FromSeconds(remaining)); }
                    catch (TimeoutException) { break; }
                    if (result != null && result.Text.Length > 0) texts.Add(result.Text);
                }
                return Ok("\"seconds\":" + seconds + ",\"text\":" + Q(string.Join(" ", texts.ToArray())) + ",\"chunks\":" + texts.Count);
            }
            catch (Exception ex)
            {
                return Err("听写失败：" + ex.Message);
            }
            finally
            {
                if (engine != null) engine.Dispose();
            }
        }

        static string Transcribe(string[] args)
        {
            if (args.Length < 2) return Err("transcribe 需要 WAV 文件路径");
            string path = args[1];
            if (!File.Exists(path)) return Err("文件不存在: " + path);
            if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return Err("目前只支持 WAV；其他格式请先转成 WAV");
            SpeechRecognitionEngine engine = null;
            try
            {
                engine = new SpeechRecognitionEngine(new CultureInfo("zh-CN"));
                engine.LoadGrammar(new DictationGrammar());
                engine.SetInputToWaveFile(path);
                List<string> texts = new List<string>();
                while (true)
                {
                    RecognitionResult result = null;
                    try { result = engine.Recognize(TimeSpan.FromSeconds(3)); }
                    catch (TimeoutException) { break; }
                    catch (InvalidOperationException) { break; }
                    if (result == null) break;
                    if (result.Text.Length > 0) texts.Add(result.Text);
                }
                return Ok("\"file\":" + Q(path) + ",\"text\":" + Q(string.Join(" ", texts.ToArray())) + ",\"chunks\":" + texts.Count);
            }
            catch (Exception ex)
            {
                return Err("转写失败：" + ex.Message);
            }
            finally
            {
                if (engine != null) engine.Dispose();
            }
        }

        static string SelfTest()
        {
            Rectangle b = Screen.PrimaryScreen.Bounds;
            string tmp = Path.Combine(Path.GetTempPath(), "deskhand-selftest.png");
            using (Bitmap bmp = new Bitmap(Math.Min(200, b.Width), Math.Min(120, b.Height)))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(b.X, b.Y, 0, 0, bmp.Size);
                }
                bmp.Save(tmp, ImageFormat.Png);
            }
            FileInfo f = new FileInfo(tmp);
            bool shotOk = f.Exists && f.Length > 0;
            try { File.Delete(tmp); } catch { }
            bool clipOk = false;
            string previous = null;
            try { previous = Clipboard.GetText(); } catch { }
            try
            {
                Clipboard.SetText("deskhand-selftest");
                clipOk = Clipboard.GetText() == "deskhand-selftest";
            }
            catch { }
            try
            {
                if (previous == null) Clipboard.Clear();
                else Clipboard.SetText(previous);
            }
            catch { }
            string body = "\"version\":" + Q(Version) +
                ",\"checks\":{\"screen\":" + B(b.Width > 0 && b.Height > 0) +
                ",\"capture\":" + B(shotOk) +
                ",\"clipboard\":" + B(clipOk) + "}" +
                ",\"screen\":{\"width\":" + b.Width + ",\"height\":" + b.Height + "}";
            if (!shotOk || !clipOk) return "{\"ok\":false," + body + "}";
            return Ok(body);
        }

        // ---------- broker ----------

        static string BuildPipeSddl()
        {
            // Everyone 读写；再给一个 Low 完整性标签，允许沙箱内的插件客户端连接。
            return "D:(A;;GA;;;WD)S:(ML;;NW;;;LW)";
        }

        static PipeSecurity CreatePipeSecurity()
        {
            PipeSecurity security = new PipeSecurity();
            try
            {
                security.SetSecurityDescriptorSddlForm(BuildPipeSddl());
            }
            catch
            {
                // 没有 SeSecurityPrivilege 时退化为只设 DACL。
                security = new PipeSecurity();
                SecurityIdentifier everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                security.AddAccessRule(new PipeAccessRule(everyone, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            }
            return security;
        }

        static string BrokerDataDir(string exe)
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe), "..", "data"));
        }

        static void WriteBrokerInfo(string dataDir, int port, string token)
        {
            Directory.CreateDirectory(dataDir);
            string json = "{\"port\":" + port + ",\"token\":\"" + token + "\",\"pid\":" + Process.GetCurrentProcess().Id + "}";
            string target = Path.Combine(dataDir, "broker.json");
            string temp = Path.Combine(dataDir, "broker.json.tmp");
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Copy(temp, target, true);
            File.Delete(temp);
        }

        static int Serve(string[] args)
        {
            Native.FreeConsole();
            bool created = false;
            using (Mutex mutex = new Mutex(true, "deskhand-broker", out created))
            {
                if (!created) return 0;
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                string dataDir = args.Length >= 2 ? Path.GetFullPath(args[1]) : BrokerDataDir(exe);
                string token = Guid.NewGuid().ToString("N");
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Blacklist.EnsureDefault(dataDir);
                Overlay.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                WriteBrokerInfo(dataDir, port, token);
                Log(dataDir, "broker serve pid=" + Process.GetCurrentProcess().Id + " port=" + port + " integrity=" + Native.IntegrityLevel());
                while (true)
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    {
                        client.ReceiveTimeout = 120000;
                        using (NetworkStream stream = client.GetStream())
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (line.Trim().Length == 0) continue;
                                string[] parts = line.Split('\t');
                                string response;
                                if (parts.Length < 2 || parts[0] != token)
                                {
                                    response = Err("unauthorized");
                                }
                                else
                                {
                                    string[] command = new string[parts.Length - 1];
                                    Array.Copy(parts, 1, command, 0, parts.Length - 1);
                                    try
                                    {
                                        if (command.Length > 0)
                                        {
                                            string verb = command[0].ToLowerInvariant();
                                            if (verb != "version" && verb != "selftest" && verb != "state")
                                            {
                                                Overlay.Poke();
                                            }
                                        }
                                        response = Execute(command);
                                    }
                                    catch (Exception ex)
                                    {
                                        response = Err(ex.Message);
                                    }
                                }
                                writer.WriteLine(response);
                                writer.Flush();
                            }
                        }
                    }
                }
            }
        }

        static bool DshRunning()
        {
            try
            {
                Process[] found = Process.GetProcessesByName(DshProcess);
                return found.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        static void Log(string dataDir, string message)
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                File.AppendAllText(Path.Combine(dataDir, "watch.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        static Process StartBroker(string exe, string dataDir)
        {
            ProcessStartInfo info = new ProcessStartInfo(exe, "serve \"" + dataDir + "\"");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            return Process.Start(info);
        }

        static int TryExitCode(Process process)
        {
            try { return process.ExitCode; } catch { return -1; }
        }

        static int Watch()
        {
            Native.FreeConsole();
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string dataDir = BrokerDataDir(exe);
            Log(dataDir, "watch boot pid=" + Process.GetCurrentProcess().Id + " integrity=" + Native.IntegrityLevel());
            bool created = false;
            using (Mutex mutex = new Mutex(true, "deskhand-watch", out created))
            {
                if (!created)
                {
                    Log(dataDir, "watch exit: another watcher is running");
                    return 0;
                }
                Log(dataDir, "watch active pid=" + Process.GetCurrentProcess().Id);
                Process broker = null;
                while (!watchStop)
                {
                    try
                    {
                        bool running = DshRunning();
                        if (running && (broker == null || broker.HasExited))
                        {
                            if (broker != null) Log(dataDir, "broker exited code=" + TryExitCode(broker));
                            broker = StartBroker(exe, dataDir);
                            Log(dataDir, "broker start pid=" + (broker == null ? "null" : broker.Id.ToString()));
                        }
                        else if (!running && broker != null && !broker.HasExited)
                        {
                            Log(dataDir, "dsh gone, stop broker pid=" + broker.Id);
                            broker.Kill();
                            broker.WaitForExit(2000);
                            broker = null;
                            try { File.Delete(Path.Combine(dataDir, "broker.json")); } catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(dataDir, "watch error: " + ex.Message);
                    }
                    Thread.Sleep(2000);
                }
                if (broker != null && !broker.HasExited)
                {
                    try { broker.Kill(); } catch { }
                }
                try { File.Delete(Path.Combine(dataDir, "broker.json")); } catch { }
                return 0;
            }
        }
    }

    static class Native
    {
        public const uint LEFT_DOWN = 0x0002;
        public const uint LEFT_UP = 0x0004;
        public const uint RIGHT_DOWN = 0x0008;
        public const uint RIGHT_UP = 0x0010;
        public const uint MIDDLE_DOWN = 0x0020;
        public const uint MIDDLE_UP = 0x0040;
        public const uint WHEEL = 0x0800;
        public const uint MOVE = 0x0001;
        public const uint ABSOLUTE = 0x8000;
        public const uint KEYUP = 0x0002;
        public const uint LEFTDOWN = LEFT_DOWN;
        public const uint LEFTUP = LEFT_UP;
        public const uint RIGHTDOWN = RIGHT_DOWN;
        public const uint RIGHTUP = RIGHT_UP;
        public const uint MIDDLEDOWN = MIDDLE_DOWN;
        public const uint MIDDLEUP = MIDDLE_UP;
        public const byte VK_CONTROL = 0x11;
        public const byte VK_SHIFT = 0x10;
        public const byte VK_MENU = 0x12;
        public const byte VK_LWIN = 0x5B;
        public const byte VK_V = 0x56;

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder text, int count);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr hObj, int index, StringBuilder info, int length, out int needed);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("ntdll.dll")] public static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref PROCESS_BASIC_INFORMATION info, int infoLength, out int returnLength);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int infoLength, out int returnLength);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr text);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll")] public static extern bool FreeConsole();

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2a;
            public IntPtr Reserved2b;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        public static string IntegrityLevel()
        {
            return IntegrityOf(GetCurrentProcess());
        }

        public static string IntegrityOf(IntPtr process)
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(process, 0x0008, out token)) return "unknown";
                int size = 64;
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    int needed = 0;
                    if (!GetTokenInformation(token, 25, buffer, size, out needed)) return "unknown";
                    IntPtr sid = Marshal.ReadIntPtr(buffer, 0);
                    IntPtr text = IntPtr.Zero;
                    try
                    {
                        if (!ConvertSidToStringSid(sid, out text)) return "unknown";
                        return Marshal.PtrToStringUni(text);
                    }
                    finally
                    {
                        if (text != IntPtr.Zero) LocalFree(text);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        public static void ParentInfo(out int pid, out string integrity)
        {
            pid = 0;
            integrity = "unknown";
            IntPtr self = GetCurrentProcess();
            PROCESS_BASIC_INFORMATION info = new PROCESS_BASIC_INFORMATION();
            int returned = 0;
            int status = NtQueryInformationProcess(self, 0, ref info, Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)), out returned);
            if (status != 0) return;
            pid = (int)info.InheritedFromUniqueProcessId.ToInt64();
            if (pid <= 0) return;
            IntPtr parent = OpenProcess(0x1000, false, pid);
            if (parent == IntPtr.Zero)
            {
                integrity = "denied";
                return;
            }
            try
            {
                integrity = IntegrityOf(parent);
            }
            finally
            {
                CloseHandle(parent);
            }
        }

        static int absX(int x)
        {
            int width = Screen.PrimaryScreen.Bounds.Width;
            return (int)((long)x * 65535L / (width - 1));
        }

        static int absY(int y)
        {
            int height = Screen.PrimaryScreen.Bounds.Height;
            return (int)((long)y * 65535L / (height - 1));
        }

        public static bool MoveAbsolute(int x, int y)
        {
            INPUT input = new INPUT();
            input.type = 0;
            input.u.mi.dx = absX(x);
            input.u.mi.dy = absY(y);
            input.u.mi.dwFlags = MOVE | ABSOLUTE;
            INPUT[] inputs = new INPUT[1];
            inputs[0] = input;
            uint sent = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            return sent == 1;
        }

        public static bool MouseButton(uint flags)
        {
            INPUT input = new INPUT();
            input.type = 0;
            input.u.mi.dwFlags = flags;
            INPUT[] inputs = new INPUT[1];
            inputs[0] = input;
            return SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT))) == 1;
        }

        public static bool MouseWheel(int delta)
        {
            INPUT input = new INPUT();
            input.type = 0;
            input.u.mi.dwFlags = WHEEL;
            input.u.mi.mouseData = (uint)delta;
            INPUT[] inputs = new INPUT[1];
            inputs[0] = input;
            return SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT))) == 1;
        }

        public static bool KeyDown(byte vk)
        {
            INPUT input = new INPUT();
            input.type = 1;
            input.u.ki.wVk = vk;
            INPUT[] inputs = new INPUT[1];
            inputs[0] = input;
            return SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT))) == 1;
        }

        public static bool KeyUp(byte vk)
        {
            INPUT input = new INPUT();
            input.type = 1;
            input.u.ki.wVk = vk;
            input.u.ki.dwFlags = KEYUP;
            INPUT[] inputs = new INPUT[1];
            inputs[0] = input;
            return SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT))) == 1;
        }

        public static string DesktopName()
        {
            IntPtr desktop = GetThreadDesktop(GetCurrentThreadId());
            if (desktop == IntPtr.Zero) return "unknown";
            StringBuilder sb = new StringBuilder(256);
            int needed = 0;
            if (!GetUserObjectInformation(desktop, 2, sb, sb.Capacity, out needed)) return "unknown";
            return sb.ToString();
        }
    }

    static class Overlay
    {
        static readonly object Gate = new object();
        static OverlayForm form;
        static System.Windows.Forms.Timer hideTimer;
        static DateTime lastPoke = DateTime.MinValue;

        public static void Start()
        {
            Thread thread = new Thread(Run);
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        static void Run()
        {
            form = new OverlayForm();
            hideTimer = new System.Windows.Forms.Timer();
            hideTimer.Interval = 1000;
            hideTimer.Tick += (sender, args) =>
            {
                bool stale;
                lock (Gate) { stale = (DateTime.Now - lastPoke).TotalSeconds > 5; }
                if (stale) form.Hide();
            };
            form.Shown += (sender, args) => { form.Hide(); hideTimer.Start(); };
            Application.Run(form);
        }

        public static void Poke()
        {
            lock (Gate) { lastPoke = DateTime.Now; }
            OverlayForm current = form;
            if (current == null || current.IsDisposed) return;
            try
            {
                current.BeginInvoke((MethodInvoker)(() =>
                {
                    current.Show();
                    current.BringToFront();
                }));
            }
            catch { }
        }
    }

    class OverlayForm : Form
    {
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x20;        // WS_EX_TRANSPARENT（鼠标穿透）
                cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE（不抢焦点）
                cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW（不进 Alt-Tab）
                return cp;
            }
        }

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(30, 41, 59);
            Opacity = 0.92;
            Size = new Size(520, 88);
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            Location = new Point(bounds.Right - Width - 40, bounds.Bottom - Height - 120);
            Label label = new Label();
            label.Text = "●  agent 正在使用桌面";
            label.ForeColor = Color.White;
            label.Font = new Font("Microsoft YaHei", 13f, FontStyle.Regular);
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(label);
        }
    }

    static class Blacklist
    {
        static readonly object Gate = new object();
        static string filePath;
        static string[] cached = new string[0];
        static DateTime lastRead = DateTime.MinValue;

        static string FilePath()
        {
            if (filePath == null)
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                filePath = Path.Combine(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe), "..", "data")), "blacklist.json");
            }
            return filePath;
        }

        public static void EnsureDefault(string dataDir)
        {
            try
            {
                string path = Path.Combine(dataDir, "blacklist.json");
                if (!File.Exists(path))
                {
                    File.WriteAllText(path,
                        "{\"processes\":[\"RVLSession\"],\"titles\":[\"独享桌面\"],\"classes\":[]}",
                        new UTF8Encoding(false));
                }
            }
            catch { }
        }

        static string[] Patterns()
        {
            lock (Gate)
            {
                if ((DateTime.UtcNow - lastRead).TotalSeconds < 5) return cached;
                lastRead = DateTime.UtcNow;
                try
                {
                    string path = FilePath();
                    if (!File.Exists(path)) { cached = new string[0]; return cached; }
                    string text = File.ReadAllText(path);
                    System.Collections.Generic.List<string> items = new System.Collections.Generic.List<string>();
                    int i = 0;
                    while ((i = text.IndexOf('"', i)) >= 0)
                    {
                        int j = text.IndexOf('"', i + 1);
                        if (j < 0) break;
                        items.Add(text.Substring(i + 1, j - i - 1));
                        i = j + 1;
                    }
                    System.Collections.Generic.List<string> patterns = new System.Collections.Generic.List<string>();
                    foreach (string item in items)
                    {
                        if (item == "processes" || item == "titles" || item == "classes") continue;
                        if (item.Trim().Length == 0) continue;
                        patterns.Add(item);
                    }
                    cached = patterns.ToArray();
                }
                catch { cached = new string[0]; }
                return cached;
            }
        }

        public static bool Match(string proc, string title, string cls)
        {
            string[] patterns = Patterns();
            for (int i = 0; i < patterns.Length; i++)
            {
                string p = patterns[i];
                if (proc.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cls.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        public static string Violation()
        {
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            StringBuilder title = new StringBuilder(512);
            Native.GetWindowText(h, title, title.Capacity);
            StringBuilder cls = new StringBuilder(256);
            Native.GetClassName(h, cls, cls.Capacity);
            uint pid = 0;
            Native.GetWindowThreadProcessId(h, out pid);
            string proc = string.Empty;
            try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { }
            if (Match(proc, title.ToString(), cls.ToString()))
            {
                string label = title.ToString().Length > 0 ? title.ToString() : proc;
                return "前台应用在黑名单中（" + label + "），拒绝操作";
            }
            return null;
        }
    }
}
